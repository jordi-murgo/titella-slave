// Transport and link of the Windows slave: an exclusive Win32 CDC
// transport, an internal Stream seam used by the contract tests, and the
// AgentLink that owns the reader/writer/heartbeat threads, the bounded
// queues, the per-block ACK bookkeeping and the sliding transfer window.
// Port of implementations/python/agent_link.py.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>Raised when the transport can no longer carry bytes.</summary>
    internal sealed class LinkLostException : Exception
    {
        private readonly int _error;

        public LinkLostException(int error, string message)
            : base(message)
        {
            _error = error;
        }

        public int Error
        {
            get { return _error; }
        }
    }

    /// <summary>Raised by AgentLink when an operation is refused or timed out.</summary>
    internal sealed class AgentLinkError : Exception
    {
        public AgentLinkError(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; private set; }
    }

    /// <summary>Minimal transport: three verbs plus liveness.</summary>
    internal interface ILinkTransport : IDisposable
    {
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
        bool IsOpen { get; }
    }

    /// <summary>
    /// Exclusive Win32 CDC. CreateFileW with shareMode 0 reproduces the
    /// Python TIOCEXCL and makes the device single-owner; a NULL security
    /// descriptor makes the handle non-inheritable, which keeps the CDC out
    /// of every CreateProcessW(bInheritHandles: TRUE) launched by the agent.
    /// </summary>
    internal sealed class ComTransport : ILinkTransport
    {
        private readonly string _portName;
        // Reads and writes are independent: each verb holds only its own
        // gate, so a 100 ms read poll never delays a write and a 2 s write
        // never delays a read. Dispose takes both gates in a fixed order, so
        // a handle is never closed while a thread is inside ReadFile or
        // WriteFile with it.
        private readonly object _readGate = new object();
        private readonly object _writeGate = new object();
        private IntPtr _handle;
        private byte[] _readBuffer;
        private byte[] _writeBuffer;

        public ComTransport(string portName)
        {
            if (!IsValidPortName(portName))
            {
                throw new ArgumentException("A CDC port name such as COM7 is required", "portName");
            }
            _portName = portName;
        }

        public static bool IsValidPortName(string value)
        {
            if (value == null || value.Length < 4)
            {
                return false;
            }
            if (!(value[0] == 'C' && value[1] == 'O' && value[2] == 'M'))
            {
                return false;
            }
            if (value[3] < '1' || value[3] > '9')
            {
                return false;
            }
            for (int index = 4; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Opens and configures the native USB CDC port: 115200 8N1, no flow
        /// control, with DTR and RTS asserted because Arduino USB CDC reports
        /// itself connected only while both modem lines are high. The
        /// documented COMMTIMEOUTS poll recipe makes ReadFile return with zero
        /// bytes after 100 ms instead of blocking. Firmware disables the
        /// line-state reboot sequence, so asserting both lines cannot reset it.
        /// </summary>
        public void Open()
        {
            IntPtr handle = NativeMethods.CreateFileW(
                @"\\.\" + _portName,
                Win32Constants.GENERIC_READ | Win32Constants.GENERIC_WRITE,
                0u,
                IntPtr.Zero,
                Win32Constants.OPEN_EXISTING,
                Win32Constants.FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();
                throw new LinkLostException(
                    error,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "CreateFileW({0}) failed with {1}",
                        _portName,
                        error));
            }
            try
            {
                NativeMethods.SetupComm(handle, 8192u, 8192u);

                Win32Structs.DCB dcb = new Win32Structs.DCB();
                dcb.DCBlength = (uint)Marshal.SizeOf(typeof(Win32Structs.DCB));
                if (!NativeMethods.GetCommState(handle, ref dcb))
                {
                    throw new LinkLostException(
                        Marshal.GetLastWin32Error(), "GetCommState failed");
                }
                // Binary mode, DTR and RTS enabled, no parity/flow
                // control/XON/XOFF. DTR_CONTROL_ENABLE occupies bits 4-5;
                // RTS_CONTROL_ENABLE occupies bits 12-13.
                dcb.Flags = 0x00001011u;
                dcb.BaudRate = Win32Constants.CBR_115200;
                dcb.ByteSize = 8;
                dcb.Parity = (byte)Win32Constants.NOPARITY;
                dcb.StopBits = Win32Constants.ONESTOPBIT;
                if (!NativeMethods.SetCommState(handle, ref dcb))
                {
                    throw new LinkLostException(
                        Marshal.GetLastWin32Error(), "SetCommState failed");
                }
                NativeMethods.EscapeCommFunction(handle, Win32Constants.SETDTR);
                NativeMethods.EscapeCommFunction(handle, Win32Constants.SETRTS);

                Win32Structs.COMMTIMEOUTS timeouts = new Win32Structs.COMMTIMEOUTS();
                timeouts.ReadIntervalTimeout = Win32Constants.MAXDWORD;
                timeouts.ReadTotalTimeoutMultiplier = Win32Constants.MAXDWORD;
                timeouts.ReadTotalTimeoutConstant = Win32Constants.READ_POLL_MS;
                timeouts.WriteTotalTimeoutMultiplier = 0u;
                timeouts.WriteTotalTimeoutConstant = Win32Constants.WRITE_TOTAL_MS;
                if (!NativeMethods.SetCommTimeouts(handle, ref timeouts))
                {
                    throw new LinkLostException(
                        Marshal.GetLastWin32Error(), "SetCommTimeouts failed");
                }

                lock (_readGate)
                {
                    lock (_writeGate)
                    {
                        _handle = handle;
                    }
                }
            }
            catch
            {
                NativeMethods.CloseHandle(handle);
                throw;
            }
        }

        public bool IsOpen
        {
            get
            {
                lock (_readGate)
                {
                    return _handle != IntPtr.Zero;
                }
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_readGate)
            {
                IntPtr handle = _handle;
                if (handle == IntPtr.Zero)
                {
                    throw new LinkLostException(0, "the transport is closed");
                }
                if (_readBuffer == null || _readBuffer.Length < count)
                {
                    _readBuffer = new byte[Math.Max(count, WireConstants.MaxFrameBytes)];
                }
                uint read;
                if (!NativeMethods.ReadFile(handle, _readBuffer, (uint)count, out read, IntPtr.Zero))
                {
                    throw new LinkLostException(
                        Marshal.GetLastWin32Error(), "ReadFile failed");
                }
                if (read == 0)
                {
                    return 0;
                }
                Array.Copy(_readBuffer, 0, buffer, offset, (int)read);
                return (int)read;
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (_writeGate)
            {
                IntPtr handle = _handle;
                if (handle == IntPtr.Zero)
                {
                    throw new LinkLostException(0, "the transport is closed");
                }
                if (_writeBuffer == null || _writeBuffer.Length < count)
                {
                    _writeBuffer = new byte[Math.Max(count, WireConstants.FrameBufferBytes)];
                }
                Array.Copy(buffer, offset, _writeBuffer, 0, count);
                int sent = 0;
                while (sent < count)
                {
                    uint written;
                    uint remaining = (uint)(count - sent);
                    if (!NativeMethods.WriteFile(
                            handle, _writeBuffer, remaining, out written, IntPtr.Zero))
                    {
                        throw new LinkLostException(
                            Marshal.GetLastWin32Error(), "WriteFile failed");
                    }
                    if (written == 0)
                    {
                        throw new LinkLostException(0, "short write to the CDC");
                    }
                    sent += (int)written;
                    if (sent < count)
                    {
                        // Shift the tail to the front of the reusable buffer.
                        Array.Copy(_writeBuffer, sent, _writeBuffer, 0, count - sent);
                    }
                }
            }
        }

        public void Dispose()
        {
            IntPtr handle;
            lock (_writeGate)
            {
                lock (_readGate)
                {
                    handle = _handle;
                    _handle = IntPtr.Zero;
                }
            }
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// Internal test seam: the same three verbs over a managed Stream. It is
    /// not selectable from the protocol and carries no network code; a
    /// contract test asserts that the type is not public.
    /// </summary>
    internal sealed class StreamTransport : ILinkTransport
    {
        private readonly Stream _readStream;
        private readonly Stream _writeStream;
        private readonly object _lock = new object();
        private bool _open;

        public StreamTransport(Stream stream)
            : this(stream, stream)
        {
        }

        public StreamTransport(Stream readStream, Stream writeStream)
        {
            if (readStream == null)
            {
                throw new ArgumentNullException("readStream");
            }
            if (writeStream == null)
            {
                throw new ArgumentNullException("writeStream");
            }
            _readStream = readStream;
            _writeStream = writeStream;
            _open = true;
        }

        public bool IsOpen
        {
            get
            {
                lock (_lock)
                {
                    return _open;
                }
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (!_open)
                {
                    throw new LinkLostException(0, "the stream is closed");
                }
                return _readStream.Read(buffer, offset, count);
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (!_open)
                {
                    throw new LinkLostException(0, "the stream is closed");
                }
                _writeStream.Write(buffer, offset, count);
                _writeStream.Flush();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _open = false;
            }
        }
    }

    /// <summary>
    /// Bounded blocking queue. Enqueue with a timeout; the reader uses it for
    /// inbound frames and terminals for their output blocks.
    /// </summary>
    internal sealed class BoundedQueue<T>
    {
        private readonly Queue<T> _items = new Queue<T>();
        private readonly object _gate = new object();
        private readonly int _capacity;

        public BoundedQueue(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }
            _capacity = capacity;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _items.Count;
                }
            }
        }

        public bool TryEnqueue(T item, int timeoutMs)
        {
            long deadline = Environment.TickCount + timeoutMs;
            lock (_gate)
            {
                while (_items.Count >= _capacity)
                {
                    int remaining = (int)(deadline - Environment.TickCount);
                    if (remaining <= 0)
                    {
                        return false;
                    }
                    Monitor.Wait(_gate, remaining);
                }
                _items.Enqueue(item);
                Monitor.PulseAll(_gate);
                return true;
            }
        }

        public bool TryDequeue(int timeoutMs, out T item)
        {
            item = default(T);
            long deadline = Environment.TickCount + timeoutMs;
            lock (_gate)
            {
                while (_items.Count == 0)
                {
                    int remaining = (int)(deadline - Environment.TickCount);
                    if (remaining <= 0)
                    {
                        return false;
                    }
                    Monitor.Wait(_gate, remaining);
                }
                item = _items.Dequeue();
                Monitor.PulseAll(_gate);
                return true;
            }
        }
    }

    /// <summary>
    /// Stable outbound queue: ack/heartbeat first, then hello/response/
    /// error/event, then shell, then everything else. FIFO within a level,
    /// which is exactly Python's (priority, order) tuple ordering.
    /// </summary>
    internal sealed class BoundedPriorityQueue
    {
        private readonly Queue<IDictionary<string, object>>[] _levels;
        private readonly object _gate = new object();
        private readonly int _capacity;
        private int _count;

        public BoundedPriorityQueue(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }
            _capacity = capacity;
            _levels = new Queue<IDictionary<string, object>>[4];
            for (int index = 0; index < _levels.Length; index++)
            {
                _levels[index] = new Queue<IDictionary<string, object>>();
            }
        }

        public static int PriorityOf(IDictionary<string, object> frame)
        {
            string kind = frame["type"] as string;
            if (kind == "ack" || kind == "heartbeat")
            {
                return 0;
            }
            if (kind == "hello" || kind == "response" || kind == "error" || kind == "event")
            {
                return 1;
            }
            if ("shell".Equals(frame["channel"] as string, StringComparison.Ordinal))
            {
                return 2;
            }
            return 3;
        }

        public bool TryEnqueue(IDictionary<string, object> frame, int timeoutMs)
        {
            long deadline = Environment.TickCount + timeoutMs;
            int level = PriorityOf(frame);
            lock (_gate)
            {
                while (_count >= _capacity)
                {
                    int remaining = (int)(deadline - Environment.TickCount);
                    if (remaining <= 0)
                    {
                        return false;
                    }
                    Monitor.Wait(_gate, remaining);
                }
                _levels[level].Enqueue(frame);
                _count++;
                Monitor.PulseAll(_gate);
                return true;
            }
        }

        public bool TryDequeue(int timeoutMs, out IDictionary<string, object> frame)
        {
            frame = null;
            long deadline = Environment.TickCount + timeoutMs;
            lock (_gate)
            {
                while (_count == 0)
                {
                    int remaining = (int)(deadline - Environment.TickCount);
                    if (remaining <= 0)
                    {
                        return false;
                    }
                    Monitor.Wait(_gate, remaining);
                }
                for (int level = 0; level < _levels.Length; level++)
                {
                    if (_levels[level].Count > 0)
                    {
                        frame = _levels[level].Dequeue();
                        _count--;
                        Monitor.PulseAll(_gate);
                        return true;
                    }
                }
                return false;
            }
        }
    }

    /// <summary>(session_id, seq) identity of one data block awaiting its ACK.</summary>
    internal struct AckKey : IEquatable<AckKey>
    {
        private readonly string _sessionId;
        private readonly long _seq;

        public AckKey(string sessionId, long seq)
        {
            _sessionId = sessionId;
            _seq = seq;
        }

        public bool Equals(AckKey other)
        {
            return _seq == other._seq &&
                   string.Equals(_sessionId, other._sessionId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is AckKey && Equals((AckKey)obj);
        }

        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 31 + (_sessionId == null ? 0 : _sessionId.GetHashCode());
            hash = hash * 31 + _seq.GetHashCode();
            return hash;
        }
    }

    /// <summary>
    /// CDC link of the ephemeral agent. One reader, one writer and one
    /// heartbeat thread; bounded inbound/outbound queues; per-block ACK
    /// stop-and-wait for SendData and a sliding window for StreamData.
    /// </summary>
    internal sealed class AgentLink
    {
        private const int AckTimeoutMs = 1000;
        private const int AckRetries = 3;
        private const int QueueTimeoutMs = 1000;

        private static readonly int[] BackoffMs = new int[] { 500, 1000, 2000, 5000 };

        private readonly string _linkId;
        private readonly Func<ILinkTransport> _transportFactory;
        private readonly int _heartbeatMs;
        private readonly int _graceMs;
        private readonly Action _onConnect;
        private readonly Action _onExpired;
        private readonly BoundedQueue<IDictionary<string, object>> _inbound;
        private readonly BoundedPriorityQueue _outbound;
        private readonly ManualResetEventSlim _connected;
        private readonly ManualResetEventSlim _stop;
        private readonly object _ackLock;
        private readonly Dictionary<AckKey, ManualResetEventSlim> _acks;
        private readonly object _dataLock;
        private readonly object _transportLock;
        private readonly List<Thread> _threads;
        private ILinkTransport _transport;
        private long _lastSeenTicks;
        private long _disconnectedSinceTicks;
        private bool _expired;
        private bool _started;

        public AgentLink(string linkId, Func<ILinkTransport> transportFactory,
                         int heartbeatMs, int graceMs,
                         Action onConnect, Action onExpired)
        {
            _linkId = linkId;
            _transportFactory = transportFactory;
            _heartbeatMs = Math.Max(1000, heartbeatMs);
            _graceMs = Math.Max(graceMs, _heartbeatMs);
            _onConnect = onConnect;
            _onExpired = onExpired;
            _inbound = new BoundedQueue<IDictionary<string, object>>(WireConstants.MaxOutboundFrames);
            _outbound = new BoundedPriorityQueue(WireConstants.MaxOutboundFrames);
            _connected = new ManualResetEventSlim(false);
            _stop = new ManualResetEventSlim(false);
            _ackLock = new object();
            _acks = new Dictionary<AckKey, ManualResetEventSlim>();
            _dataLock = new object();
            _transportLock = new object();
            _threads = new List<Thread>();
            _lastSeenTicks = Stopwatch.GetTimestamp();
        }

        public string LinkId
        {
            get { return _linkId; }
        }

        public BoundedQueue<IDictionary<string, object>> Inbound
        {
            get { return _inbound; }
        }

        public bool Expired
        {
            get
            {
                lock (_transportLock)
                {
                    return _expired;
                }
            }
        }

        public bool Connected
        {
            get
            {
                if (!_connected.IsSet)
                {
                    return false;
                }
                ILinkTransport transport = CurrentTransport;
                return transport != null && transport.IsOpen;
            }
        }

        private ILinkTransport CurrentTransport
        {
            get
            {
                lock (_transportLock)
                {
                    return _transport;
                }
            }
        }

        public void Start()
        {
            lock (_transportLock)
            {
                if (_started)
                {
                    return;
                }
                _started = true;
            }
            Thread reader = new Thread(ReaderLoop);
            reader.IsBackground = true;
            reader.Name = "agent-cdc-reader";
            Thread writer = new Thread(WriterLoop);
            writer.IsBackground = true;
            writer.Name = "agent-cdc-writer";
            Thread heartbeat = new Thread(HeartbeatLoop);
            heartbeat.IsBackground = true;
            heartbeat.Name = "agent-heartbeat";
            _threads.Add(reader);
            _threads.Add(writer);
            _threads.Add(heartbeat);
            reader.Start();
            writer.Start();
            heartbeat.Start();
        }

        public void Close()
        {
            _stop.Set();
            _connected.Set();
            DisposeTransport();
            lock (_ackLock)
            {
                foreach (KeyValuePair<AckKey, ManualResetEventSlim> entry in _acks)
                {
                    entry.Value.Set();
                }
                _acks.Clear();
                Monitor.PulseAll(_ackLock);
            }
            foreach (Thread thread in _threads)
            {
                if (thread != Thread.CurrentThread)
                {
                    thread.Join(2000);
                }
            }
        }

        private void DisposeTransport()
        {
            ILinkTransport transport;
            lock (_transportLock)
            {
                transport = _transport;
                _transport = null;
                _connected.Reset();
            }
            if (transport != null)
            {
                try
                {
                    transport.Dispose();
                }
                catch (Exception)
                {
                    // Disposal of an already-dead handle is never fatal.
                }
            }
        }

        private void MarkLost()
        {
            lock (_transportLock)
            {
                if (_disconnectedSinceTicks == 0)
                {
                    _disconnectedSinceTicks = Stopwatch.GetTimestamp();
                }
            }
            DisposeTransport();
        }

        private void OpenTransport()
        {
            ILinkTransport transport = _transportFactory();
            lock (_transportLock)
            {
                _transport = transport;
                // A successful reopen does NOT reset the grace origin: Python
                // keeps counting from the loss moment until a matching frame
                // arrives, so a peer that reopens without sending frames still
                // expires on schedule.
                _connected.Set();
            }
            if (_onConnect != null)
            {
                _onConnect();
            }
        }

        private static long NowTicks()
        {
            return Stopwatch.GetTimestamp();
        }

        private static long MillisecondsToTicks(long milliseconds)
        {
            return (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
        }

        private void ReaderLoop()
        {
            byte[] buffer = new byte[WireConstants.MaxFrameBytes];
            int attempt = 0;
            FrameParser parser = new FrameParser(WireConstants.FirmwareToSlave, true);
            while (!_stop.IsSet)
            {
                ILinkTransport transport = CurrentTransport;
                if (transport == null || !transport.IsOpen)
                {
                    try
                    {
                        OpenTransport();
                        attempt = 0;
                        parser = new FrameParser(WireConstants.FirmwareToSlave, true);
                        transport = CurrentTransport;
                    }
                    catch (Exception)
                    {
                        int delay = BackoffMs[Math.Min(attempt, BackoffMs.Length - 1)];
                        attempt++;
                        if (_stop.Wait(delay))
                        {
                            return;
                        }
                        continue;
                    }
                }
                if (transport == null)
                {
                    continue;
                }
                int count;
                try
                {
                    count = transport.Read(buffer, 0, buffer.Length);
                }
                catch (LinkLostException)
                {
                    MarkLost();
                    continue;
                }
                catch (Exception)
                {
                    MarkLost();
                    continue;
                }
                if (count <= 0)
                {
                    continue;
                }
                IList<IDictionary<string, object>> frames = parser.Feed(buffer, 0, count);
                foreach (IDictionary<string, object> frame in frames)
                {
                    object linkValue;
                    frame.TryGetValue("link_id", out linkValue);
                    if (!string.Equals(linkValue as string, _linkId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    lock (_transportLock)
                    {
                        _lastSeenTicks = NowTicks();
                        _disconnectedSinceTicks = 0;
                    }
                    string kind = frame["type"] as string;
                    if (kind == "ack")
                    {
                        object sessionValue;
                        object seqValue;
                        frame.TryGetValue("session_id", out sessionValue);
                        frame.TryGetValue("ack_seq", out seqValue);
                        long seq;
                        Json.TryGetInt64(seqValue, out seq);
                        AckKey key = new AckKey(sessionValue as string, seq);
                        lock (_ackLock)
                        {
                            ManualResetEventSlim waiter;
                            if (_acks.TryGetValue(key, out waiter))
                            {
                                waiter.Set();
                            }
                            Monitor.PulseAll(_ackLock);
                        }
                    }
                    else
                    {
                        if (!_inbound.TryEnqueue(frame, QueueTimeoutMs))
                        {
                            SendError(frame, "queue_full", "inbound queue full", true);
                        }
                    }
                }
            }
        }

        public void SendFrame(IDictionary<string, object> frame)
        {
            if (_stop.IsSet)
            {
                throw new AgentLinkError("link_closed", "link closed");
            }
            Wire.EncodeFrame(frame, WireConstants.SlaveToFirmware);
            if (!_outbound.TryEnqueue(frame, QueueTimeoutMs))
            {
                throw new AgentLinkError("queue_full", "outbound queue full");
            }
        }

        private void WriterLoop()
        {
            while (!_stop.IsSet)
            {
                IDictionary<string, object> frame;
                if (!_outbound.TryDequeue(250, out frame))
                {
                    continue;
                }
                byte[] payload = Wire.EncodeFrame(frame, WireConstants.SlaveToFirmware);
                while (!_stop.IsSet)
                {
                    if (!_connected.Wait(250))
                    {
                        continue;
                    }
                    ILinkTransport transport = CurrentTransport;
                    if (transport == null)
                    {
                        continue;
                    }
                    try
                    {
                        transport.Write(payload, 0, payload.Length);
                        break;
                    }
                    catch (LinkLostException)
                    {
                        MarkLost();
                    }
                    catch (Exception)
                    {
                        MarkLost();
                    }
                }
            }
        }

        private void HeartbeatLoop()
        {
            while (!_stop.Wait(_heartbeatMs))
            {
                if (Connected)
                {
                    try
                    {
                        SendFrame(Wire.BaseFrame(
                            _linkId, "control", "heartbeat"));
                    }
                    catch (Exception)
                    {
                        // A heartbeat that cannot be queued is not fatal; the
                        // grace check below still decides expiry.
                    }
                }
                long origin;
                lock (_transportLock)
                {
                    origin = _disconnectedSinceTicks != 0
                        ? _disconnectedSinceTicks
                        : _lastSeenTicks;
                }
                if (origin != 0 &&
                    NowTicks() - origin > MillisecondsToTicks(_graceMs))
                {
                    bool alreadyExpired;
                    lock (_transportLock)
                    {
                        alreadyExpired = _expired;
                        _expired = true;
                    }
                    if (!alreadyExpired)
                    {
                        if (_onExpired != null)
                        {
                            _onExpired();
                        }
                        _stop.Set();
                        DisposeTransport();
                        return;
                    }
                }
            }
        }

        public void Response(IDictionary<string, object> request, IDictionary<string, object> body)
        {
            SendFrame(Wire.BaseFrame(
                _linkId,
                request["channel"] as string,
                "response",
                "request_id", request["request_id"],
                "op", request["op"],
                "body", body));
        }

        public void SendError(IDictionary<string, object> request, string code,
                              string message, bool retryable)
        {
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["code"] = code;
            body["message"] = message;
            body["retryable"] = retryable;
            object requestId;
            request.TryGetValue("request_id", out requestId);
            long parsed;
            object channelValue;
            request.TryGetValue("channel", out channelValue);
            string channel = channelValue as string;
            if (channel == null)
            {
                channel = "control";
            }
            if (requestId != null && Json.TryGetInt64(requestId, out parsed))
            {
                object opValue;
                request.TryGetValue("op", out opValue);
                string op = opValue as string;
                if (op == null)
                {
                    op = "unknown";
                }
                SendFrame(Wire.BaseFrame(
                    _linkId, channel, "error",
                    "request_id", requestId,
                    "op", op,
                    "body", body));
            }
            else
            {
                SendFrame(Wire.BaseFrame(
                    _linkId, channel, "error",
                    "body", body));
            }
        }

        public void Event(string channel, string op, string sessionId, IDictionary<string, object> body)
        {
            SendFrame(Wire.BaseFrame(
                _linkId, channel, "event",
                "op", op,
                "session_id", sessionId,
                "body", body));
        }

        public void Ack(string channel, string sessionId, long sequence)
        {
            SendFrame(Wire.BaseFrame(
                _linkId, channel, "ack",
                "session_id", sessionId,
                "ack_seq", sequence));
        }

        /// <summary>
        /// Stop-and-wait per block under the data lock: 1 + 3 resends, one
        /// second each. A large file never interleaves with terminal bytes.
        /// </summary>
        public void SendData(string channel, string sessionId, long sequence,
                             byte[] data, IDictionary<string, object> extra)
        {
            IDictionary<string, object> frame = BuildDataFrame(channel, sessionId, sequence, data, extra);
            AckKey key = new AckKey(sessionId, sequence);
            lock (_dataLock)
            {
                for (int attempt = 0; attempt < AckRetries + 1; attempt++)
                {
                    ManualResetEventSlim waiter = new ManualResetEventSlim(false);
                    lock (_ackLock)
                    {
                        _acks[key] = waiter;
                    }
                    SendFrame(frame);
                    bool acked = waiter.Wait(AckTimeoutMs);
                    lock (_ackLock)
                    {
                        _acks.Remove(key);
                    }
                    if (acked)
                    {
                        return;
                    }
                }
            }
            throw new AgentLinkError(
                "ack_timeout",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ACK timeout for {0}:{1}",
                    sessionId,
                    sequence));
        }

        private IDictionary<string, object> BuildDataFrame(
            string channel, string sessionId, long sequence, byte[] data,
            IDictionary<string, object> extra)
        {
            IDictionary<string, object> frame = new Dictionary<string, object>();
            frame["v"] = WireConstants.ProtocolVersion;
            frame["channel"] = channel;
            frame["type"] = "data";
            frame["link_id"] = _linkId;
            frame["session_id"] = sessionId;
            frame["seq"] = sequence;
            frame["data_b64"] = Wire.DataB64(data);
            if (extra != null)
            {
                foreach (KeyValuePair<string, object> entry in extra)
                {
                    frame[entry.Key] = entry.Value;
                }
            }
            return Wire.ValidateFrame(frame, WireConstants.SlaveToFirmware);
        }

        /// <summary>
        /// Sliding window with per-chunk ACK for a data stream. readChunk is
        /// only called while there is room in the window, so read-ahead is
        /// bounded. Expired blocks are resent in sequence order and abandoned
        /// after AckRetries resends. It does not take the data lock: a file
        /// does not block interactive sends.
        /// </summary>
        public int StreamData(string channel, string sessionId,
                              Func<byte[]> readChunk, int window,
                              Action<int> onAck, Func<bool> stopCheck)
        {
            if (window < 1 || window > WireConstants.DataWindowMax)
            {
                throw new ProtocolError("invalid_argument", "window out of range", false);
            }
            Dictionary<long, StreamBlock> pending = new Dictionary<long, StreamBlock>();
            long nextSeq = 0;
            bool eof = false;
            int ackedBytes = 0;
            try
            {
                while (pending.Count > 0 || !eof)
                {
                    if (_stop.IsSet)
                    {
                        throw new AgentLinkError("link_closed", "link closed");
                    }
                    if (stopCheck != null && stopCheck())
                    {
                        throw new AgentLinkError("cancelled", "transfer cancelled");
                    }
                    while (!eof && pending.Count < window)
                    {
                        byte[] chunk = readChunk();
                        if (chunk == null || chunk.Length == 0)
                        {
                            eof = true;
                            break;
                        }
                        if (chunk.Length > WireConstants.DataChunkBytes)
                        {
                            throw new AgentLinkError("chunk_too_large", "chunk above 512 bytes");
                        }
                        long seq = nextSeq;
                        nextSeq++;
                        ManualResetEventSlim waiter = new ManualResetEventSlim(false);
                        lock (_ackLock)
                        {
                            _acks[new AckKey(sessionId, seq)] = waiter;
                        }
                        StreamBlock block = new StreamBlock();
                        block.Data = chunk;
                        block.Waiter = waiter;
                        block.Attempts = 0;
                        block.DeadlineTicks = NowTicks() + MillisecondsToTicks(AckTimeoutMs);
                        pending[seq] = block;
                        SendFrame(BuildDataFrame(channel, sessionId, seq, chunk, null));
                    }
                    if (pending.Count == 0)
                    {
                        break;
                    }
                    long earliestDeadline = long.MaxValue;
                    lock (_ackLock)
                    {
                        while (true)
                        {
                            if (_stop.IsSet)
                            {
                                break;
                            }
                            bool anySet = false;
                            foreach (StreamBlock block in pending.Values)
                            {
                                if (block.Waiter.IsSet)
                                {
                                    anySet = true;
                                    break;
                                }
                            }
                            if (anySet)
                            {
                                break;
                            }
                            earliestDeadline = long.MaxValue;
                            foreach (StreamBlock block in pending.Values)
                            {
                                if (block.DeadlineTicks < earliestDeadline)
                                {
                                    earliestDeadline = block.DeadlineTicks;
                                }
                            }
                            // Stopwatch ticks -> milliseconds. Monitor.Wait
                            // takes milliseconds; passing ticks stalled the
                            // sender for ~10 million ms whenever a pulse was
                            // missed, which is how a 2 MiB download could lose
                            // its transfer.finished event.
                            long remainingTicks = earliestDeadline - NowTicks();
                            if (remainingTicks <= 0)
                            {
                                break;
                            }
                            int remainingMs = (int)Math.Min(
                                remainingTicks * 1000L / Stopwatch.Frequency + 1L,
                                int.MaxValue);
                            Monitor.Wait(_ackLock, remainingMs);
                        }
                    }
                    long now = NowTicks();
                    List<long> acknowledged = new List<long>();
                    foreach (KeyValuePair<long, StreamBlock> entry in pending)
                    {
                        if (entry.Value.Waiter.IsSet)
                        {
                            acknowledged.Add(entry.Key);
                        }
                    }
                    acknowledged.Sort();
                    foreach (long seq in acknowledged)
                    {
                        StreamBlock block = pending[seq];
                        pending.Remove(seq);
                        lock (_ackLock)
                        {
                            _acks.Remove(new AckKey(sessionId, seq));
                        }
                        ackedBytes += block.Data.Length;
                        if (onAck != null)
                        {
                            onAck(ackedBytes);
                        }
                    }
                    List<long> expired = new List<long>();
                    foreach (KeyValuePair<long, StreamBlock> entry in pending)
                    {
                        if (now >= entry.Value.DeadlineTicks)
                        {
                            expired.Add(entry.Key);
                        }
                    }
                    expired.Sort();
                    foreach (long seq in expired)
                    {
                        StreamBlock block = pending[seq];
                        if (block.Attempts >= AckRetries)
                        {
                            throw new AgentLinkError(
                                "ack_timeout",
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "ACK timeout for {0}:{1}",
                                    sessionId,
                                    seq));
                        }
                        block.Attempts++;
                        block.DeadlineTicks = now + MillisecondsToTicks(AckTimeoutMs);
                        SendFrame(BuildDataFrame(channel, sessionId, seq, block.Data, null));
                    }
                }
                return ackedBytes;
            }
            finally
            {
                lock (_ackLock)
                {
                    foreach (long seq in new List<long>(pending.Keys))
                    {
                        _acks.Remove(new AckKey(sessionId, seq));
                    }
                }
            }
        }

        private sealed class StreamBlock
        {
            public byte[] Data;
            public ManualResetEventSlim Waiter;
            public int Attempts;
            public long DeadlineTicks;
        }
    }
}
