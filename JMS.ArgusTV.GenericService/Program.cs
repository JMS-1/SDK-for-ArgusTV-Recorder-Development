using System;
using System.Collections;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.ServiceProcess;


namespace JMS.ArgusTV.GenericService
{
    /// <summary>
    /// Repräsentiert den Windows Prozess als Ganzes.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Der eigentliche Dienst.
        /// </summary>
        private class Service : ServiceBase
        {
            /// <summary>
            /// Die zugehörige Konfiguration.
            /// </summary>
            private readonly RecordingServiceConfiguration m_configuration;

            /// <summary>
            /// Der Aufzeichnungsdienst.
            /// </summary>
            private IDisposable m_recordingService;

            /// <summary>
            /// Die WCF Kapsel des Dienstes.
            /// </summary>
            private IDisposable m_serviceHost;

            /// <summary>
            /// Erstellt einen neuen Windows Dienst.
            /// </summary>
            /// <param name="configuration">Die zu verwendende Configuration.</param>
            private Service( RecordingServiceConfiguration configuration )
            {
                // Remember
                m_configuration = configuration;

                // Forward
                ServiceName = CreateServiceName( configuration );
                AutoLog = false;
                CanStop = true;
            }

            /// <summary>
            /// Erstellt den Aufzeichnungsdienst.
            /// </summary>
            private void CreateRecordingService()
            {
                // Create host
                var host = m_configuration.CreateServiceHost();

                // Remember for later cleanup
                using (m_serviceHost)
                    m_serviceHost = host;

                // Dito the instance
                using (m_recordingService)
                    m_recordingService = host.SingletonInstance as IDisposable;

                // Start
                host.Open();
            }

            /// <summary>
            /// Beendet den Aufzeichnungsdienst.
            /// </summary>
            private void RemoveRecordingService()
            {
                // Get rid of it all
                using (m_serviceHost)
                    m_serviceHost = null;
                using (m_recordingService)
                    m_recordingService = null;
            }

            /// <summary>
            /// Startet den Dienst.
            /// </summary>
            /// <param name="args">Die Parameter des Dienstes.</param>
            protected override void OnStart( string[] args )
            {
                // Forward
                base.OnStart( args );

                // Be safe
                try
                {
                    // Startup host
                    CreateRecordingService();
                }
                catch (Exception e)
                {
                    // Report
                    EventLog.WriteEntry( e.ToString(), EventLogEntryType.Error );
                }
            }

            /// <summary>
            /// Beendet den Dienst.
            /// </summary>
            protected override void OnStop()
            {
                // Be safe
                try
                {
                    // Startup host
                    RemoveRecordingService();
                }
                catch (Exception e)
                {
                    // Report
                    EventLog.WriteEntry( e.ToString(), EventLogEntryType.Error );
                }

                // Forward
                base.OnStop();
            }

            /// <summary>
            /// Erstellt einen neuen Windows Dienst.
            /// </summary>
            /// <param name="configuration">Die zu verwendende Configuration.</param>
            public static void Run( RecordingServiceConfiguration configuration )
            {
                // Create the new service
                using (var service = new Service( configuration ))
                    if (DebugMode)
                    {
                        // Start host
                        service.CreateRecordingService();

                        // Wait
                        Console.WriteLine( "Running, press ENTER to End" );
                        Console.ReadLine();

                        // Done with host
                        service.RemoveRecordingService();
                    }
                    else
                    {
                        // Full processing
                        Run( service );
                    }
            }
        }

        /// <summary>
        /// Gesetzt, wenn im Testmodus gearbeitet wird.
        /// </summary>
        public static bool DebugMode;

        /// <summary>
        /// Startet die Anwendung.
        /// </summary>
        /// <param name="args">Feinsteurung der Anwendung.</param>
        /// <returns>Das Ergebnis der Ausführung.</returns>
        public static int Main( string[] args )
        {
            // Is there a path
            if (args.Length < 1)
                return 1;
            if (args.Length > 2)
                return 1;

            // Get the path
            var configurationPath = Path.Combine( Path.GetDirectoryName( new Uri( Assembly.GetExecutingAssembly().CodeBase ).LocalPath ), args[0] );
            var configuration = RecordingServiceConfiguration.Load( configurationPath );

            // Check mode
            if (args.Length == 2)
                switch (args[1])
                {
                    case "/uninstall": Install( configuration, configurationPath, false ); return 0;
                    case "/install": Install( configuration, configurationPath, true ); return 0;
                    case "/debug": DebugMode = true; break;
                    default: return 2;
                }

            // Process
            Service.Run( configuration );

            // Done
            return 0;
        }

        /// <summary>
        /// Erstellt den eindeutigen Namen des Dienstes.
        /// </summary>
        /// <param name="configuration">Die verwendete Konfiguration.</param>
        /// <returns>Der gewünschte Name.</returns>
        private static string CreateServiceName( RecordingServiceConfiguration configuration )
        {
            // Construct
            return "ArgusTVRecorder" + configuration.Name;
        }

        /// <summary>
        /// Installiert einen Dienst.
        /// </summary>
        /// <param name="configuration">Die Konfiguration des Dienstes.</param>
        /// <param name="fileName">Der volle Pfad zur Konfigurationsdatei.</param>
        /// <param name="install">Gesetzt, wenn eine Installation ausgeführt werden soll.</param>
        private static void Install( RecordingServiceConfiguration configuration, string fileName, bool install )
        {
            // Create the service installer
            var service =
                new ServiceInstaller
                {
                    Description = "Generic Recorder / Tuner Service based on JMS Argus TV SDK",
                    DisplayName = configuration.Name + " (Argus TV Recorder)",
                    ServiceName = CreateServiceName( configuration ),
                    StartType = ServiceStartMode.Manual,
                    Context = new InstallContext(),
                };

            // Create the process installer
            var process =
                new ServiceProcessInstaller
                {
                    Account = ServiceAccount.LocalService,
                    Context = service.Context,
                };

            // Link together
            process.Installers.Add( service );

            // Create service path 
            var exePath = "\"" + new Uri( Assembly.GetExecutingAssembly().CodeBase ).LocalPath.Replace( "\"", "\"" ) + "\"";
            var configPath = "\"" + fileName + "\"";

            // Configure service path
            process.Context.Parameters["assemblypath"] = exePath + " " + configPath;

            // Create state 
            var pathToState = fileName + ".install";
            var serializer = new BinaryFormatter();

            // Try it
            if (install)
            {
                // Create the state
                var state = new Hashtable();

                // Do the installation
                process.Install( state );

                // Save the state
                using (var stateFile = new FileStream( pathToState, FileMode.Create, FileAccess.Write, FileShare.None ))
                    serializer.Serialize( stateFile, state );
            }
            else
            {
                // Load the state
                using (var stateFile = new FileStream( pathToState, FileMode.Open, FileAccess.Read, FileShare.Read ))
                {
                    // Reconstruct the state
                    var state = (Hashtable) serializer.Deserialize( stateFile );

                    // Do the installation
                    process.Uninstall( state );
                }

                // Done
                File.Delete( pathToState );
            }
        }
    }
}
