using System;
using System.IO;
using System.Linq;
using ArgusTV.DataContracts;
using ArgusTV.ServiceAgents;


namespace JMS.ArgusTV
{
    /// <summary>
    /// Eine einzelne Aufzeichnung.
    /// </summary>
    public class RecordingActivity
    {
        /// <summary>
        /// Der nächste Zeitpunkt, an dem eine Ausführung erwünscht ist.
        /// </summary>
        private volatile object m_nextTime = DateTime.MaxValue;

        /// <summary>
        /// Meldet den Zeitpunkt der nächsten Ausführung.
        /// </summary>
        public DateTime NextTime { get { return (DateTime) m_nextTime; } private set { m_nextTime = value; } }

        /// <summary>
        /// Die nächste Aktion, die auszuführen ist.
        /// </summary>
        private Func<bool> m_run;

        /// <summary>
        /// Der zugehörige Dienst.
        /// </summary>
        private readonly RecordingService m_service;

        /// <summary>
        /// Synchronisiert alle Zustandsänderungen.
        /// </summary>
        private readonly object m_synchronizer = new object();

        /// <summary>
        /// Gesetzt, wenn diese Aufzeichnung bereits aktiv ist.
        /// </summary>
        private bool m_isRunning;

        /// <summary>
        /// Das Gerät, das wir verwenden.
        /// </summary>
        private RecordingDevice m_device;

        /// <summary>
        /// Unser Aufzeichnungsverzeichnis.
        /// </summary>
        private string m_recordingPath;

        /// <summary>
        /// Der Name des Rechners mit dem Scheduler.
        /// </summary>
        private string m_schedulerHost;

        /// <summary>
        /// Die Adresses des Schedulers.
        /// </summary>
        private int m_schedulerPort;

        /// <summary>
        /// Gesetzt, wenn die Aufzeichnung am Gerät angemeldet wurde.
        /// </summary>
        private Guid? m_streamIdentifier;

        /// <summary>
        /// Die zugehörige Auswahl des Gerätes.
        /// </summary>
        private CardChannelAllocation m_allocation;

        /// <summary>
        /// Die Daten zur Sendung.
        /// </summary>
        private UpcomingProgram m_program;

        /// <summary>
        /// Die ursprüngliche Startzeit.
        /// </summary>
        private DateTime m_startTime;

        /// <summary>
        /// Die geplante Endzeit.
        /// </summary>
        private DateTime m_originalEndTime;

        /// <summary>
        /// Die aktuelle Endzeit.
        /// </summary>
        private volatile object m_currentEndTime = DateTime.MinValue;

        /// <summary>
        /// Die aktuelle Endzeit.
        /// </summary>
        private DateTime CurrentEndTime { get { return (DateTime) m_currentEndTime; } set { m_currentEndTime = value; } }

        /// <summary>
        /// Die möglichen Quellen passend zum Namen des Senders.
        /// </summary>
        private RecordingDevice.SourceResolveResult m_sources;

        /// <summary>
        /// Gesetzt, wenn die Aufzeichnung unvollständig ist.
        /// </summary>
        private bool m_incomplete;

        /// <summary>
        /// Gesetzt, wenn ein Abbruch erfolgt ist.
        /// </summary>
#pragma warning disable 0414
        private bool m_aborted;
#pragma warning restore 0414

        /// <summary>
        /// Erzeugt eine neue Aktivität.
        /// </summary>
        /// <param name="service">Der zugehörige Dienst.</param>
        public RecordingActivity( RecordingService service )
        {
            // Remember
            m_service = service;

            // Disable execution
            m_run = DoNothing;
        }

        /// <summary>
        /// Macht einfach nur nichts.
        /// </summary>
        /// <returns>Immer gesetzt.</returns>
        private static bool DoNothing()
        {
            // Report
            return true;
        }

        /// <summary>
        /// Prüft die Eingangsdaten.
        /// </summary>
        /// <returns>Gesetzt, wenn weitere Aktionen notwendig sind.</returns>
        private bool Validate()
        {
            // Validation phase
            try
            {
                // Check times
                if (m_originalEndTime < m_startTime)
                    throw new InvalidOperationException( "ends before start" );

                // Load the device
                m_device = m_service.Devices[m_allocation.CardId];
                if (m_device == null)
                    throw new InvalidOperationException( "bad device name" );

                // Find the possible sources
                m_sources = m_device.Resolve( m_allocation.ChannelName );
                if (m_sources == null)
                    throw new InvalidOperationException( "no such channel" );

                // Prepare next step
                m_run = Start;

                // When we should go for it
                NextTime = m_startTime;

                // If we are overdue just start immediately
                return (NextTime <= DateTime.UtcNow) ? Run() : true;
            }
            catch (Exception e)
            {
                // Report to scheduler
                using (var sink = new RecorderCallbackServiceAgent( m_schedulerHost, m_schedulerPort ))
                    sink.StartRecordingFailed( m_allocation, m_program, e.Message );

                // No further error processing required
                return false;
            }
        }

