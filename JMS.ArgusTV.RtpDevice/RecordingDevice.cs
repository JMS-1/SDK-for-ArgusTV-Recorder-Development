using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using JMS.DVB;
using JMS.DVB.TS;


namespace JMS.ArgusTV.RtpDevice
{
    /// <summary>
    /// Erlaubt das Anlegen von Geräten.
    /// </summary>
    public class DeviceFactory : ArgusTV.IRecordingDeviceFactory
    {
        /// <summary>
        /// Führt einmalige Initialisierungen aus
        /// </summary>
        static DeviceFactory()
        {
            // Dynamic runtime loader
            RunTimeLoader.Startup();
        }

        /// <summary>
        /// Erstellt ein neues Gerät.
        /// </summary>
        /// <param name="name">Der eindeutige Name des Gerätes.</param>
        /// <param name="priority">Die Priorität des Gerätes - kleiner Werte sind besser.</param>
        /// <returns>Das gewünschte Gerät.</returns>
        public ArgusTV.RecordingDevice CreateDevice( string name, int priority )
        {
            // Forward
            return new RecordingDevice( name, priority );
        }
    }

    /// <summary>
    /// Ein einzelnes Aufzeichnungsgerät.
    /// </summary>
    internal class RecordingDevice : RecordingDevice<object, bool>
    {
        /// <summary>
        /// Über diesen Kanal empfangen wird die Daten.
        /// </summary>
        private Socket m_socket;

        /// <summary>
        /// Die Dateien, in die wir die Aufzeichnung schreiben.
        /// </summary>
        private Dictionary<Guid, DoubleBufferedFile> m_targets = new Dictionary<Guid, DoubleBufferedFile>();

        /// <summary>
        /// Die Verarbeitung aller Daten.
        /// </summary>
        private event Action<byte[], int, int> m_sink;

        /// <summary>
        /// Erstellt eine neue Simulation.
        /// </summary>
        /// <param name="name">Der Name des Gerätes.</param>
        /// <param name="priority">Die Priorität des Gerätes - kleiner Werte sind besser.</param>
        public RecordingDevice( string name, int priority )
            : base( name, priority )
        {
        }

        /// <summary>
        /// Prüft, ob zwei Quellen zum gleichen Zeitpunkt aufgezeichnet werden können.
        /// </summary>
        /// <param name="first">Eine Quelle.</param>
        /// <param name="second">Eine andere Quelle.</param>
        /// <returns>Gesetzt, wenn eine gleichzeitige Aufzeichnung möglich ist.</returns>
        protected override bool CanRecordedInParallel( object first, object second )
        {
            // Never
            return false;
        }

        /// <summary>
        /// Ermittelt zu einem Text den Sender.
        /// </summary>
        /// <param name="channelIdentification">Die Identifikation des Senders.</param>
        /// <returns>Die Senderinformationen.</returns>
        protected override object[] OnResolve( string channelIdentification )
        {
            // Forward
            return new object[] { channelIdentification };
        }

        /// <summary>
        /// Ermittelt zu einer asynchronen Operation das Ergebnis.
        /// </summary>
        /// <typeparam name="TResult">Die Art des Ergebnisses.</typeparam>
        /// <param name="control">Die Steuerinformation der Operation.</param>
        /// <returns>Das gewünschte Ergebnis.</returns>
        protected override TResult GetResult<TResult>( bool control )
        {
            // Forward
            return default( TResult );
        }

        /// <summary>
        /// Wird aufgerufen, um das zugehörige Gerät zu reservieren.
        /// </summary>
        protected override void ReserveDevice()
        {
        }

        /// <summary>
        /// Gibt die Nutzung des Gerätes endültig auf.
        /// </summary>
        protected override void ReleaseDevice()
        {
            // Forward
            ReleaseDevice( false );
        }

