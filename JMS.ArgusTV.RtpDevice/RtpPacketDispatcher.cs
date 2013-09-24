using System;
using JMS.DVB.TS;


namespace JMS.ArgusTV.RtpDevice
{
    /// <summary>
    /// Ein einzelnes RTP Paket
    /// </summary>
    /// <remarks>
    /// The RTP header has the following format:
    /// 0                   1                   2                   3
    /// 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |V=2|P|X|  CC   |M|     PT      |       sequence number         |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |                           timestamp                           |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |           synchronization source (SSRC) identifier            |
    /// +=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
    /// |            contributing source (CSRC) identifiers             |
    /// |                             ....                              |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// </remarks>
    internal static class RtpPacketDispatcher
    {
        /// <summary>
        /// Prüft die Eingangsdaten und versendet das Ergebnis.
        /// </summary>
        /// <param name="packet">Die Rohdaten.</param>
        /// <param name="offset">Das erste Nutzbyte.</param>
        /// <param name="length">Die Anzahl der Bytes.</param>
        /// <param name="sink">Empfänger für alle gültigen Daten.</param>
        public static void DispatchTSPayload( byte[] packet, int offset, int length, Action<byte[], int, int> sink )
        {
            // Not active 
            if (sink == null)
                return;

            // Validate
            if (packet == null)
                return;
            if (offset < 0)
                return;
            if (length < 12)
                return;
            if ((offset + (long) length) > packet.Length)
                return;

            // Extract header information
            var header1 = packet[offset + 1];
            var payloadType = header1 & 127;

            // Check type first to speed up processing
            if (payloadType != 0x21)
                return;

            // Extract header information
            var header0 = packet[offset + 0];
            var extension = ((header0 >> 4) & 1) != 0;
            var padding = ((header0 >> 5) & 1) != 0;
            var version = (header0 >> 6) & 3;
            var ccCount = header0 & 15;

            // Validation and currently unsupported features
            if (version != 2)
                return;
            if (extension)
                return;
            if (padding)
                return;

            // The real size of the header
            var headerSize = 12 + 4 * ccCount;
            var payloadSize = length - headerSize;
            if (payloadSize < 0)
                return;

            // Validate
            if ((payloadSize % Manager.FullSize) != 0)
                return;

            // Send
            sink( packet, offset + headerSize, payloadSize );
        }
    }
}
