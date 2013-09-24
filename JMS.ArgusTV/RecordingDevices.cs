using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;


namespace JMS.ArgusTV
{
    /// <summary>
    /// Alle aktuell verfügbaren Geräte.
    /// </summary>
    public class RecordingDevices : IEnumerable<RecordingDevice>, IDisposable
    {
        /// <summary>
        /// Alle unterstützten Geräte.
        /// </summary>
        private readonly Dictionary<string, RecordingDevice> m_devices;

        /// <summary>
        /// Erstellt eine neue Geräteverwaltung.
        /// </summary>
        /// <param name="deviceNames">Alle zu verwendenden Geräte.</param>
        /// <param name="comparer">Der Vergleichsalgorithmus für Gerätenamen.</param>
        /// <param name="factory">Komponente zum Anlegen von Geräten-</param>
        public RecordingDevices( IEnumerable<string> deviceNames, IEqualityComparer<string> comparer, IRecordingDeviceFactory factory )
        {
            // Priority counter
            var priority = 0;

            // Remember
            m_devices = deviceNames.ToDictionary( name => name, name => factory.CreateDevice( name, ++priority ), comparer );
        }

        /// <summary>
        /// Meldet den Vergleichsalgorithmus für Namen.
        /// </summary>
        public IEqualityComparer<string> NameComparer { get { return m_devices.Comparer; } }

        /// <summary>
        /// Meldet ein Gerät mit einem bestimmten Namen.
        /// </summary>
        /// <param name="deviceName">Der Name des gwünschten Gerätes.</param>
        public RecordingDevice this[string deviceName]
        {
            get
            {
                // Load
                RecordingDevice device;
                if (m_devices.TryGetValue( deviceName, out device ))
                    return device;
                else
                    return null;
            }
        }

        /// <summary>
        /// Meldet eine auflistung über alle Geräte.
        /// </summary>
        /// <returns>Die gewünschte Auflistung.</returns>
        public IEnumerator<RecordingDevice> GetEnumerator()
        {
            // Forward
            return m_devices.Values.GetEnumerator();
        }

        /// <summary>
        /// Meldet eine auflistung über alle Geräte.
        /// </summary>
        /// <returns>Die gewünschte Auflistung.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            // Forward
            return GetEnumerator();
        }

        /// <summary>
        /// Beendet die Nutzung aller Geräte endgültig.
        /// </summary>
        public void Dispose()
        {
            // Forward
            foreach (var device in m_devices.Values)
                try
                {
                    // Forward
                    device.Dispose();
                }
                catch (Exception e)
                {
                    // Report only
                    Trace.TraceError( e.Message );
                }

            // Forget
            m_devices.Clear();
        }
    }
}
