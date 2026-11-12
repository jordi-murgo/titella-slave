// ILinkTransport adapter over the HID byte stream. AgentLink keeps its
// reader/writer/heartbeat threads, bounded queues and every service exactly
// as it does for the CDC and clipboard transports; this unit only maps the
// HID binding to a HidByteStream and translates stream failures into the
// existing LinkLostException the link already understands.

namespace Titella.Slave
{
    using System;
    using System.IO;

    /// <summary>
    /// Transport for <c>slave_transport = "hid"</c> (the protocol field
    /// <c>agent_protocol</c> stays <c>serial</c>): protocol v1 frames travel
    /// inside the HID data stream, never through the shared clipboard, a COM
    /// port or the network. Read keeps the CDC contract (zero after 100 ms
    /// without bytes) and Write returns once the peer acknowledged the bytes.
    /// </summary>
    internal sealed class HidTransport : ILinkTransport
    {
        private readonly string _binding;
        private HidByteStream _stream;

        public HidTransport(string binding)
        {
            if (!HidByteStream.IsHidBinding(binding))
            {
                throw new ArgumentException(
                    "a HID binding such as HID:0123456789AB is required", "binding");
            }
            _binding = binding;
        }

        /// <summary>True for the exact <c>HID:&lt;12 uppercase hex&gt;</c> form.</summary>
        public static bool IsHidBinding(string value)
        {
            return HidByteStream.IsHidBinding(value);
        }

        /// <summary>Opens the data collection and completes the OPEN handshake.</summary>
        public void Open()
        {
            _stream = HidByteStream.Open(_binding);
        }

        public bool IsOpen
        {
            get { return _stream != null; }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            HidByteStream stream = _stream;
            if (stream == null)
            {
                throw new LinkLostException(0, "the HID transport is closed");
            }
            try
            {
                return stream.Read(buffer, offset, count);
            }
            catch (IOException error)
            {
                throw ToLinkLost(error);
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            HidByteStream stream = _stream;
            if (stream == null)
            {
                throw new LinkLostException(0, "the HID transport is closed");
            }
            try
            {
                stream.Write(buffer, offset, count);
            }
            catch (IOException error)
            {
                throw ToLinkLost(error);
            }
        }

        public void Dispose()
        {
            HidByteStream stream = _stream;
            _stream = null;
            if (stream != null)
            {
                stream.Dispose();
            }
        }

        /// <summary>
        /// Keeps the HID code visible: the loader walks the InnerException
        /// chain for Exception.Data["titella_code"], and AgentLink only knows
        /// LinkLostException.
        /// </summary>
        private static LinkLostException ToLinkLost(IOException error)
        {
            LinkLostException link = new LinkLostException(0, error.Message);
            string code = HidErrors.CodeOf(error);
            if (code != null)
            {
                link.Data["titella_code"] = code;
            }
            return link;
        }
    }
}
