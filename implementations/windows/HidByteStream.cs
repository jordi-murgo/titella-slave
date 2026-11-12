// HID data transport: .NET endpoint of the firmware's report 0x10 flow
// plus the Win32 side that finds and opens the vendor collection.
// Report 0x10 (64 bytes with ID, little-endian): [0]=0x10 [1]=version 1
// [2]=class OPEN=1 OPEN_ACK=2 DATA=3 ACK=4 CLOSE=5 [3..6]=epoch uint32
// [7..8]=sequence uint16 per direction [9]=length 0..54 [10..63]=payload.
// State machine mirrors the portable C++ endpoint: one in-flight DATA per
// direction, exact-duplicate re-acknowledgement, payload admission before
// the ACK, controls that overtake data, and 250 ms x3 retries before an
// explicit disconnect. Self-contained (own P/Invokes, C# 5, System and
// System.Core); it never touches the 0x11 loader mailbox.

namespace Titella.Slave
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    internal static class HidWireConstants
    {
        public const byte ReportId = 0x10;
        public const int ReportBytes = 64;
        public const byte ProtocolVersion = 1;
        public const int QueueBytes = 8192;
        public const int DataMax = 54;
        public const uint RetryTimeoutMs = 250u;
        public const int MaxRetries = 3;

        public const int PollMs = 5;
        public const int ReadIdleMs = 100;
        public const int OpenBudgetMs = 1000;
        public const int DisposeJoinMs = 1000;
        public const uint WriteTimeoutMs = 250u;
        // Bounded wait for a cancellation to be confirmed by the driver; never an unbounded completion wait.
        public const uint CancelSettleMs = 250u;

        public const ushort VendorId = 0x17EF;
        public const ushort ProductId = 0xA38F;
        public const ushort UsagePageVendor = 0xFF00;
        public const ushort UsageData = 0x01;
        public const int CollectionReportBytes = 64;

        public const string BindingPrefix = "HID:";
        public const int SerialChars = 12;
    }

    internal enum HidFrameClass : byte
    {
        Open = 1,
        OpenAck = 2,
        Data = 3,
        Ack = 4,
        Close = 5,
    }

    internal enum HidLinkState : byte
    {
        Disconnected = 0,
        Opening = 1,
        Connected = 2,
    }

    internal enum HidStaged : byte
    {
        None = 0,
        Open = 1,
        OpenAck = 2,
        Ack = 3,
        Close = 4,
        DataNew = 5,
        DataRetry = 6,
    }

    internal static class HidErrors
    {
        public static IOException Coded(string code, string message)
        {
            IOException error = new IOException(message);
            error.Data["titella_code"] = code;
            return error;
        }

        public static string CodeOf(Exception error)
        {
            if (error == null || error.Data == null)
            {
                return null;
            }
            if (!error.Data.Contains("titella_code"))
            {
                return null;
            }
            return error.Data["titella_code"] as string;
        }
    }

    internal interface IHidReportIo : IDisposable
    {
        int Read(byte[] report, int timeoutMs);
        void Write(byte[] report);
    }

    public sealed class HidByteStream : Stream
    {
        private static int _epochCounter;

        private readonly object _gate;
        private readonly Func<long> _clock;
        private readonly IHidReportIo _io;
        private readonly Thread _worker;
        private readonly byte[] _inReport;
        private readonly byte[] _outReport;

        private readonly byte[] _txQueue;
        private readonly byte[] _rxQueue;
        private int _txHead;
        private int _txLength;
        private int _rxHead;
        private int _rxLength;

        private HidLinkState _state;
        private uint _epoch;
        private ushort _txSequence;
        private ushort _rxSequence;

        private bool _pendingValid;
        private ushort _pendingSequence;
        private int _pendingLength;
        private int _pendingAttempt;
        private uint _pendingDeadline;
        private readonly byte[] _flightData;
        private readonly byte[] _scratchData;

        private bool _ackPending;
        private ushort _ackSequence;
        private bool _openAckPending;
        private bool _closePending;
        private uint _closeEpoch;

        private bool _openSent;
        private int _openAttempt;
        private uint _openDeadline;

        private bool _lastRxValid;
        private ushort _lastRxSequence;
        private int _lastRxLength;
        private readonly byte[] _lastRxData;

        private HidStaged _staged;
        private int _stagedTake;

        // Cumulative byte counters: Write waits until its own watermark has been acknowledged, so a queue drain...
        private long _txEnqueued;
        private long _txAcknowledged;
        private long _generation;

        private bool _cancelled;
        private bool _disposeRequested;
        private volatile bool _disposedFlag;
        private string _failureCode;
        private string _failureMessage;

        internal HidByteStream(IHidReportIo io, Func<long> clockMilliseconds, uint epoch)
        {
            if (io == null)
            {
                throw new ArgumentNullException("io");
            }
            if (epoch == 0)
            {
                throw new ArgumentException("a non-zero epoch is required", "epoch");
            }
            _gate = new object();
            _io = io;
            _clock = clockMilliseconds ?? new Func<long>(SystemClockMilliseconds);
            _inReport = new byte[HidWireConstants.ReportBytes];
            _outReport = new byte[HidWireConstants.ReportBytes];
            _txQueue = new byte[HidWireConstants.QueueBytes];
            _rxQueue = new byte[HidWireConstants.QueueBytes];
            _flightData = new byte[HidWireConstants.DataMax];
            _scratchData = new byte[HidWireConstants.DataMax];
            _lastRxData = new byte[HidWireConstants.DataMax];
            _epoch = epoch;
            _state = HidLinkState.Opening;
            _openSent = false;
            _openAttempt = 0;
            _openDeadline = 0u;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "titella-hid";
            _worker.Start();
        }

        public static HidByteStream Open(string binding)
        {
            string serial = ParseBinding(binding);
            Win32HidReportIo io = new Win32HidReportIo(serial);
            HidByteStream stream = new HidByteStream(io, SystemClockMilliseconds, NextEpoch());
            stream.WaitForOpen(HidWireConstants.OpenBudgetMs);
            return stream;
        }

        public static bool IsHidBinding(string value)
        {
            if (value == null || value.Length != HidWireConstants.BindingPrefix.Length + HidWireConstants.SerialChars)
            {
                return false;
            }
            if (value[0] != 'H' || value[1] != 'I' || value[2] != 'D' || value[3] != ':')
            {
                return false;
            }
            for (int index = HidWireConstants.BindingPrefix.Length; index < value.Length; index++)
            {
                char current = value[index];
                bool hexadecimal = (current >= '0' && current <= '9')
                    || (current >= 'A' && current <= 'F');
                if (!hexadecimal)
                {
                    return false;
                }
            }
            return true;
        }

        private static string ParseBinding(string binding)
        {
            if (!IsHidBinding(binding))
            {
                throw HidErrors.Coded(
                    "invalid_argument",
                    "the HID binding must be HID:<12 uppercase hexadecimal characters>");
            }
            return binding.Substring(HidWireConstants.BindingPrefix.Length);
        }

        private static long SystemClockMilliseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        private static uint NextEpoch()
        {
            int counter = Interlocked.Increment(ref _epochCounter);
            uint mixed = unchecked((uint)counter * 2654435761u);
            uint time = unchecked((uint)(Stopwatch.GetTimestamp() & 0xFFFFFFFFL));
            uint epoch = mixed ^ time;
            if (epoch == 0)
            {
                epoch = 0xA5A50001u;
            }
            return epoch;
        }

        private void WaitForOpen(int budgetMs)
        {
            long started = Stopwatch.GetTimestamp();
            IOException failure = null;
            lock (_gate)
            {
                while (true)
                {
                    if (_state == HidLinkState.Connected)
                    {
                        return;
                    }
                    if (_failureCode != null)
                    {
                        failure = BuildFailureLocked();
                        break;
                    }
                    long elapsed = (Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency;
                    long remaining = budgetMs - elapsed;
                    if (remaining <= 0)
                    {
                        break;
                    }
                    Monitor.Wait(_gate, (int)Math.Min(remaining, 50L));
                }
            }
            long used = (Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency;
            long left = budgetMs - used;
            Shutdown((int)Math.Max(0L, left));
            if (failure != null)
            {
                throw failure;
            }
            throw HidErrors.Coded(
                "hid_open_timeout",
                "the HID OPEN handshake did not complete within 1000 ms");
        }

        private void Shutdown(int joinBudgetMs)
        {
            bool first = false;
            lock (_gate)
            {
                if (!_disposedFlag)
                {
                    _disposedFlag = true;
                    first = true;
                }
                if (_state != HidLinkState.Connected)
                {
                    _cancelled = true;
                }
                Monitor.PulseAll(_gate);
            }
            if (first && joinBudgetMs > 0)
            {
                Thread worker = _worker;
                if (worker != null && worker != Thread.CurrentThread)
                {
                    worker.Join(joinBudgetMs);
                }
            }
        }

        public override bool CanRead
        {
            get { return !_disposedFlag; }
        }

        public override bool CanWrite
        {
            get { return !_disposedFlag; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override long Length
        {
            get { throw new NotSupportedException("the HID stream has no length"); }
        }

        public override long Position
        {
            get { throw new NotSupportedException("the HID stream is not seekable"); }
            set { throw new NotSupportedException("the HID stream is not seekable"); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("the HID stream is not seekable");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("the HID stream has no length");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count == 0)
            {
                return 0;
            }
            long started = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                while (true)
                {
                    ThrowIfUnavailableLocked();
                    if (_rxLength > 0)
                    {
                        int take = Math.Min(count, _rxLength);
                        PopRing(_rxQueue, ref _rxHead, ref _rxLength, buffer, offset, take);
                        return take;
                    }
                    long elapsed = (Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency;
                    long remaining = HidWireConstants.ReadIdleMs - elapsed;
                    if (remaining <= 0)
                    {
                        return 0;
                    }
                    Monitor.Wait(_gate, (int)Math.Min(remaining, HidWireConstants.ReadIdleMs));
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count == 0)
            {
                return;
            }
            lock (_gate)
            {
                ThrowIfUnavailableLocked();
                long generation = _generation;
                long watermark = _txEnqueued + count;
                int written = 0;
                while (written < count)
                {
                    ThrowIfUnavailableLocked();
                    ThrowIfGenerationLocked(generation);
                    int free = HidWireConstants.QueueBytes - _txLength;
                    if (free > 0)
                    {
                        int take = Math.Min(free, count - written);
                        PushRing(_txQueue, ref _txHead, ref _txLength, buffer, offset + written, take);
                        _txEnqueued += take;
                        written += take;
                        Monitor.PulseAll(_gate);
                    }
                    if (written < count)
                    {
                        Monitor.Wait(_gate, 100);
                    }
                }
                while (true)
                {
                    ThrowIfUnavailableLocked();
                    ThrowIfGenerationLocked(generation);
                    if (_txAcknowledged >= watermark)
                    {
                        return;
                    }
                    Monitor.Wait(_gate, 100);
                }
            }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                ThrowIfUnavailableLocked();
                long generation = _generation;
                while (_txLength > 0 || _pendingValid || _ackPending || _openAckPending
                    || _closePending || _state == HidLinkState.Opening)
                {
                    ThrowIfUnavailableLocked();
                    ThrowIfGenerationLocked(generation);
                    Monitor.Wait(_gate, 100);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            bool first = false;
            lock (_gate)
            {
                if (!_disposedFlag)
                {
                    _disposedFlag = true;
                    first = true;
                }
                if (_state == HidLinkState.Connected)
                {
                    _disposeRequested = true;
                    _closeEpoch = _epoch;
                    ResetFlowLockedKeepClose();
                }
                else
                {
                    _cancelled = true;
                }
                Monitor.PulseAll(_gate);
            }
            if (disposing && first)
            {
                Thread worker = _worker;
                if (worker != null && worker != Thread.CurrentThread)
                {
                    worker.Join(HidWireConstants.DisposeJoinMs);
                }
            }
            base.Dispose(disposing);
        }

        private void ResetFlowLockedKeepClose()
        {
            uint closingEpoch = _closeEpoch;
            ResetFlowLocked();
            _closeEpoch = closingEpoch;
            _closePending = true;
            _state = HidLinkState.Disconnected;
            _epoch = 0u;
        }

        private uint Now()
        {
            return unchecked((uint)_clock());
        }

        private void WorkerLoop()
        {
            try
            {
                while (true)
                {
                    bool stop;
                    bool finishing;
                    lock (_gate)
                    {
                        stop = _cancelled || _failureCode != null;
                        finishing = _disposeRequested && !_closePending;
                    }
                    if (stop || finishing)
                    {
                        break;
                    }
                    PumpOutbound();
                    lock (_gate)
                    {
                        if (_cancelled || _failureCode != null
                            || (_disposeRequested && !_closePending))
                        {
                            break;
                        }
                    }
                    int read;
                    try
                    {
                        read = _io.Read(_inReport, HidWireConstants.PollMs);
                    }
                    catch (IOException error)
                    {
                        FailLocked(error);
                        break;
                    }
                    if (read < 0)
                    {
                        break;
                    }
                    if (read > 0)
                    {
                        if (read != HidWireConstants.ReportBytes)
                        {
                            // A short or oversized report must never reach the parser: the reused buffer can still carry a valid he...
                            Array.Clear(_inReport, 0, _inReport.Length);
                            continue;
                        }
                        lock (_gate)
                        {
                            HandleInboundLocked(_inReport, Now());
                        }
                        continue;
                    }
                }
            }
            finally
            {
                FinishWorker();
            }
        }

        private void PumpOutbound()
        {
            while (true)
            {
                bool staged;
                lock (_gate)
                {
                    if (_cancelled || _failureCode != null)
                    {
                        return;
                    }
                    staged = FillNextReportLocked(Now());
                }
                if (!staged)
                {
                    return;
                }
                try
                {
                    _io.Write(_outReport);
                }
                catch (IOException error)
                {
                    FailLocked(error);
                    return;
                }
                lock (_gate)
                {
                    ConfirmSendLocked(Now());
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private bool FillNextReportLocked(uint now)
        {
            _staged = HidStaged.None;
            if (_closePending)
            {
                BuildReportLocked(HidFrameClass.Close, _closeEpoch, 0, null, 0);
                _staged = HidStaged.Close;
                return true;
            }
            if (_state == HidLinkState.Opening)
            {
                if (!_openSent)
                {
                    BuildReportLocked(HidFrameClass.Open, _epoch, 0, null, 0);
                    _staged = HidStaged.Open;
                    return true;
                }
                if (unchecked((int)(now - _openDeadline)) < 0)
                {
                    return false;
                }
                if (_openAttempt > HidWireConstants.MaxRetries)
                {
                    FailLocked(HidErrors.Coded(
                        "hid_retry_exhausted",
                        "the peer never answered the HID OPEN"));
                    return false;
                }
                BuildReportLocked(HidFrameClass.Open, _epoch, 0, null, 0);
                _staged = HidStaged.Open;
                return true;
            }
            if (_state != HidLinkState.Connected)
            {
                return false;
            }
            if (_openAckPending)
            {
                BuildReportLocked(HidFrameClass.OpenAck, _epoch, 0, null, 0);
                _staged = HidStaged.OpenAck;
                return true;
            }
            if (_ackPending)
            {
                BuildReportLocked(HidFrameClass.Ack, _epoch, _ackSequence, null, 0);
                _staged = HidStaged.Ack;
                return true;
            }
            if (_pendingValid)
            {
                if (unchecked((int)(now - _pendingDeadline)) < 0)
                {
                    return false;
                }
                if (_pendingAttempt > HidWireConstants.MaxRetries)
                {
                    FailLocked(HidErrors.Coded(
                        "hid_retry_exhausted",
                        "the peer never acknowledged the in-flight HID DATA"));
                    return false;
                }
                BuildReportLocked(
                    HidFrameClass.Data, _epoch, _pendingSequence, _flightData, _pendingLength);
                _staged = HidStaged.DataRetry;
                return true;
            }
            if (_txLength > 0)
            {
                int take = Math.Min(_txLength, HidWireConstants.DataMax);
                PeekRing(_txQueue, _txHead, _txLength, _scratchData, take);
                BuildReportLocked(HidFrameClass.Data, _epoch, _txSequence, _scratchData, take);
                _stagedTake = take;
                _staged = HidStaged.DataNew;
                return true;
            }
            return false;
        }

        private void ConfirmSendLocked(uint now)
        {
            switch (_staged)
            {
                case HidStaged.Open:
                    _openSent = true;
                    _openAttempt++;
                    _openDeadline = unchecked(now + HidWireConstants.RetryTimeoutMs);
                    break;
                case HidStaged.OpenAck:
                    _openAckPending = false;
                    break;
                case HidStaged.Ack:
                    _ackPending = false;
                    break;
                case HidStaged.Close:
                    _closePending = false;
                    break;
                case HidStaged.DataNew:
                    StageNewDataLocked(_stagedTake);
                    _pendingDeadline = unchecked(now + HidWireConstants.RetryTimeoutMs);
                    break;
                case HidStaged.DataRetry:
                    _pendingAttempt++;
                    _pendingDeadline = unchecked(now + HidWireConstants.RetryTimeoutMs);
                    break;
                case HidStaged.None:
                    break;
            }
            _staged = HidStaged.None;
        }

        private void StageNewDataLocked(int take)
        {
            PopRing(_txQueue, ref _txHead, ref _txLength, _flightData, 0, take);
            _pendingLength = take;
            _pendingSequence = _txSequence;
            _txSequence = unchecked((ushort)(_txSequence + 1));
            _pendingValid = true;
            _pendingAttempt = 1;
        }

        private void HandleInboundLocked(byte[] report, uint now)
        {
            HidFrameClass frame;
            uint epoch;
            ushort sequence;
            int length;
            if (!ParseReportLocked(report, out frame, out epoch, out sequence, out length))
            {
                return;
            }
            switch (frame)
            {
                case HidFrameClass.Open:
                    if (_state == HidLinkState.Connected && epoch == _epoch)
                    {
                        _openAckPending = true;
                        break;
                    }
                    ResetFlowLocked();
                    _epoch = epoch;
                    _state = HidLinkState.Connected;
                    _openAckPending = true;
                    break;
                case HidFrameClass.OpenAck:
                    if (_state == HidLinkState.Opening && epoch == _epoch)
                    {
                        _state = HidLinkState.Connected;
                        _openSent = false;
                        _openAttempt = 0;
                        _openDeadline = 0u;
                    }
                    break;
                case HidFrameClass.Data:
                    if (_state != HidLinkState.Connected || epoch != _epoch)
                    {
                        break;
                    }
                    if (sequence == _rxSequence)
                    {
                        if (length > HidWireConstants.QueueBytes - _rxLength)
                        {
                            // Backpressure: never copy partially and never acknowledge bytes that were not admitted; the retransmis...
                            break;
                        }
                        PushRing(_rxQueue, ref _rxHead, ref _rxLength, report, 10, length);
                        _lastRxValid = true;
                        _lastRxSequence = sequence;
                        _lastRxLength = length;
                        Array.Copy(report, 10, _lastRxData, 0, length);
                        _rxSequence = unchecked((ushort)(sequence + 1));
                        _ackPending = true;
                        _ackSequence = sequence;
                        break;
                    }
                    if (_lastRxValid && sequence == _lastRxSequence && length == _lastRxLength
                        && SamePayload(report, 10, _lastRxData, length))
                    {
                        _ackPending = true;
                        _ackSequence = sequence;
                    }
                    break;
                case HidFrameClass.Ack:
                    if (_state == HidLinkState.Connected && epoch == _epoch
                        && _pendingValid && sequence == _pendingSequence)
                    {
                        _pendingValid = false;
                        _txAcknowledged += _pendingLength;
                        _pendingLength = 0;
                    }
                    break;
                case HidFrameClass.Close:
                    if (_state != HidLinkState.Disconnected && epoch == _epoch)
                    {
                        FailLocked(HidErrors.Coded(
                            "hid_peer_closed",
                            "the peer closed the HID session"));
                    }
                    break;
            }
            Monitor.PulseAll(_gate);
        }

        private static bool ParseReportLocked(
            byte[] report, out HidFrameClass frame, out uint epoch, out ushort sequence, out int length)
        {
            frame = HidFrameClass.Open;
            epoch = 0u;
            sequence = 0;
            length = 0;
            if (report == null || report.Length < HidWireConstants.ReportBytes)
            {
                return false;
            }
            if (report[0] != HidWireConstants.ReportId)
            {
                return false;
            }
            if (report[1] != HidWireConstants.ProtocolVersion)
            {
                return false;
            }
            epoch = ReadU32(report, 3);
            if (epoch == 0u)
            {
                return false;
            }
            byte klass = report[2];
            if (klass < (byte)HidFrameClass.Open || klass > (byte)HidFrameClass.Close)
            {
                return false;
            }
            int payload = report[9];
            if (klass == (byte)HidFrameClass.Data)
            {
                if (payload == 0 || payload > HidWireConstants.DataMax)
                {
                    return false;
                }
            }
            else if (payload != 0)
            {
                return false;
            }
            frame = (HidFrameClass)klass;
            sequence = ReadU16(report, 7);
            length = payload;
            return true;
        }

        private void BuildReportLocked(
            HidFrameClass frame, uint epoch, ushort sequence, byte[] data, int length)
        {
            Array.Clear(_outReport, 0, HidWireConstants.ReportBytes);
            _outReport[0] = HidWireConstants.ReportId;
            _outReport[1] = HidWireConstants.ProtocolVersion;
            _outReport[2] = (byte)frame;
            WriteU32(_outReport, 3, epoch);
            WriteU16(_outReport, 7, sequence);
            if (data != null && length > 0)
            {
                int take = Math.Min(length, HidWireConstants.DataMax);
                _outReport[9] = (byte)take;
                Array.Copy(data, 0, _outReport, 10, take);
            }
        }

        private void ResetFlowLocked()
        {
            _txHead = 0;
            _txLength = 0;
            _rxHead = 0;
            _rxLength = 0;
            _txSequence = 0;
            _rxSequence = 0;
            _pendingValid = false;
            _pendingLength = 0;
            _pendingAttempt = 0;
            _pendingDeadline = 0u;
            _ackPending = false;
            _ackSequence = 0;
            _openAckPending = false;
            _closePending = false;
            _closeEpoch = 0u;
            _openSent = false;
            _openAttempt = 0;
            _openDeadline = 0u;
            _lastRxValid = false;
            _lastRxLength = 0;
            _staged = HidStaged.None;
            _txAcknowledged = _txEnqueued;
            _generation++;
        }

        private void FailLocked(IOException error)
        {
            lock (_gate)
            {
                if (_failureCode == null)
                {
                    string code = HidErrors.CodeOf(error);
                    _failureCode = code != null ? code : "hid_io_error";
                    _failureMessage = error.Message;
                }
                ResetFlowLocked();
                _state = HidLinkState.Disconnected;
                _epoch = 0u;
                Monitor.PulseAll(_gate);
            }
        }

        private void FinishWorker()
        {
            lock (_gate)
            {
                _cancelled = true;
                if (_failureCode == null && !_disposedFlag)
                {
                    _failureCode = "hid_io_error";
                    _failureMessage = "the HID worker stopped";
                }
                ResetFlowLocked();
                _state = HidLinkState.Disconnected;
                _epoch = 0u;
                Monitor.PulseAll(_gate);
            }
            try
            {
                _io.Dispose();
            }
            catch (Exception)
            {
                // Teardown is best effort: the worker owns every handle and releases them here, on its own thread.
            }
            // Cooperative reclamation.
            Win32HidReportIo native = _io as Win32HidReportIo;
            if (native != null)
            {
                while (!native.CleanupComplete)
                {
                    native.TryReclaimDeferred();
                }
            }
        }

        private void ThrowIfUnavailableLocked()
        {
            if (_disposedFlag)
            {
                throw HidErrors.Coded("hid_closed", "the HID stream was disposed");
            }
            if (_failureCode != null)
            {
                throw BuildFailureLocked();
            }
        }

        private void ThrowIfGenerationLocked(long generation)
        {
            if (_generation != generation)
            {
                throw HidErrors.Coded(
                    "hid_session_reset",
                    "a new HID session began; unacknowledged bytes were discarded");
            }
        }

        private IOException BuildFailureLocked()
        {
            return HidErrors.Coded(_failureCode, _failureMessage);
        }

        private static bool SamePayload(byte[] left, int leftOffset, byte[] right, int length)
        {
            for (int index = 0; index < length; index++)
            {
                if (left[leftOffset + index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static ushort ReadU16(byte[] buffer, int offset)
        {
            return unchecked((ushort)(buffer[offset] | (buffer[offset + 1] << 8)));
        }

        private static uint ReadU32(byte[] buffer, int offset)
        {
            return unchecked((uint)buffer[offset]
                | ((uint)buffer[offset + 1] << 8)
                | ((uint)buffer[offset + 2] << 16)
                | ((uint)buffer[offset + 3] << 24));
        }

        private static void WriteU16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = unchecked((byte)(value & 0xFF));
            buffer[offset + 1] = unchecked((byte)((value >> 8) & 0xFF));
        }

        private static void WriteU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = unchecked((byte)(value & 0xFF));
            buffer[offset + 1] = unchecked((byte)((value >> 8) & 0xFF));
            buffer[offset + 2] = unchecked((byte)((value >> 16) & 0xFF));
            buffer[offset + 3] = unchecked((byte)((value >> 24) & 0xFF));
        }

        private static void PushRing(
            byte[] queue, ref int head, ref int length, byte[] source, int sourceOffset, int count)
        {
            int tail = (head + length) % HidWireConstants.QueueBytes;
            int first = HidWireConstants.QueueBytes - tail;
            if (first > count)
            {
                first = count;
            }
            Array.Copy(source, sourceOffset, queue, tail, first);
            if (count > first)
            {
                Array.Copy(source, sourceOffset + first, queue, 0, count - first);
            }
            length += count;
        }

        private static void PopRing(
            byte[] queue, ref int head, ref int length, byte[] destination,
            int destinationOffset, int count)
        {
            int first = HidWireConstants.QueueBytes - head;
            if (first > count)
            {
                first = count;
            }
            Array.Copy(queue, head, destination, destinationOffset, first);
            if (count > first)
            {
                Array.Copy(queue, 0, destination, destinationOffset + first, count - first);
            }
            head = (head + count) % HidWireConstants.QueueBytes;
            length -= count;
            if (length == 0)
            {
                head = 0;
            }
        }

        private static void PeekRing(byte[] queue, int head, int length, byte[] destination, int count)
        {
            int take = Math.Min(count, length);
            int first = HidWireConstants.QueueBytes - head;
            if (first > take)
            {
                first = take;
            }
            Array.Copy(queue, head, destination, 0, first);
            if (take > first)
            {
                Array.Copy(queue, 0, destination, first, take - first);
            }
        }
    }

    internal sealed class Win32HidReportIo : IHidReportIo
    {
        private readonly string _serial;
        private readonly object _writeGate;
        private readonly byte[] _readBuffer;
        private readonly byte[] _writeBuffer;
        private readonly GCHandle[] _bufferPins;
        private IntPtr _handle;
        private IntPtr _readEvent;
        private IntPtr _writeEvent;
        private IntPtr _readOverlapped;
        private IntPtr _writeOverlapped;
        private bool _readPending;
        private bool _writePending;
        private bool _opened;
        private bool _disposed;
        private bool _cleanupDeferred;
        private Mutex _mutex;
        private bool _mutexHeld;

        public Win32HidReportIo(string serial)
        {
            _serial = serial;
            _writeGate = new object();
            _readBuffer = new byte[HidWireConstants.ReportBytes];
            _writeBuffer = new byte[HidWireConstants.ReportBytes];
            _bufferPins = new GCHandle[2];
            _readOverlapped = IntPtr.Zero;
            _writeOverlapped = IntPtr.Zero;
        }

        public int Read(byte[] report, int timeoutMs)
        {
            if (report == null || report.Length < HidWireConstants.ReportBytes)
            {
                throw new ArgumentException("a 64-byte report buffer is required", "report");
            }
            if (_disposed)
            {
                return -1;
            }
            EnsureOpen();
            if (!_readPending)
            {
                uint immediate;
                bool started = HidInterop.ReadFile(
                    _handle,
                    _bufferPins[0].AddrOfPinnedObject(),
                    (uint)HidWireConstants.ReportBytes,
                    out immediate,
                    _readOverlapped);
                if (started)
                {
                    return CopyReadResult(report, immediate);
                }
                int error = Marshal.GetLastWin32Error();
                if (error != HidNative.ErrorIoPending)
                {
                    throw HidErrors.Coded(
                        "hid_io_error",
                        "ReadFile on the HID collection failed with " + error);
                }
                _readPending = true;
            }
            uint wait = HidInterop.WaitForSingleObject(_readEvent, (uint)Math.Max(timeoutMs, 0));
            if (wait == HidNative.WaitObject0)
            {
                _readPending = false;
                HidInterop.ResetEvent(_readEvent);
                uint transferred;
                if (!HidInterop.GetOverlappedResult(_handle, _readOverlapped, out transferred, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == HidNative.ErrorOperationAborted)
                    {
                        return -1;
                    }
                    throw HidErrors.Coded(
                        "hid_io_error",
                        "GetOverlappedResult after a HID read failed with " + error);
                }
                return CopyReadResult(report, transferred);
            }
            return 0;
        }

        private int CopyReadResult(byte[] report, uint transferred)
        {
            if (transferred == 0)
            {
                return 0;
            }
            if (transferred > HidWireConstants.ReportBytes)
            {
                transferred = (uint)HidWireConstants.ReportBytes;
            }
            Array.Copy(_readBuffer, 0, report, 0, (int)transferred);
            for (int index = (int)transferred; index < HidWireConstants.ReportBytes; index++)
            {
                report[index] = 0;
            }
            return (int)transferred;
        }

        public void Write(byte[] report)
        {
            if (report == null || report.Length < HidWireConstants.ReportBytes)
            {
                throw new ArgumentException("a 64-byte report is required", "report");
            }
            if (_disposed)
            {
                throw HidErrors.Coded("hid_closed", "the HID stream was disposed");
            }
            EnsureOpen();
            lock (_writeGate)
            {
                if (_writePending)
                {
                    // A previous write timed out and its overlapped operation is still owned by the kernel: the pinned buff...
                    if (!WriteCompleted())
                    {
                        throw HidErrors.Coded(
                            "hid_write_timeout",
                            "a previous HID write is still pending");
                    }
                }
                Array.Copy(report, 0, _writeBuffer, 0, HidWireConstants.ReportBytes);
                uint immediate;
                bool started = HidInterop.WriteFile(
                    _handle,
                    _bufferPins[1].AddrOfPinnedObject(),
                    (uint)HidWireConstants.ReportBytes,
                    out immediate,
                    _writeOverlapped);
                if (!started)
                {
                    int pending = Marshal.GetLastWin32Error();
                    if (pending != HidNative.ErrorIoPending)
                    {
                        throw HidErrors.Coded(
                            "hid_io_error",
                            "WriteFile on the HID collection failed with " + pending);
                    }
                    _writePending = true;
                    uint wait = HidInterop.WaitForSingleObject(
                        _writeEvent, HidWireConstants.WriteTimeoutMs);
                    if (wait != HidNative.WaitObject0)
                    {
                        HidInterop.CancelIoEx(_handle, _writeOverlapped);
                        throw HidErrors.Coded(
                            "hid_write_timeout",
                            "the HID write did not complete within 250 ms");
                    }
                    _writePending = false;
                    HidInterop.ResetEvent(_writeEvent);
                }
                uint written;
                if (!HidInterop.GetOverlappedResult(_handle, _writeOverlapped, out written, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == HidNative.ErrorOperationAborted)
                    {
                        throw HidErrors.Coded("hid_closed", "the HID write was cancelled");
                    }
                    throw HidErrors.Coded(
                        "hid_io_error",
                        "GetOverlappedResult after a HID write failed with " + error);
                }
                if (written != HidWireConstants.ReportBytes)
                {
                    throw HidErrors.Coded(
                        "hid_io_error",
                        "short HID write: " + written + " of 64 bytes");
                }
            }
        }

        private bool WriteCompleted()
        {
            if (!_writePending)
            {
                return true;
            }
            if (_handle == IntPtr.Zero)
            {
                return false;
            }
            uint wait = HidInterop.WaitForSingleObject(_writeEvent, 0u);
            if (wait != HidNative.WaitObject0)
            {
                return false;
            }
            uint transferred;
            HidInterop.GetOverlappedResult(_handle, _writeOverlapped, out transferred, false);
            _writePending = false;
            HidInterop.ResetEvent(_writeEvent);
            return true;
        }

        // Cooperative native reclamation, run by the worker's own thread after the device handle was closed (cl...
        private bool ReclaimPendingMemory()
        {
            if (OperationDone(_writePending, _writeEvent))
            {
                _writePending = false;
                if (_writeOverlapped != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_writeOverlapped);
                    _writeOverlapped = IntPtr.Zero;
                }
                if (_writeEvent != IntPtr.Zero)
                {
                    HidInterop.CloseHandle(_writeEvent);
                    _writeEvent = IntPtr.Zero;
                }
                if (_bufferPins[1].IsAllocated)
                {
                    _bufferPins[1].Free();
                }
            }
            if (OperationDone(_readPending, _readEvent))
            {
                _readPending = false;
                if (_readOverlapped != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_readOverlapped);
                    _readOverlapped = IntPtr.Zero;
                }
                if (_readEvent != IntPtr.Zero)
                {
                    HidInterop.CloseHandle(_readEvent);
                    _readEvent = IntPtr.Zero;
                }
                if (_bufferPins[0].IsAllocated)
                {
                    _bufferPins[0].Free();
                }
            }
            return !_writePending && !_readPending;
        }

        public bool TryReclaimDeferred()
        {
            if (!_cleanupDeferred)
            {
                return true;
            }
            if (ReclaimPendingMemory())
            {
                _cleanupDeferred = false;
            }
            return !_cleanupDeferred;
        }

        private bool OperationDone(bool pending, IntPtr completionEvent)
        {
            if (!pending || completionEvent == IntPtr.Zero)
            {
                return true;
            }
            uint wait = HidInterop.WaitForSingleObject(
                completionEvent, HidWireConstants.CancelSettleMs);
            return wait == HidNative.WaitObject0;
        }

        private void EnsureOpen()
        {
            if (_opened)
            {
                return;
            }
            AcquireMutex();
            string path = HidDeviceBrowser.FindDataCollection(_serial);
            IntPtr handle = HidInterop.CreateFileW(
                path,
                HidNative.GenericRead | HidNative.GenericWrite,
                HidNative.FileShareRead | HidNative.FileShareWrite,
                IntPtr.Zero,
                HidNative.OpenExisting,
                HidNative.FileFlagOverlapped,
                IntPtr.Zero);
            if (handle == HidNative.InvalidHandleValue)
            {
                int error = Marshal.GetLastWin32Error();
                throw HidErrors.Coded(
                    "hid_access_denied",
                    "CreateFileW on the HID collection failed with " + error);
            }
            IntPtr readEvent = HidInterop.CreateEventW(IntPtr.Zero, true, false, null);
            IntPtr writeEvent = HidInterop.CreateEventW(IntPtr.Zero, true, false, null);
            if (readEvent == IntPtr.Zero || writeEvent == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (readEvent != IntPtr.Zero)
                {
                    HidInterop.CloseHandle(readEvent);
                }
                if (writeEvent != IntPtr.Zero)
                {
                    HidInterop.CloseHandle(writeEvent);
                }
                HidInterop.CloseHandle(handle);
                throw HidErrors.Coded(
                    "hid_io_error",
                    "CreateEventW for the HID transport failed with " + error);
            }
            _bufferPins[0] = GCHandle.Alloc(_readBuffer, GCHandleType.Pinned);
            _bufferPins[1] = GCHandle.Alloc(_writeBuffer, GCHandleType.Pinned);
            _readOverlapped = AllocateOverlapped(readEvent);
            _writeOverlapped = AllocateOverlapped(writeEvent);
            _handle = handle;
            _readEvent = readEvent;
            _writeEvent = writeEvent;
            _opened = true;
        }

        private static IntPtr AllocateOverlapped(IntPtr eventHandle)
        {
            int size = Marshal.SizeOf(typeof(HidOverlapped));
            IntPtr memory = Marshal.AllocHGlobal(size);
            HidOverlapped overlapped = new HidOverlapped();
            overlapped.Internal = IntPtr.Zero;
            overlapped.InternalHigh = IntPtr.Zero;
            overlapped.Offset = 0u;
            overlapped.OffsetHigh = 0u;
            overlapped.EventHandle = eventHandle;
            Marshal.StructureToPtr(overlapped, memory, false);
            return memory;
        }

        private void AcquireMutex()
        {
            try
            {
                // Windows mutexes are thread-affine: acquired and released
                // only on the I/O (worker) thread; failure is hid_busy.
                _mutex = new Mutex(false, "Local\\Titella.Hid." + _serial);
            }
            catch (Exception error)
            {
                throw HidErrors.Coded(
                    "hid_busy",
                    "the HID consumer mutex could not be created: " + error.Message);
            }
            try
            {
                _mutexHeld = _mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing it: ownership was passed to this thread.
                _mutexHeld = true;
            }
            if (!_mutexHeld)
            {
                throw HidErrors.Coded(
                    "hid_busy",
                    "another consumer holds Local\\Titella.Hid." + _serial);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                HidInterop.CancelIoEx(_handle, IntPtr.Zero);
                HidInterop.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
            if (!ReclaimPendingMemory())
            {
                _cleanupDeferred = true;
            }
            if (_mutexHeld)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Ownership was already lost; nothing to release.
                }
                _mutexHeld = false;
            }
            if (_mutex != null)
            {
                _mutex.Dispose();
                _mutex = null;
            }
        }

        public bool CleanupComplete
        {
            get { return !_cleanupDeferred; }
        }
    }

    internal static class HidDeviceBrowser
    {
        public static string FindDataCollection(string serial)
        {
            Guid hidGuid;
            HidInterop.HidD_GetHidGuid(out hidGuid);
            IntPtr set = HidInterop.SetupDiGetClassDevsW(
                ref hidGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                HidNative.DigcfPresent | HidNative.DigcfDeviceInterface);
            if (set == HidNative.InvalidHandleValue)
            {
                throw HidErrors.Coded(
                    "hid_not_found",
                    "SetupDiGetClassDevsW failed with " + Marshal.GetLastWin32Error());
            }
            try
            {
                HidInterfaceData data = new HidInterfaceData();
                data.Size = Marshal.SizeOf(typeof(HidInterfaceData));
                int exact = 0;
                int incompatible = 0;
                int denied = 0;
                string exactPath = null;
                for (uint index = 0; ; index++)
                {
                    if (!HidInterop.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == HidNative.ErrorNoMoreItems)
                        {
                            break;
                        }
                        throw HidErrors.Coded(
                            "hid_not_found",
                            "SetupDiEnumDeviceInterfaces failed with " + error);
                    }
                    string path = InterfacePath(set, ref data);
                    if (path == null)
                    {
                        continue;
                    }
                    HidProbe probe = Probe(path, serial);
                    if (probe == HidProbe.Exact)
                    {
                        exact++;
                        exactPath = path;
                    }
                    else if (probe == HidProbe.Incompatible)
                    {
                        incompatible++;
                    }
                    else if (probe == HidProbe.Denied && PathCarriesIdentity(path))
                    {
                        denied++;
                    }
                }
                if (exact == 1)
                {
                    return exactPath;
                }
                if (exact > 1)
                {
                    throw HidErrors.Coded(
                        "hid_ambiguous",
                        "several HID data collections match the device serial");
                }
                if (incompatible > 0)
                {
                    throw HidErrors.Coded(
                        "hid_incompatible",
                        "the HID data collection does not expose 64-byte IN/OUT/Feature reports");
                }
                if (denied > 0)
                {
                    throw HidErrors.Coded(
                        "hid_access_denied",
                        "the HID data collection could not be inspected");
                }
                throw HidErrors.Coded(
                    "hid_not_found",
                    "no HID data collection for serial " + serial + " is present");
            }
            finally
            {
                HidInterop.SetupDiDestroyDeviceInfoList(set);
            }
        }

        private static string InterfacePath(IntPtr set, ref HidInterfaceData data)
        {
            uint required;
            HidInterop.SetupDiGetDeviceInterfaceDetailW(
                set, ref data, IntPtr.Zero, 0u, out required, IntPtr.Zero);
            if (required == 0u)
            {
                return null;
            }
            IntPtr buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                // cbSize is 6 on x86 / 8 on x64, but DevicePath always begins
                // at byte offset 4 on BOTH architectures (offset 8 drops the
                // first two UTF-16 chars on x64 and CreateFile would fail).
                const int textOffset = 4;
                uint detailSize = IntPtr.Size == 8 ? 8u : 6u;
                Marshal.WriteInt32(buffer, (int)detailSize);
                if (!HidInterop.SetupDiGetDeviceInterfaceDetailW(
                        set, ref data, buffer, required, out required, IntPtr.Zero))
                {
                    return null;
                }
                return Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + textOffset));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static HidProbe Probe(string path, string serial)
        {
            IntPtr handle = HidInterop.CreateFileW(
                path,
                0u,
                HidNative.FileShareRead | HidNative.FileShareWrite,
                IntPtr.Zero,
                HidNative.OpenExisting,
                0u,
                IntPtr.Zero);
            if (handle == HidNative.InvalidHandleValue)
            {
                return HidProbe.Denied;
            }
            try
            {
                HidAttributes attributes = new HidAttributes();
                attributes.Size = (uint)Marshal.SizeOf(typeof(HidAttributes));
                if (!HidInterop.HidD_GetAttributes(handle, ref attributes))
                {
                    return HidProbe.Denied;
                }
                if (attributes.VendorID != HidWireConstants.VendorId
                    || attributes.ProductID != HidWireConstants.ProductId)
                {
                    return HidProbe.Other;
                }
                string deviceSerial = SerialNumber(handle);
                if (deviceSerial == null || !string.Equals(deviceSerial, serial, StringComparison.Ordinal))
                {
                    return HidProbe.Other;
                }
                IntPtr preparsed;
                if (!HidInterop.HidD_GetPreparsedData(handle, out preparsed))
                {
                    return HidProbe.Incompatible;
                }
                try
                {
                    HidCaps caps = new HidCaps();
                    caps.Reserved = new ushort[17];
                    if (HidInterop.HidP_GetCaps(preparsed, ref caps) != HidNative.HidpStatusSuccess)
                    {
                        return HidProbe.Incompatible;
                    }
                    if (caps.UsagePage != HidWireConstants.UsagePageVendor
                        || caps.Usage != HidWireConstants.UsageData)
                    {
                        return HidProbe.Other;
                    }
                    if (caps.InputReportByteLength != HidWireConstants.CollectionReportBytes
                        || caps.OutputReportByteLength != HidWireConstants.CollectionReportBytes
                        || caps.FeatureReportByteLength != HidWireConstants.CollectionReportBytes)
                    {
                        return HidProbe.Incompatible;
                    }
                    return HidProbe.Exact;
                }
                finally
                {
                    HidInterop.HidD_FreePreparsedData(preparsed);
                }
            }
            finally
            {
                HidInterop.CloseHandle(handle);
            }
        }

        private static string SerialNumber(IntPtr handle)
        {
            StringBuilder buffer = new StringBuilder(128);
            if (!HidInterop.HidD_GetSerialNumberString(handle, buffer, (uint)(buffer.Capacity * 2)))
            {
                return null;
            }
            string serial = buffer.ToString();
            int terminator = serial.IndexOf('\0');
            if (terminator >= 0)
            {
                serial = serial.Substring(0, terminator);
            }
            return serial;
        }

        private static bool PathCarriesIdentity(string path)
        {
            if (path == null)
            {
                return false;
            }
            return path.IndexOf("vid_17ef", StringComparison.OrdinalIgnoreCase) >= 0
                && path.IndexOf("pid_a38f", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal enum HidProbe : byte
    {
        Other = 0,
        Exact = 1,
        Incompatible = 2,
        Denied = 3,
    }

    internal static class HidNative
    {
        public const uint DigcfPresent = 0x00000002u;
        public const uint DigcfDeviceInterface = 0x00000010u;
        public const uint GenericRead = 0x80000000u;
        public const uint GenericWrite = 0x40000000u;
        public const uint FileShareRead = 0x00000001u;
        public const uint FileShareWrite = 0x00000002u;
        public const uint OpenExisting = 3u;
        public const uint FileFlagOverlapped = 0x40000000u;
        public const uint ErrorNoMoreItems = 259u;
        public const int ErrorIoPending = 997;
        public const int ErrorOperationAborted = 995;
        public const uint WaitObject0 = 0u;
        public const int HidpStatusSuccess = 0x00110000;
        public static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }

    internal static class HidInterop
    {
        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetAttributes(IntPtr device, ref HidAttributes attributes);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool HidD_GetSerialNumberString(
            IntPtr device, StringBuilder buffer, uint bufferChars);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsed);

        [DllImport("hid.dll")]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsed, ref HidCaps caps);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid, IntPtr enumerator, IntPtr window, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr set, IntPtr deviceInfo, ref Guid classGuid, uint index,
            ref HidInterfaceData data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr set, ref HidInterfaceData data, IntPtr detail, uint detailBytes,
            out uint required, IntPtr deviceInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateFileW(
            string path, uint access, uint share, IntPtr security, uint disposition,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(
            IntPtr handle, IntPtr buffer, uint bytes, out uint transferred, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(
            IntPtr handle, IntPtr buffer, uint bytes, out uint transferred, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(
            IntPtr handle, IntPtr overlapped, out uint transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEventW(
            IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ResetEvent(IntPtr handle);

        [DllImport("kernel32.dll")]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
