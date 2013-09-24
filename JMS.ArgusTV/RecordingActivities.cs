using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;


namespace JMS.ArgusTV
{
    /// <summary>
    /// Alle gerade aktiven Aufzeichnungen.
    /// </summary>
    public class RecordingActivities : IDisposable
    {
        /// <summary>
        /// Die einzelnen Zustände der Sperre des Schlafzsutands.
        /// </summary>
        [Flags]
        private enum ExecutionState : uint
        {
            /// <summary>
            /// Es ist ein Fehler aufgetreten.
            /// </summary>
            Error = 0,

            /// <summary>
            /// Schlafzustand ist grundsätzlich verboten.
            /// </summary>
            SystemRequired = 1,

            /// <summary>
            /// Die Anzeige wird benötigt.
            /// </summary>
            DisplayRequired = 2,

            /// <summary>
            /// Ein Benutzer benötigt das System.
            /// </summary>
            UserPresent = 4,

            /// <summary>
            /// Die Simulation des Schlafzustands ist gestattet.
            /// </summary>
            AwayModeRequired = 0x40,

            /// <summary>
            /// Gesetzt, um mit einem einmaligen Aufruf einen Zustand zu fixieren.
            /// </summary>
            Continuous = 0x80000000
        }

        /// <summary>
        /// Steuert die Sperre für den Übergang in den Schlafzustand.
        /// </summary>
        [DllImport( "kernel32.dll", EntryPoint = "SetThreadExecutionState" )]
        [SuppressUnmanagedCodeSecurity]
        private static extern ExecutionState SetThreadExecutionState( ExecutionState esFlags );

        /// <summary>
        /// Alle bekannten Aufzeichnungen.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, RecordingActivity> m_activities = new ConcurrentDictionary<Guid, RecordingActivity>();

        /// <summary>
        /// Steuert den Ablauf.
        /// </summary>
        private volatile Thread m_worker;

        /// <summary>
        /// Erstellt eine neue Aufgabenverwaltung.
        /// </summary>
        public RecordingActivities()
        {
            // Create the thread - setting the background flag is a bit dangerous but may helf to shutdown the service on demand
            m_worker = new Thread( Worker ) { Name = "Recoding Service Activities", IsBackground = true };

            // Fire up
            m_worker.Start();
        }

        /// <summary>
        /// Ermittelt eine Aktivität und legt sie bei Bedarf neu an.
        /// </summary>
        /// <param name="activityIdentifier">Die eindeutige Kennung der Aktivität.</param>
        /// <param name="service">Der zugehörige Dienst.</param>
        /// <returns>Die gewünschte Aktivität.</returns>
        public RecordingActivity GetOrCreate( Guid activityIdentifier, RecordingService service )
        {
            // Process
            return m_activities.GetOrAdd( activityIdentifier, identifier => new RecordingActivity( service ) );
        }

        /// <summary>
        /// Ermittelt eine Aktivität.
        /// </summary>
        /// <param name="activityIdentifier">Die eindeutige Kennung der Aktivität.</param>
        /// <returns>Die gewünschte Aktivität.</returns>
        public RecordingActivity Get( Guid activityIdentifier )
        {
            // Process
            RecordingActivity activity;
            if (m_activities.TryGetValue( activityIdentifier, out activity ))
                return activity;
            else
                return null;
        }

        /// <summary>
        /// Entfernt eine Arbeitseinheit.
        /// </summary>
        /// <param name="key">Die eindeutige Kennung der Arbeitseinheit.</param>
        private void Remove( Guid key )
        {
            // Force remove
            RecordingActivity activity;
            m_activities.TryRemove( key, out activity );
        }

        /// <summary>
        /// Wird periodisch aufgerufen und prüft, ob etwas zu tun ist.
        /// </summary>
        private void Worker()
        {
            // Current sleep state
            var sleepIsAllowed = true;

            // Be very safe with sleep control
            try
            {
                // As long as neccessary
                for (; m_worker != null; Thread.Sleep( 1000 ))
                {
                    // May want to disable sleep
                    if (m_activities.Count > 0)
                        if (sleepIsAllowed)
                            if (SetThreadExecutionState( ExecutionState.SystemRequired | ExecutionState.Continuous | ExecutionState.AwayModeRequired ) != ExecutionState.Error)
                                sleepIsAllowed = false;
                            else if (SetThreadExecutionState( ExecutionState.SystemRequired | ExecutionState.Continuous ) != ExecutionState.Error)
                                sleepIsAllowed = false;

                    // Processing limit
                    var now = DateTime.UtcNow;

                    // Inspect 
                    foreach (var activity in m_activities.Where( activity => activity.Value.NextTime <= now ).ToArray())
                        try
                        {
                            // Enforced shutdown
                            if (m_worker == null)
                                return;

                            // Process and discard when needed
                            if (!activity.Value.Run())
                                Remove( activity.Key );
                        }
                        catch (Exception e)
                        {
                            // Force remove
                            Remove( activity.Key );

                            // Just report
                            Trace.TraceError( e.Message );
                        }

                    // May want to allow sleep - there is a little time gap between enqueue into an empty queue but who cares
                    if (m_activities.Count < 1)
                        if (!sleepIsAllowed)
                            if (SetThreadExecutionState( ExecutionState.Continuous ) != ExecutionState.Error)
                                sleepIsAllowed = true;
                }
            }
            finally
            {
                // Must allow sleep
                if (!sleepIsAllowed)
                    SetThreadExecutionState( ExecutionState.Continuous );
            }
        }

        /// <summary>
        /// Beendet die Verwaltung endgültig.
        /// </summary>
        public void Dispose()
        {
            // Attach to worker
            var worker = m_worker;

            // Forget it
            m_worker = null;

            // Wait til end
            if (worker != null)
                worker.Join();
        }
    }
}
