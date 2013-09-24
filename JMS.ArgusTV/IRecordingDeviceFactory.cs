

namespace JMS.ArgusTV
{
    /// <summary>
    /// Wird zum Anlegen von Geräten verwendet. 
    /// </summary>
    public interface IRecordingDeviceFactory
    {
        /// <summary>
        /// Erstellt ein neues Gerät.
        /// </summary>
        /// <param name="name">Der eindeutige Name des Gerätes.</param>
        /// <param name="priority">Die Priorität des Gerätes - kleiner Werte sind besser.</param>
        /// <returns>Das gewünschte Gerät.</returns>
        RecordingDevice CreateDevice( string name, int priority );
    }
}
