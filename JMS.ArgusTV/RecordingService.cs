using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using ArgusTV.DataContracts;
using ArgusTV.DataContracts.Tuning;
using ArgusTV.ServiceAgents;
using ArgusTV.ServiceContracts;


namespace JMS.ArgusTV
{
    /// <summary>
    /// Ein Beispieldienst für Aufzeichnungen mit <i>ArgusTV</i>.
    /// </summary>
    [ServiceBehavior( ConcurrencyMode = ConcurrencyMode.Reentrant, InstanceContextMode = InstanceContextMode.Single )]
    public class RecordingService : IRecorderTunerService, IDisposable
    {
        /// <summary>
        /// Alle unterstützten Geräte.
        /// </summary>
        public RecordingDevices Devices { get; private set; }

        /// <summary>
        /// Der (eindeutige) Name dieses Dienstes.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Die eindeutige Kennung dieses Dienstes.
        /// </summary>
        public Guid Identifier { get; private set; }

        /// <summary>
        /// Alle bekannten Aktivitäten.
        /// </summary>
        private RecordingActivities m_activities = new RecordingActivities();

        /// <summary>
        /// Die zugehörige Konfiguration.
        /// </summary>
        public readonly RecordingServiceConfiguration Configuration;

        /// <summary>
        /// Erstellt einen neuen Dienst.
        /// </summary>
        /// <param name="configuration">Die zu verwendende Konfiguration.</param>
        /// <param name="factory">Kann Gerätebeschreibungen erzeugen.</param>
        public RecordingService( RecordingServiceConfiguration configuration, IRecordingDeviceFactory factory )
        {
            // Remember
            Devices = new RecordingDevices( configuration.DeviceNames, configuration.Comparer, factory );
            Configuration = configuration;
            Name = configuration.Name;
        }

        /// <summary>
        /// Beendet eine Aufzeichnung.
        /// </summary>
        /// <param name="serverHostName">Der Name des Rechners mit dem ArgusTV Scheduler.</param>
        /// <param name="tcpPort">Die Adresse des Schedulers.</param>
        /// <param name="recordingProgram">Das zu beendende Programm.</param>
        /// <returns>Gesetzt, wenn der Aufruf erfolgreich war.</returns>
        public bool AbortRecording( string serverHostName, int tcpPort, UpcomingProgram recordingProgram )
        {
            // Locate
            var activity = m_activities.Get( recordingProgram.UpcomingProgramId );
            if (activity == null)
                return false;

            // Forward
            return activity.Abort();
        }

        /// <summary>
        /// Prüft, ob eine Ausführung möglich ist.
        /// </summary>
        /// <param name="channel">Der Sender, von dem aufgezeichnet werden soll.</param>
        /// <param name="alreadyAllocated">Alle bereits geplanten Aufzeichnungen.</param>
        /// <param name="useReversePriority">Gesetzt, um die Planungspriorität zu verändern (wird hier ignoriert).</param>
        /// <returns>Der eindeutige Name des Gerätes, auf dem eine Aufzeichnung stattfinden könnte.</returns>
        public string AllocateCard( Channel channel, CardChannelAllocation[] alreadyAllocated, bool useReversePriority )
        {
            // Find all devices which are able to handle the channel of interest
            var candidates =
                    Devices
                        .Where( d => d.Resolve( channel.DisplayName ) != null )
                        .ToDictionary( d => d.Name, Devices.NameComparer );

            // None left
            if (candidates.Count < 1)
                return null;

            // Check all allocations
            foreach (var allocation in alreadyAllocated)
            {
                // No longer a candidate
                if (!candidates.ContainsKey( allocation.CardId ))
                    continue;

                // Attach to the device
                var device = Devices[allocation.CardId];
                if (device == null)
                    continue;

                // If the channel name is the same guess we can do it
                if (StringComparer.Ordinal.Equals( channel.DisplayName, allocation.ChannelName ))
                    continue;

                // Now we have to check for the source group - we can record different sources if coming from the same group
                var allowedGroups = device.Resolve( channel.DisplayName );
                var allocatedGroups = device.Resolve( allocation.ChannelName );

                // Check for any match - actually this is fairly slow (ok for the given scenario) and not totally correct when using multiple sources with the same name (e.g. Channel 4)
                if (device.CheckForCommonSourceGroups( allowedGroups, allocatedGroups ))
                    continue;

                // Discard
                candidates.Remove( allocation.CardId );

                // Maybe we are done
                if (candidates.Count < 1)
                    return null;
            }

            // Get the best fit - we know the list is NOT empty
            return
                candidates
                    .Values
                    .Aggregate( default( RecordingDevice ), ( best, test ) => (best == null) ? test : ((test.Priority < best.Priority) ? test : best) )
                    .Name;
        }

        /// <summary>
        /// Meldet die Aufzeichnungsverzeichnisse in UNC Notation.
        /// </summary>
        /// <returns>Die Liste aller Aufzeichnungsverzeichnisse.</returns>
        public string[] GetRecordingShares()
        {
            // Report
            return
                Configuration
                    .Directories
                    .Where( d => (d.Usage & RecordingDirectoryUsage.Recording) != 0 )
                    .Select( d => d.NetworkPath )
                    .ToArray();
        }

        /// <summary>
        /// Meldet alle Verzeichniss zum zeitversetzten Betrachen von Aufzeichnungen in UNC Notation.
        /// </summary>
        /// <returns>Doe Liste der Verzeichnisse.</returns>
        /// <seealso cref="GetRecordingShares"/>
        public string[] GetTimeshiftShares()
        {
            // Report
            return
                Configuration
                    .Directories
                    .Where( d => (d.Usage & RecordingDirectoryUsage.Timeshift) != 0 )
                    .Select( d => d.NetworkPath )
                    .ToArray();
        }