        /// <summary>
        /// Gibt die Nutzung des Gerätes endültig auf.
        /// </summary>
        /// <param name="dispose">Gesetzt, wenn es sich um die endgültige Freigabe handelt.</param>
        private void ReleaseDevice( bool dispose )
        {
            // Normal operation
            if (!dispose)
            {
                // Synchronizer
                var waitEnd = new object();

                // Synchronize
                lock (waitEnd)
                {
                    // Enqueue command
                    Enqueue( () => { ReleaseDevice( true ); return true; }, ( object result ) => { lock (waitEnd) Monitor.Pulse( waitEnd ); } );

                    // Until we are done - better synchronize here
                    Monitor.Wait( waitEnd );
                }
            }

            // Safe cleanup
            try
            {
                // Get rid of socket
                using (var socket = m_socket)
                {
                    // Forget
                    m_socket = null;

                    // Proper termination
                    if (socket != null)
                        socket.Close();
                }
            }
            catch (Exception e)
            {
                // Just report
                Trace.TraceError( e.Message );
            }
        }

        /// <summary>
        /// Wählt den Empfang einer Quelle an.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        protected override void Tune( object source )
        {
            // Detach
            Enqueue( () =>
                {
                    // Attach to device - fixed to IPv6 for now
                    var socket =
                        new Socket( AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp )
                        {
                            ReceiveTimeout = (int) TimeSpan.FromHours( 1 ).TotalMilliseconds,
                            Blocking = true,
                        };

                    // Requires cleanup on error
                    try
                    {
                        // Configure
                        socket.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 1000000 );

                        // Bind it - fixed to a dedicated port for now
                        socket.Bind( new IPEndPoint( IPAddress.IPv6Any, 35677 ) );

                        // Reset processor
                        m_sink = null;

                        // Our buffer
                        var buffer = new byte[10000];

                        // Starter
                        AsyncCallback whenDone = null;
                        Action asyncRead = () => socket.BeginReceive( buffer, 0, buffer.Length, SocketFlags.None, whenDone, null );

                        // Define packet processor
                        whenDone =
                            result =>
                            {
                                // Be safe
                                try
                                {
                                    // Finish
                                    var bytes = socket.EndReceive( result );

                                    // Try to dispatch
                                    lock (m_targets)
                                        RtpPacketDispatcher.DispatchTSPayload( buffer, 0, bytes, m_sink );

                                    // Fire next
                                    asyncRead();
                                }
                                catch (ObjectDisposedException)
                                {
                                }
                                catch (Exception e)
                                {
                                    // Report
                                    Trace.TraceError( e.Message );
                                }
                            };

                        // Start first read
                        asyncRead();

                        // Replace our current socket with the new one
                        using (m_socket)
                            m_socket = socket;

                        // Did it
                        return true;
                    }
                    catch (Exception)
                    {
                        // Cleanup
                        socket.Dispose();

                        // Forward
                        throw;
                    }
                } );
        }

        /// <summary>
        /// Beginnt mit der Aufzeichnung einer Quelle.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        /// <param name="recordingPath">Der volle Pfad zur Aufzeichnungsdatei.</param>
        protected override void BeginRecording( object source, Guid streamIdentifier, string recordingPath )
        {
            // Asynchronous processing
            Enqueue( () =>
            {
                // Create a new buffer
                var buffer = new DoubleBufferedFile( recordingPath, 1000000 );

                // Add synchronized
                lock (m_targets)
                {
                    // Remember
                    m_targets.Add( streamIdentifier, buffer );

                    // Make sure we receive
                    m_sink += buffer.Write;
                }

                // Done
                return true;
            } );
        }

        /// <summary>
        /// Beendet die Aufzeichnung einer Quelle.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        /// <param name="recordingPath">Der volle Pfad zur Aufzeichnungsdatei.</param>
        protected override void EndRecording( object source, Guid streamIdentifier, string recordingPath )
        {
            // Asynchronous processing
            Enqueue( () =>
            {
                // Process synchronized
                lock (m_targets)
                {
                    // See if we know it
                    DoubleBufferedFile buffer;
                    if (m_targets.TryGetValue( streamIdentifier, out buffer ))
                        using (buffer)
                        {
                            // Remove from manager
                            m_targets.Remove( streamIdentifier );

                            // Unregister
                            m_sink -= buffer.Write;
                        }

                    // Done
                    return true;
                }
            } );
        }

        /// <summary>
        /// Beendet die Nutzung dieses Gerätes endgültig.
        /// </summary>
        protected override void OnDispose()
        {
            // Forward
            ReleaseDevice( true );

            // Release all streams
            foreach (var file in m_targets.Values)
                file.Dispose();

            // Done
            m_targets.Clear();
        }
    }
}