        /// <summary>
        /// Startet die Aufzeichnung, sobald dies möglich ist.
        /// </summary>
        /// <returns>Gesetzt, wenn weitere asynchrone Operationen notwendig sind.</returns>
        private bool Start()
        {
            // Pnly if we are not already done
            if (DateTime.UtcNow < CurrentEndTime)
            {
                // Try to allocate the device
                m_streamIdentifier = m_device.Start( m_sources, m_recordingPath );

                // Not possible
                if (!m_streamIdentifier.HasValue)
                {
                    // Fast retest
                    NextTime = DateTime.UtcNow.AddSeconds( 1 );

                    // Again
                    return true;
                }

                // Check recording
                if (DateTime.UtcNow > m_program.StartTimeUtc)
                    m_incomplete = true;

                // Be safe
                try
                {
                    // Report to scheduler
                    using (var sink = new RecorderCallbackServiceAgent( m_schedulerHost, m_schedulerPort ))
                        sink.AddNewRecording( m_program, DateTime.UtcNow, m_recordingPath );
                }
                catch (Exception)
                {
                    // Release device
                    ReleaseDevice();

                    // Forward
                    throw;
                }
            }

            // Prepare next step
            m_run = Finish;

            // When we should go for it
            NextTime = CurrentEndTime;

            // If we are overdue just start immediately
            return (NextTime <= DateTime.UtcNow) ? Run() : true;
        }

        /// <summary>
        /// Beendet die Nutzung des Gerätes.
        /// </summary>
        private void ReleaseDevice()
        {
            // Get the current identifier
            var streamIdentifier = m_streamIdentifier;

            // Reset
            m_streamIdentifier = null;

            // Release device
            if (streamIdentifier.HasValue)
                m_device.Stop( streamIdentifier.Value );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private bool Finish()
        {
            // Check recording
            if (DateTime.UtcNow < m_program.StopTimeUtc)
                m_incomplete = true;

            // Release device
            ReleaseDevice();

            // Report to scheduler
            using (var sink = new RecorderCallbackServiceAgent( m_schedulerHost, m_schedulerPort ))
                sink.EndRecording( m_recordingPath, DateTime.UtcNow, m_incomplete, true );

            // Done
            return false;
        }

        /// <summary>
        /// Beginnt eine neue Aufzeichnung.
        /// </summary>
        /// <param name="serverHostName">Der Name des ArgusTV Steuerdienstes.</param>
        /// <param name="tcpPort">Die Adresse des Steuerdienstes.</param>
        /// <param name="channelAllocation">Das Gerät, auf dem die Aufzeichnung stattfinden soll.</param>
        /// <param name="startTimeUtc">Der Startzeitpunkt.</param>
        /// <param name="stopTimeUtc">Der Endzeitpunkt.</param>
        /// <param name="recordingProgram">Das aufzuzeichnende Programm.</param>
        /// <param name="suggestedBaseFileName">Ein Vorschlag für den Namen der Aufzeichnungsdatei.</param>
        /// <returns>Gesetzt, wenn der Start eingeleitet wurde.</returns>
        public bool Start( string serverHostName, int tcpPort, CardChannelAllocation channelAllocation, DateTime startTimeUtc, DateTime stopTimeUtc, UpcomingProgram recordingProgram, string suggestedBaseFileName )
        {
            // Synchronize
            lock (m_synchronizer)
            {
                // Already active
                if (m_isRunning)
                    return false;

                // Get the first recording path in local notation
                var recordingDir =
                    m_service
                        .Configuration
                        .Directories
                        .Where( d => (d.Usage & RecordingDirectoryUsage.Recording) != 0 )
                        .Select( d => d.LocalPath )
                        .First();

                // Copy anything we need to us as the current state
                m_recordingPath = Path.Combine( recordingDir, (string.IsNullOrEmpty( suggestedBaseFileName ) ? Guid.NewGuid().ToString( "N" ) : suggestedBaseFileName) + ".ts" );
                m_allocation = channelAllocation;
                m_schedulerHost = serverHostName;
                m_originalEndTime = stopTimeUtc;
                m_program = recordingProgram;
                CurrentEndTime = stopTimeUtc;
                m_startTime = startTimeUtc;
                m_schedulerPort = tcpPort;
                m_isRunning = true;

                // Do asynchronous validation
                m_run = Validate;

                // Fire as soon as possible on the dedicated timer thread
                NextTime = DateTime.UtcNow;

                // Did it - remote call will now end
                return true;
            }
        }

        /// <summary>
        /// Verändert den Endzeitpunkt.
        /// </summary>
        /// <param name="newEnd">Der neue Endzeitpunkt.</param>
        /// <returns>Gesetzt, wenn die Änderung erfolgreich war.</returns>
        public bool SetNewStopTime( DateTime newEnd )
        {
            // Forward
            return ModifyEnd( newEnd, false );
        }

        /// <summary>
        /// Verändert den Endzeitpunkt.
        /// </summary>
        /// <returns>Gesetzt, wenn die Änderung erfolgreich war.</returns>
        public bool Abort()
        {
            // Forward
            return ModifyEnd( DateTime.UtcNow, true );
        }

        /// <summary>
        /// Verändert den Endzeitpunkt.
        /// </summary>
        /// <param name="newEnd">Der neue Endzeitpunkt.</param>
        /// <param name="aborted">Gesetzt, wenn es sich um einen Abbruch handelt.</param>
        /// <returns>Gesetzt, wenn die Änderung erfolgreich war.</returns>
        private bool ModifyEnd( DateTime newEnd, bool aborted )
        {
            // Protected
            lock (m_synchronizer)
            {
                // Not yet started - strange!
                if (!m_isRunning)
                    return false;

                // Set flag - never reset!
                if (aborted)
                    m_aborted = true;

                // Adjust the end time
                CurrentEndTime = newEnd;

                // Adjust wakeup
                if (newEnd < NextTime)
                    NextTime = newEnd;

                // Finished with the synchronous part
                return true;
            }
        }

        /// <summary>
        /// Führt den aktuelle Arbeitsschritt aus.
        /// </summary>
        /// <returns>Gesetzt, wenn später weitere Schritte notwendig sind.</returns>
        public bool Run()
        {
            // Synchronize and forward
            lock (m_synchronizer)
                return m_run();
        }
    }
}
