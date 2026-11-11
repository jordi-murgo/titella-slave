// LF-delimited JSON protocol v1 shared with the master. Faithful port of
// src/titella_slave_assets/wire.py: identical constants, check order and
// error codes. Every other unit depends on this one.
//
// Deliberate hardenings over the Python contract, all invisible to a
// conforming peer (documented in the implementation handoff):
//   - v must be the numeric value 1; Python accepts boolean true because
//     True == 1, this port does not;
//   - the parser keeps at most the last 64 errors instead of an unbounded
//     list (no observable protocol effect);
//   - Base64 whitespace is rejected explicitly instead of relying on
//     Convert.FromBase64String, which ignores it.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Web.Script.Serialization;

    internal static class WireConstants
    {
        public const int ProtocolVersion = 1;
        public const int MaxFrameBytes = 4096;
        public const int FrameBufferBytes = MaxFrameBytes + 1;
        public const int DataChunkBytes = 512;
        public const int DataWindowDefault = 8;
        public const int DataWindowMax = 64;
        public const int MaxRequests = 256;
        public const int MaxDedupEntries = 1024;
        public const int MaxOutboundFrames = 1024;
        public const int MaxParserErrors = 64;

        public const string MasterToFirmware = "master_to_firmware";
        public const string FirmwareToMaster = "firmware_to_master";
        public const string SlaveToFirmware = "slave_to_firmware";
        public const string FirmwareToSlave = "firmware_to_slave";

        public static readonly string[] Channels = new string[]
        {
            "hid", "video", "control", "bootstrap", "shell", "fs", "transfer",
        };

        public static readonly string[] Types = new string[]
        {
            "hello", "heartbeat", "request", "response", "data", "ack", "event", "error",
        };

        // Direction -> channel -> allowed types, verbatim from wire.py:28-47.
        private static readonly Dictionary<string, Dictionary<string, string[]>> DirectionTypes =
            BuildDirectionTypes();

        private static Dictionary<string, string[]> MasterTypes()
        {
            Dictionary<string, string[]> table = new Dictionary<string, string[]>();
            table["control"] = new string[] { "heartbeat", "request", "ack" };
            table["bootstrap"] = new string[] { "request", "data", "ack" };
            table["shell"] = new string[] { "request", "data", "ack" };
            table["fs"] = new string[] { "request", "data", "ack" };
            table["transfer"] = new string[] { "request", "data", "ack" };
            return table;
        }

        private static Dictionary<string, string[]> SlaveTypes()
        {
            Dictionary<string, string[]> table = new Dictionary<string, string[]>();
            table["control"] = new string[] { "hello", "heartbeat", "response", "ack", "event", "error" };
            table["bootstrap"] = new string[] { "response", "ack", "event", "error" };
            table["shell"] = new string[] { "response", "data", "ack", "event", "error" };
            table["fs"] = new string[] { "response", "data", "ack", "event", "error" };
            table["transfer"] = new string[] { "response", "data", "ack", "event", "error" };
            return table;
        }

        private static Dictionary<string, Dictionary<string, string[]>> BuildDirectionTypes()
        {
            Dictionary<string, Dictionary<string, string[]>> table =
                new Dictionary<string, Dictionary<string, string[]>>();

            Dictionary<string, string[]> masterToFirmware = MasterTypes();
            masterToFirmware["hid"] = new string[] { "request" };
            masterToFirmware["video"] = new string[] { "request" };
            table[MasterToFirmware] = masterToFirmware;

            table[FirmwareToSlave] = MasterTypes();

            table[SlaveToFirmware] = SlaveTypes();

            Dictionary<string, string[]> firmwareToMaster = SlaveTypes();
            firmwareToMaster["hid"] = new string[] { "response", "error" };
            firmwareToMaster["video"] = new string[] { "response", "error" };
            table[FirmwareToMaster] = firmwareToMaster;

            return table;
        }

        public static bool Contains(string[] set, string value)
        {
            return Array.IndexOf(set, value) >= 0;
        }

        public static bool DirectionAllows(string direction, string channel, string kind,
                                           out bool knownDirection)
        {
            knownDirection = false;
            Dictionary<string, string[]> channels;
            if (direction == null || !DirectionTypes.TryGetValue(direction, out channels))
            {
                return false;
            }
            knownDirection = true;
            string[] allowed;
            if (!channels.TryGetValue(channel, out allowed))
            {
                return false;
            }
            return Contains(allowed, kind);
        }

        public static bool IsLinkId(string value)
        {
            if (value == null || value.Length != 32)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Malformed frame or one incompatible with protocol v1.</summary>
    internal sealed class WireError : Exception
    {
        private readonly string _code;
        private readonly string _detail;

        public WireError(string code, string detail)
            : base(detail)
        {
            _code = code;
            _detail = detail;
        }

        public string Code
        {
            get { return _code; }
        }

        public string Detail
        {
            get { return _detail; }
        }
    }

    /// <summary>
    /// System.Web.Script.Serialization helpers. A per-thread serializer keeps
    /// the reader, writer, heartbeat and stream threads independent
    /// (JavaScriptSerializer instances are not documented as thread-safe).
    /// MaxJsonLength and RecursionLimit bound the work a 4096-byte frame can
    /// cause; the defaults (2 MiB / 100) are far above the protocol needs.
    /// </summary>
    internal static class Json
    {
        // Inbound frames are bounded to 4096 bytes, so parsing never needs
        // more than a small window; outbound replies are not (a search reply
        // can carry 200 x 1000 chars, a find page 500 paths). Python's
        // json.dumps has no length cap, so the serializer used to produce
        // replies must not impose one either: that was the real defect behind
        // "the length of the string exceeds the value set on the
        // maxJsonLength property" on fs.search/fs.list.
        private const int InboundMaxJsonLength = 65536;
        private const int OutboundMaxJsonLength = 4 * 1024 * 1024;
        private const int RecursionLimit = 8;

        [ThreadStatic]
        private static JavaScriptSerializer _parseSerializer;

        [ThreadStatic]
        private static JavaScriptSerializer _writeSerializer;

        private static JavaScriptSerializer ParseSerializer
        {
            get
            {
                JavaScriptSerializer serializer = _parseSerializer;
                if (serializer == null)
                {
                    serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = InboundMaxJsonLength;
                    serializer.RecursionLimit = RecursionLimit;
                    _parseSerializer = serializer;
                }
                return serializer;
            }
        }

        private static JavaScriptSerializer WriteSerializer
        {
            get
            {
                JavaScriptSerializer serializer = _writeSerializer;
                if (serializer == null)
                {
                    serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = OutboundMaxJsonLength;
                    serializer.RecursionLimit = RecursionLimit;
                    _writeSerializer = serializer;
                }
                return serializer;
            }
        }

        public static object Parse(string text)
        {
            try
            {
                return ParseSerializer.DeserializeObject(text);
            }
            catch (ArgumentException error)
            {
                throw new WireError("invalid_json", error.Message);
            }
            catch (InvalidOperationException error)
            {
                throw new WireError("invalid_json", error.Message);
            }
        }

        public static string Serialize(object value)
        {
            try
            {
                return WriteSerializer.Serialize(value);
            }
            catch (ArgumentException error)
            {
                throw new WireError("invalid_json", error.Message);
            }
            catch (InvalidOperationException error)
            {
                throw new WireError("invalid_json", error.Message);
            }
        }

        public static bool IsObject(object value)
        {
            return value is IDictionary<string, object>;
        }

        public static bool IsArray(object value)
        {
            return value is object[];
        }

        /// <summary>
        /// The deserializer yields int, long, decimal or double depending on
        /// magnitude and fraction. Python's isinstance(value, int) rejects
        /// bool first and floats outright; booleans and strings are rejected
        /// here for the same reason, while integral decimal/double values are
        /// accepted because JavaScriptSerializer may produce them for a
        /// plain integer literal.
        /// </summary>
        public static bool TryGetInt64(object value, out long result)
        {
            result = 0;
            if (value == null || value is bool || value is string)
            {
                return false;
            }
            if (value is int)
            {
                result = (int)value;
                return true;
            }
            if (value is long)
            {
                result = (long)value;
                return true;
            }
            if (value is decimal)
            {
                decimal decimalValue = (decimal)value;
                if (decimalValue != decimal.Truncate(decimalValue) ||
                    decimalValue < long.MinValue || decimalValue > long.MaxValue)
                {
                    return false;
                }
                result = (long)decimalValue;
                return true;
            }
            if (value is double)
            {
                double doubleValue = (double)value;
                if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue) ||
                    doubleValue != Math.Truncate(doubleValue) ||
                    doubleValue < long.MinValue || doubleValue > long.MaxValue)
                {
                    return false;
                }
                result = (long)doubleValue;
                return true;
            }
            return false;
        }

        /// <summary>Absent or null means the default; non-integers also fall
        /// back; valid integers are clamped into [minimum, maximum].</summary>
        public static int GetInt32(IDictionary<string, object> frame, string key,
                                   int minimum, int maximum, int fallback)
        {
            object value;
            long parsed;
            if (frame == null || !frame.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }
            if (!TryGetInt64(value, out parsed))
            {
                return fallback;
            }
            if (parsed < minimum)
            {
                return minimum;
            }
            if (parsed > maximum)
            {
                return maximum;
            }
            return (int)parsed;
        }

        public static long GetInt64(IDictionary<string, object> frame, string key, long fallback)
        {
            object value;
            long parsed;
            if (frame == null || !frame.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }
            if (!TryGetInt64(value, out parsed))
            {
                return fallback;
            }
            return parsed;
        }

        /// <summary>Seconds as a double; invalid or absent means fallback.</summary>
        public static double GetDouble(IDictionary<string, object> frame, string key, double fallback)
        {
            object value;
            if (frame == null || !frame.TryGetValue(key, out value) || value == null || value is bool)
            {
                return fallback;
            }
            if (value is int)
            {
                return (int)value;
            }
            if (value is long)
            {
                return (long)value;
            }
            if (value is decimal)
            {
                return (double)(decimal)value;
            }
            if (value is double)
            {
                return (double)value;
            }
            return fallback;
        }
    }

    /// <summary>Frame validation, encoding and payload helpers.</summary>
    internal static class Wire
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static IDictionary<string, object> ValidateFrame(object frame, string direction)
        {
            IDictionary<string, object> mapping = frame as IDictionary<string, object>;
            if (mapping == null)
            {
                throw new WireError("invalid_frame", "the frame must be a JSON object");
            }

            object versionValue;
            long version;
            mapping.TryGetValue("v", out versionValue);
            if (!Json.TryGetInt64(versionValue, out version) ||
                version != WireConstants.ProtocolVersion)
            {
                throw new WireError("unsupported_version", "only v=1 is accepted");
            }

            object channelValue;
            mapping.TryGetValue("channel", out channelValue);
            string channel = channelValue as string;
            if (channel == null || !WireConstants.Contains(WireConstants.Channels, channel))
            {
                throw new WireError("invalid_channel", "unknown channel");
            }

            object typeValue;
            mapping.TryGetValue("type", out typeValue);
            string kind = typeValue as string;
            if (kind == null || !WireConstants.Contains(WireConstants.Types, kind))
            {
                throw new WireError("invalid_type", "unknown type");
            }

            object linkValue;
            mapping.TryGetValue("link_id", out linkValue);
            string linkId = linkValue as string;
            if (!WireConstants.IsLinkId(linkId))
            {
                throw new WireError("invalid_link_id", "link_id must hold 128 bits in hexadecimal");
            }

            if (direction != null)
            {
                bool knownDirection;
                bool allowed = WireConstants.DirectionAllows(direction, channel, kind, out knownDirection);
                if (!knownDirection)
                {
                    throw new WireError(
                        "invalid_direction",
                        "unknown direction: " + direction);
                }
                if (!allowed)
                {
                    throw new WireError(
                        "direction_denied",
                        channel + "." + kind + " is not allowed in " + direction);
                }
            }

            if (kind == "request" || kind == "response")
            {
                RequireInteger(mapping, "request_id", 1);
                object opValue;
                mapping.TryGetValue("op", out opValue);
                string op = opValue as string;
                if (op == null || op.Length == 0)
                {
                    throw new WireError("invalid_frame", "request/response requires op");
                }
            }
            else if (kind == "data")
            {
                RequireSessionId(mapping, "data requires session_id");
                RequireInteger(mapping, "seq", 0);
                DecodeData(mapping);
            }
            else if (kind == "ack")
            {
                RequireSessionId(mapping, "ack requires session_id");
                RequireInteger(mapping, "ack_seq", 0);
            }

            object bodyValue;
            bool hasBody = mapping.TryGetValue("body", out bodyValue);
            if (hasBody && bodyValue != null && !Json.IsObject(bodyValue))
            {
                throw new WireError("invalid_frame", "body must be an object");
            }

            return new Dictionary<string, object>(mapping);
        }

        private static void RequireInteger(IDictionary<string, object> frame, string key, long minimum)
        {
            object value;
            long parsed;
            frame.TryGetValue(key, out value);
            if (!Json.TryGetInt64(value, out parsed) || parsed < minimum)
            {
                throw new WireError(
                    "invalid_frame",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} must be an integer >= {1}",
                        key,
                        minimum));
            }
        }

        private static void RequireSessionId(IDictionary<string, object> frame, string message)
        {
            object value;
            frame.TryGetValue("session_id", out value);
            string sessionId = value as string;
            if (sessionId == null || sessionId.Length == 0)
            {
                throw new WireError("invalid_frame", message);
            }
        }

        public static byte[] EncodeFrame(object frame, string direction)
        {
            IDictionary<string, object> validated = ValidateFrame(frame, direction);
            string json = Json.Serialize(validated);
            int size = Encoding.UTF8.GetByteCount(json);
            if (size > WireConstants.MaxFrameBytes)
            {
                throw new WireError(
                    "frame_too_large",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "frame of {0} bytes; maximum {1}",
                        size,
                        WireConstants.MaxFrameBytes));
            }
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] payload = new byte[jsonBytes.Length + 1];
            Array.Copy(jsonBytes, payload, jsonBytes.Length);
            payload[payload.Length - 1] = 0x0A;
            return payload;
        }

        public static IDictionary<string, object> DecodeLine(byte[] raw, string direction)
        {
            if (raw.Length > WireConstants.MaxFrameBytes)
            {
                throw new WireError("frame_too_large", "frame above the maximum");
            }
            byte[] trimmed = raw;
            if (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == 0x0D)
            {
                trimmed = new byte[raw.Length - 1];
                Array.Copy(raw, trimmed, trimmed.Length);
            }
            string text;
            try
            {
                text = StrictUtf8.GetString(trimmed);
            }
            catch (DecoderFallbackException error)
            {
                throw new WireError("invalid_json", error.Message);
            }
            object parsed = Json.Parse(text);
            return ValidateFrame(parsed, direction);
        }

        public static string DataB64(byte[] data)
        {
            if (data.Length > WireConstants.DataChunkBytes)
            {
                throw new WireError(
                    "chunk_too_large",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "chunk of {0} bytes; maximum {1}",
                        data.Length,
                        WireConstants.DataChunkBytes));
            }
            return Convert.ToBase64String(data);
        }

        /// <summary>
        /// Python's b64decode(validate=True) rejects whitespace and bad
        /// padding while Convert.FromBase64String silently ignores
        /// whitespace; the pre-check closes that divergence. An empty string
        /// decodes to zero bytes and is valid. Shared with the file service.
        /// </summary>
        internal static bool IsStrictBase64(string value)
        {
            if (value == null || value.Length % 4 != 0)
            {
                return false;
            }
            int padding = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (c == '=')
                {
                    if (index < value.Length - 2)
                    {
                        return false;
                    }
                    padding++;
                    continue;
                }
                bool alphabet =
                    (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '+' || c == '/';
                if (!alphabet)
                {
                    return false;
                }
            }
            return padding <= 2;
        }

        public static byte[] DecodeData(IDictionary<string, object> frame)
        {
            object encodedValue;
            frame.TryGetValue("data_b64", out encodedValue);
            string encoded = encodedValue as string;
            if (encoded == null)
            {
                throw new WireError("invalid_frame", "data requires data_b64");
            }
            if (!IsStrictBase64(encoded))
            {
                throw new WireError("invalid_base64", "data_b64 is not valid base64");
            }
            byte[] data;
            try
            {
                data = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw new WireError("invalid_base64", "data_b64 is not valid base64");
            }
            if (data.Length > WireConstants.DataChunkBytes)
            {
                throw new WireError(
                    "chunk_too_large",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "chunk of {0} bytes; maximum {1}",
                        data.Length,
                        WireConstants.DataChunkBytes));
            }
            return data;
        }

        /// <summary>
        /// Builds the standard frame shape and validates it, as Python's
        /// base_frame does. <paramref name="fields"/> is an alternating
        /// key/value list.
        /// </summary>
        public static IDictionary<string, object> BaseFrame(
            string linkId, string channel, string kind, params object[] fields)
        {
            IDictionary<string, object> frame = new Dictionary<string, object>();
            frame["v"] = WireConstants.ProtocolVersion;
            frame["channel"] = channel;
            frame["type"] = kind;
            frame["link_id"] = linkId;
            if (fields != null)
            {
                if (fields.Length % 2 != 0)
                {
                    throw new WireError("invalid_frame", "frame fields must be key/value pairs");
                }
                for (int index = 0; index < fields.Length; index += 2)
                {
                    frame[(string)fields[index]] = fields[index + 1];
                }
            }
            return ValidateFrame(frame, null);
        }
    }

    /// <summary>
    /// Bounded incremental parser: at most 4096 bytes per line, overflow
    /// discards up to the next LF and resynchronizes. Exactly wire.py:166-195.
    /// </summary>
    internal sealed class FrameParser
    {
        private readonly string _direction;
        private readonly bool _ignoreInvalid;
        private readonly byte[] _buffer;
        private readonly List<WireError> _errors;
        private int _length;
        private bool _discardUntilLf;

        public FrameParser(string direction)
            : this(direction, true)
        {
        }

        public FrameParser(string direction, bool ignoreInvalid)
        {
            _direction = direction;
            _ignoreInvalid = ignoreInvalid;
            _buffer = new byte[WireConstants.MaxFrameBytes];
            _errors = new List<WireError>();
        }

        public IList<WireError> Errors
        {
            get { return _errors; }
        }

        private void Record(WireError error)
        {
            if (_errors.Count >= WireConstants.MaxParserErrors)
            {
                _errors.RemoveAt(0);
            }
            _errors.Add(error);
        }

        public IList<IDictionary<string, object>> Feed(byte[] chunk, int offset, int count)
        {
            List<IDictionary<string, object>> frames = new List<IDictionary<string, object>>();
            int end = offset + count;
            for (int position = offset; position < end; position++)
            {
                byte value = chunk[position];
                if (_discardUntilLf)
                {
                    if (value == 0x0A)
                    {
                        _discardUntilLf = false;
                        _length = 0;
                    }
                    continue;
                }
                if (value == 0x0A)
                {
                    byte[] raw = new byte[_length];
                    Array.Copy(_buffer, raw, _length);
                    _length = 0;
                    if (raw.Length == 0)
                    {
                        continue;
                    }
                    try
                    {
                        frames.Add(Wire.DecodeLine(raw, _direction));
                    }
                    catch (WireError error)
                    {
                        Record(error);
                        if (!_ignoreInvalid)
                        {
                            throw;
                        }
                    }
                    continue;
                }
                if (_length >= WireConstants.MaxFrameBytes)
                {
                    WireError error = new WireError(
                        "frame_too_large", "overflow; discarding up to LF");
                    Record(error);
                    _length = 0;
                    _discardUntilLf = true;
                    if (!_ignoreInvalid)
                    {
                        throw error;
                    }
                    continue;
                }
                _buffer[_length] = value;
                _length++;
            }
            return frames;
        }
    }

    /// <summary>Identity of one data block across a link, a session and a sequence.</summary>
    internal struct DedupKey : IEquatable<DedupKey>
    {
        private readonly string _linkId;
        private readonly string _sessionId;
        private readonly long _seq;

        public DedupKey(string linkId, string sessionId, long seq)
        {
            _linkId = linkId;
            _sessionId = sessionId;
            _seq = seq;
        }

        public bool Equals(DedupKey other)
        {
            return _seq == other._seq &&
                   string.Equals(_linkId, other._linkId, StringComparison.Ordinal) &&
                   string.Equals(_sessionId, other._sessionId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is DedupKey && Equals((DedupKey)obj);
        }

        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 31 + (_linkId == null ? 0 : _linkId.GetHashCode());
            hash = hash * 31 + (_sessionId == null ? 0 : _sessionId.GetHashCode());
            hash = hash * 31 + _seq.GetHashCode();
            return hash;
        }
    }

    /// <summary>
    /// Bounded FIFO window for (link_id, session_id, seq). Seen() never
    /// registers: the key is recorded only after the frame is accepted, so a
    /// rejected gap keeps being rejected and is never fake-acknowledged.
    /// </summary>
    internal sealed class Deduplicator
    {
        private readonly int _limit;
        private readonly Dictionary<DedupKey, LinkedListNode<DedupKey>> _seen;
        private readonly LinkedList<DedupKey> _order;

        public Deduplicator()
            : this(WireConstants.MaxDedupEntries)
        {
        }

        public Deduplicator(int limit)
        {
            if (limit < 1)
            {
                throw new ArgumentOutOfRangeException("limit", "limit must be positive");
            }
            _limit = limit;
            _seen = new Dictionary<DedupKey, LinkedListNode<DedupKey>>();
            _order = new LinkedList<DedupKey>();
        }

        private readonly object _gate = new object();

        public bool Add(DedupKey key)
        {
            lock (_gate)
            {
                if (_seen.ContainsKey(key))
                {
                    return false;
                }
                LinkedListNode<DedupKey> node = _order.AddLast(key);
                _seen[key] = node;
                if (_seen.Count > _limit)
                {
                    DedupKey oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _seen.Remove(oldest);
                }
                return true;
            }
        }

        public bool Seen(DedupKey key)
        {
            lock (_gate)
            {
                return _seen.ContainsKey(key);
            }
        }
    }
}