        /// <summary>
        /// Fordert den Dienst zur Anmeldung auf.
        /// </summary>
        /// <param name="recorderTunerId">Die eindeutige Kennung dieses Dienstes.</param>
        /// <param name="serverHostName">Der Name des Steuerrechners.</param>
        /// <param name="tcpPort">Die Adresse des Steuerdienstes.</param>
        public void Initialize( Guid recorderTunerId, string serverHostName, int tcpPort )
        {
            // Remember
            Identifier = recorderTunerId;

            // Process callback
            using (var agent = new RecorderCallbackServiceAgent( serverHostName, tcpPort ))
                agent.RegisterRecorderTuner( Identifier, Name, GetType().Assembly.GetName().Version.ToString() );
        }

        /// <summary>
        /// Meldet die API Version, mit der dieser Dienst erstellt wurde.
        /// </summary>
        /// <returns>Die verwendete API Version.</returns>
        public int Ping()
        {
            // Report
            return Constants.RecorderApiVersion;
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
        public bool StartRecording( string serverHostName, int tcpPort, CardChannelAllocation channelAllocation, DateTime startTimeUtc, DateTime stopTimeUtc, UpcomingProgram recordingProgram, string suggestedBaseFileName )
        {
            // Enqueue
            return m_activities.GetOrCreate( recordingProgram.UpcomingProgramId, this ).Start( serverHostName, tcpPort, channelAllocation, startTimeUtc, stopTimeUtc, recordingProgram, suggestedBaseFileName );
        }

        /// <summary>
        /// Prüft eine Aufzeichnung und verändert optional den Endzeitpunkt.
        /// </summary>
        /// <param name="channelAllocation">Informationen zur Aufzeichnung.</param>
        /// <param name="recordingProgram">Das betroffene Programm.</param>
        /// <param name="stopTimeUtc">Der neue Endzeitpunkt.</param>
        /// <returns>Gesetzt, wenn die Änderung erfolgreich war.</returns>
        public bool ValidateAndUpdateRecording( CardChannelAllocation channelAllocation, UpcomingProgram recordingProgram, DateTime stopTimeUtc )
        {
            // Locate
            var activity = m_activities.Get( recordingProgram.UpcomingProgramId );
            if (activity == null)
                return false;

            // Forward
            return activity.SetNewStopTime( stopTimeUtc );
        }

        /// <summary>
        /// Beendet den Dienst endgültig.
        /// </summary>
        public void Dispose()
        {
            // Stop timer
            using (m_activities)
                m_activities = null;

            // Stop devices
            using (Devices)
                Devices = null;
        }

        #region Erst einmal nicht unterstützter LIVE Modus

        /// <summary>
        /// Meldet alle <i>LIVE</i> Datenströme.
        /// </summary>
        /// <returns>Die Liste aller Datenströme.</returns>
        public LiveStream[] GetLiveStreams()
        {
            // We do not support LIVE streams
            return new LiveStream[0];
        }

        /// <summary>
        /// Aktiviert den <i>LIVE</i> Modus.
        /// </summary>
        /// <param name="channel">Der gewünschte Sender.</param>
        /// <param name="upcomingRecordingAllocation">Beschreibung der Belegung.</param>
        /// <param name="liveStream">Die Daten zum Datenstrom.</param>
        /// <returns>Das Ergebnis der Operation.</returns>
        public LiveStreamResult TuneLiveStream( Channel channel, CardChannelAllocation upcomingRecordingAllocation, ref LiveStream liveStream )
        {
            // Report
            return LiveStreamResult.NotSupported;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        /// <returns></returns>
        public bool KeepLiveStreamAlive( LiveStream liveStream )
        {
            throw new NotImplementedException( "KeepLiveStreamAlive" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        /// <returns></returns>
        public ServiceTuning GetLiveStreamTuningDetails( LiveStream liveStream )
        {
            throw new NotImplementedException( "GetLiveStreamTuningDetails" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channels"></param>
        /// <param name="liveStream"></param>
        /// <returns></returns>
        public ChannelLiveState[] GetChannelsLiveState( Channel[] channels, LiveStream liveStream )
        {
            throw new NotImplementedException( "GetChannelsLiveState" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        public void StopLiveStream( LiveStream liveStream )
        {
            throw new NotImplementedException( "StopLiveStream" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        /// <returns></returns>
        public bool HasTeletext( LiveStream liveStream )
        {
            throw new NotImplementedException( "HasTeletext" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        public void StartGrabbingTeletext( LiveStream liveStream )
        {
            throw new NotImplementedException( "StartGrabbingTeletext" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        /// <returns></returns>
        public bool IsGrabbingTeletext( LiveStream liveStream )
        {
            throw new NotImplementedException( "IsGrabbingTeletext" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        /// <param name="pageNumber"></param>
        /// <param name="subPageNumber"></param>
        /// <param name="subPageCount"></param>
        /// <returns></returns>
        public byte[] GetTeletextPageBytes( LiveStream liveStream, int pageNumber, int subPageNumber, out int subPageCount )
        {
            throw new NotImplementedException( "GetTeletextPageBytes" );
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="liveStream"></param>
        public void StopGrabbingTeletext( LiveStream liveStream )
        {
            throw new NotImplementedException( "StopGrabbingTeletext" );
        }

        #endregion
    }
}
