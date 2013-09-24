using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using JMS.DVB;
using JMS.DVB.Algorithms.Scheduler;
using JMS.DVB.CardServer;


namespace JMS.ArgusTV.DVBNETRecorder
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
        /// Liest eine Einstellung aus.
        /// </summary>
        /// <param name="profile">Ein Geräteprofil.</param>
        /// <param name="settingName">Der Name der Einstellung.</param>
        /// <param name="settingDefault">Die Voreinstellung der Einstellung.</param>
        /// <returns>Der aktuelle Wert, gegebenenfalls die Voreinstellung.</returns>
        private static uint ReadSetting( Profile profile, string settingName, uint settingDefault )
        {
            // Check value
            var settings = profile.GetParameter( settingName );
            if (string.IsNullOrEmpty( settings ))
                return settingDefault;

            // Check value
            uint value;
            if (uint.TryParse( settings, out value ))
                return value;
            else
                return settingDefault;
        }

        /// <summary>
        /// Erstellt ein neues Gerät.
        /// </summary>
        /// <param name="name">Der eindeutige Name des Gerätes.</param>
        /// <param name="priority">Die Priorität des Gerätes - kleiner Werte sind besser.</param>
        /// <returns>Das gewünschte Gerät.</returns>
        public ArgusTV.RecordingDevice CreateDevice( string name, int priority )
        {
            // Attach to the profile
            var profile = ProfileManager.FindProfile( name );

            // Update the priority
            priority = checked( (int) ReadSetting( profile, ProfileScheduleResource.SchedulePriorityName, ProfileScheduleResource.DefaultSchedulePriority ) );

            // Forward
            return new RecordingDevice( name, checked( -priority ) );
        }
    }

    /// <summary>
    /// Ein einzelnes Aufzeichnungsgerät.
    /// </summary>
    internal class RecordingDevice : RecordingDevice<SourceSelection, IAsyncResult>
    {
        /// <summary>
        /// Die zugehörige Instanz des Aufzeichnungsprozesses.
        /// </summary>
        private ServerImplementation m_cardServer;

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
        protected override bool CanRecordedInParallel( SourceSelection first, SourceSelection second )
        {
            // Validate
            if (first == null)
                return false;
            if (second == null)
                return false;

            // Ask sources
            return first.CompareTo( second, true );
        }

        /// <summary>
        /// Ermittelt zu einem Text den Sender.
        /// </summary>
        /// <param name="channelIdentification">Die Identifikation des Senders.</param>
        /// <returns>Die Senderinformationen.</returns>
        protected override SourceSelection[] OnResolve( string channelIdentification )
        {
            // Forward
            return ProfileManager.FindProfile( Name ).FindSource( channelIdentification, SourceNameMatchingModes.Name );
        }

        /// <summary>
        /// Ermittelt zu einer asynchronen Operation das Ergebnis.
        /// </summary>
        /// <typeparam name="TResult">Die Art des Ergebnisses.</typeparam>
        /// <param name="control">Die Steuerinformation der Operation.</param>
        /// <returns>Das gewünschte Ergebnis.</returns>
        protected override TResult GetResult<TResult>( IAsyncResult control )
        {
            // Forward
            return ServerImplementation.EndRequest<TResult>( control );
        }

        /// <summary>
        /// Wird aufgerufen, um das zugehörige Gerät zu reservieren.
        /// </summary>
        protected override void ReserveDevice()
        {
            // Already created
            if (m_cardServer != null)
                return;

            // Create the card server instance once
            m_cardServer = ServerImplementation.CreateOutOfProcess();

            // Be safe
            try
            {
                // Initialize the hardware - will run asynchronously
                Enqueue( () => m_cardServer.BeginSetProfile( Name, false, false, false ) );
            }
            catch (Exception)
            {
                // Cleanup
                using (m_cardServer)
                    m_cardServer = null;
            }
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
            // Nothing to release
            if (m_cardServer == null)
                return;

            // Normal operation
            if (!dispose)
            {
                // Synchronizer
                var waitEnd = new object();

                // Synchronize
                lock (waitEnd)
                    try
                    {
                        // Enqueue command - actually the card server should be idle but the call gives us a chance to synchronize with outstanding request in the queue!
                        Enqueue( () => m_cardServer.BeginRemoveAllSources(), ( object result ) => { lock (waitEnd) Monitor.Pulse( waitEnd ); } );

                        // Until we are done - better synchronize here
                        Monitor.Wait( waitEnd );
                    }
                    catch (Exception e)
                    {
                        // At least report
                        Trace.TraceError( e.Message );
                    }
            }

            // Reset server accessor
            using (m_cardServer)
                m_cardServer = null;
        }

        /// <summary>
        /// Wählt den Empfang einer Quelle an.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        protected override void Tune( SourceSelection source )
        {
            // Forward to server
            Enqueue( () => m_cardServer.BeginSelect( source.SelectionKey ) );
        }

        /// <summary>
        /// Beginnt mit der Aufzeichnung einer Quelle.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        /// <param name="recordingPath">Der volle Pfad zur Aufzeichnungsdatei.</param>
        protected override void BeginRecording( SourceSelection source, Guid streamIdentifier, string recordingPath )
        {
            // Recording data for the source
            var info =
                new ReceiveInformation
                {
                    UniqueIdentifier = streamIdentifier,
                    SelectionKey = source.SelectionKey,
                    RecordingPath = recordingPath,
                    Streams =
                        new StreamSelection
                        {
                            SubTitles = { LanguageMode = LanguageModes.All },
                            AC3Tracks = { LanguageMode = LanguageModes.All },
                            MP2Tracks = { LanguageMode = LanguageModes.All },
                            ProgramGuide = true,
                            Videotext = true,
                        },
                };

            // Fire up
            Enqueue( () => m_cardServer.BeginAddSources( new[] { info } ) );
        }

        /// <summary>
        /// Beendet die Aufzeichnung einer Quelle.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        /// <param name="recordingPath">Der volle Pfad zur Aufzeichnungsdatei.</param>
        protected override void EndRecording( SourceSelection source, Guid streamIdentifier, string recordingPath )
        {
            // Terminate
            Enqueue( () => m_cardServer.BeginRemoveSource( source.Source, streamIdentifier ) );
        }

        /// <summary>
        /// Beendet die Nutzung dieses Gerätes endgültig.
        /// </summary>
        protected override void OnDispose()
        {
            // Forward
            ReleaseDevice( true );
        }
    }
}