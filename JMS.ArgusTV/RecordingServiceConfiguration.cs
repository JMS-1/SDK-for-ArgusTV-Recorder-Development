using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.ServiceModel;
using System.Xml;
using System.Xml.Serialization;
using ArgusTV.ServiceContracts;


namespace JMS.ArgusTV
{
    /// <summary>
    /// Die Nutzung eines Aufzeichnungsverzeichnisses.
    /// </summary>
    [Flags]
    public enum RecordingDirectoryUsage
    {
        /// <summary>
        /// Nur ein Platzhalter.
        /// </summary>
        None = 0,

        /// <summary>
        /// Hier werden Aufzeichnungen abgelegt.
        /// </summary>
        Recording = 1,

        /// <summary>
        /// Kann zum zeitversetzten Betrachten verwendet werden.
        /// </summary>
        Timeshift = 2,
    }

    /// <summary>
    /// Beschreibt ein Aufzeichnungsverzeichnis.
    /// </summary>
    [Serializable]
    [XmlType( "Directory" )]
    public class RecordingDirectoryConfiguration
    {
        /// <summary>
        /// Der Pfad des Verzeichnisses aus Sicht des Aufzeichnungsrechners.
        /// </summary>
        public string LocalPath { get; set; }

        /// <summary>
        /// Der Pfad des Verzeichnisses aus Sicht des Netzwerks.
        /// </summary>
        public string NetworkPath { get; set; }

        /// <summary>
        /// Die Art der Nutzung des Verzeichnisses.
        /// </summary>
        public RecordingDirectoryUsage Usage { get; set; }
    }

    /// <summary>
    /// Beschreibt die Konfiguration eines Aufzeichnungsdienstes.
    /// </summary>
    [Serializable]
    [XmlType( "Service" )]
    public class RecordingServiceConfiguration
    {
        /// <summary>
        /// Der Name des Dienstes.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Der Kommunikationskanal.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Alle Aufzeichnungsverzeichnisse.
        /// </summary>
        public readonly List<RecordingDirectoryConfiguration> Directories = new List<RecordingDirectoryConfiguration>();

        /// <summary>
        /// Der Name der .NET Klasse zur Implementierung der Geräte.
        /// </summary>
        public string DeviceFactoryTypeName { get; set; }

        /// <summary>
        /// Der Name der <see cref="StringComparer"/> Eigenschaft die zum Vergleich von Gerätenamen verwendet werden soll.
        /// </summary>
        public string NameComparerPropertyName { get; set; }

        /// <summary>
        /// Die Liste der zu verwendenden Geräte.
        /// </summary>
        public readonly List<string> DeviceNames = new List<string>();

        /// <summary>
        /// Meldet den Vergleichsalgorithmus.
        /// </summary>
        [XmlIgnore]
        public IEqualityComparer<string> Comparer { get { return (IEqualityComparer<string>) typeof( StringComparer ).GetProperty( NameComparerPropertyName ).GetValue( null, null ); } }

        /// <summary>
        /// Erstellt aus der Konfiguration den Dienst.
        /// </summary>
        /// <returns>Der gewünschte Dienst.</returns>
        private IDisposable CreateService()
        {
            // Construct the service type
            var deviceType = Type.GetType( DeviceFactoryTypeName, true );

            // Process
            return new RecordingService( this, (IRecordingDeviceFactory) Activator.CreateInstance( deviceType ) );
        }

        /// <summary>
        /// Lädt eine Konfiguration aus einer Datei.
        /// </summary>
        /// <param name="path">Der volle Pfad zur Datei.</param>
        /// <returns>Die in der Datei enthaltene Konfiguration.</returns>
        public static RecordingServiceConfiguration Load( string path )
        {
            // Process
            using (var reader = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read ))
            {
                // Create serializer
                var serializer = new XmlSerializer( typeof( RecordingServiceConfiguration ) );

                // Reconstruct
                return (RecordingServiceConfiguration) serializer.Deserialize( reader );
            }
        }

        /// <summary>
        /// Legt die Dienstumgebung an.
        /// </summary>
        /// <returns>Die angeforderte Umgebung.</returns>
        public ServiceHost CreateServiceHost()
        {
            // Create service
            var service = CreateService();
            try
            {
                // Create the host
                var host = new ServiceHost( service );
                try
                {
                    // Create binding
                    var binding =
                        new NetTcpBinding( SecurityMode.None )
                        {
                            MaxReceivedMessageSize = 256 * 1024 * 1024,
                            ReceiveTimeout = new TimeSpan( 0, 30, 0 ),
                            SendTimeout = new TimeSpan( 0, 30, 0 ),
                            Namespace = "http://www.argus-tv.com",
                            ReaderQuotas =
                                new XmlDictionaryReaderQuotas
                                {
                                    MaxStringContentLength = int.MaxValue,
                                    MaxNameTableCharCount = int.MaxValue,
                                    MaxBytesPerRead = int.MaxValue,
                                    MaxArrayLength = int.MaxValue,
                                    MaxDepth = int.MaxValue,
                                },
                        };

                    // Configure binding
                    binding.Security.Transport.ClientCredentialType = TcpClientCredentialType.None;
                    binding.Security.Message.ClientCredentialType = MessageCredentialType.None;
                    binding.Security.Transport.ProtectionLevel = ProtectionLevel.None;

                    // Start 
                    host.Description.Name = Name;
                    host.AddServiceEndpoint( typeof( IRecorderTunerService ), binding, string.Format( "net.tcp://localhost:{0}/RecorderTunerService", Port ) );

                    // Report
                    return host;
                }
                catch (Exception)
                {
                    // Cleanup
                    ((IDisposable) host).Dispose();

                    // Forward
                    throw;
                }
            }
            catch (Exception)
            {
                // Cleanup
                service.Dispose();

                // Forward
                throw;
            }
        }
    }
}
