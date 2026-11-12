// Contract tests of the HID data transport. They assert observable
// behaviour of the stream: exact bytes, exact errors, exactly-once delivery,
// acknowledgement ordering and lifetime bounds. The peer is a scripted
// emulation of the firmware endpoint driven through the IHidReportIo seam
// with real queues and an injected clock, so retransmission, wrap and
// failure cases are deterministic.
//
// The same source compiles under the local .NET SDK (C# 5) and on Windows
// with Add-Type and only System.dll/System.Core.dll, so no modern API is
// used here.

namespace Titella.Slave.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;

    /// <summary>Manually advanced protocol clock shared by a test.</summary>
    internal sealed class TestClock
    {
        private readonly object _gate = new object();
        private long _now;

        public long Now()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(long milliseconds)
        {
            lock (_gate)
            {
                _now += milliseconds;
            }
        }
    }

    /// <summary>
    /// Scripted firmware peer behind the IHidReportIo seam. Outbound reports
    /// written by the stream are recorded; inbound reports are queued by the
    /// test. Auto acknowledgements emulate the endpoint's control frames.
    /// </summary>
    internal sealed class FakePeer : IHidReportIo
    {
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _inbound = new Queue<byte[]>();
        private readonly List<byte[]> _outbound = new List<byte[]>();
        private bool _disposed;

        public bool AutoAckOpen;
        public bool AutoAckData;
        public int SuppressOpenAcks;
        public int SuppressDataAcks;
        public int FailDataWrites;

        public FakePeer()
        {
            AutoAckOpen = true;
            AutoAckData = true;
        }

        public int Read(byte[] report, int timeoutMs)
        {
            lock (_gate)
            {
                while (true)
                {
                    if (_disposed)
                    {
                        return -1;
                    }
                    if (_inbound.Count > 0)
                    {
                        byte[] next = _inbound.Dequeue();
                        Array.Copy(next, 0, report, 0, next.Length);
                        return next.Length;
                    }
                    if (timeoutMs <= 0)
                    {
                        return 0;
                    }
                    if (!Monitor.Wait(_gate, timeoutMs))
                    {
                        return 0;
                    }
                }
            }
        }

        public void Write(byte[] report)
        {
            byte[] copy = new byte[HidWireConstants.ReportBytes];
            Array.Copy(report, 0, copy, 0, HidWireConstants.ReportBytes);
            byte frame = copy[2];
            uint epoch = HidTestReports.EpochOf(copy);
            ushort sequence = HidTestReports.SequenceOf(copy);
            lock (_gate)
            {
                if (_disposed)
                {
                    throw HidErrors.Coded("hid_closed", "the peer is closed");
                }
                if (frame == (byte)HidFrameClass.Data && FailDataWrites > 0)
                {
                    FailDataWrites--;
                    throw HidErrors.Coded("hid_io_error", "scripted device failure");
                }
                _outbound.Add(copy);
                if (frame == (byte)HidFrameClass.Open && AutoAckOpen)
                {
                    if (SuppressOpenAcks > 0)
                    {
                        SuppressOpenAcks--;
                    }
                    else
                    {
                        _inbound.Enqueue(HidTestReports.Build(HidFrameClass.OpenAck, epoch, 0, null));
                    }
                }
                else if (frame == (byte)HidFrameClass.Data && AutoAckData)
                {
                    if (SuppressDataAcks > 0)
                    {
                        SuppressDataAcks--;
                    }
                    else
                    {
                        _inbound.Enqueue(
                            HidTestReports.Build(HidFrameClass.Ack, epoch, sequence, null));
                    }
                }
                Monitor.PulseAll(_gate);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>
        /// Completes the OPEN handshake before the stream is constructed: the
        /// worker's first read consumes this OPEN_ACK and the session is
        /// connected without sending a probe payload.
        /// </summary>
        public void PreloadOpenAck(uint epoch)
        {
            PushFrame(HidFrameClass.OpenAck, epoch, 0, null);
        }

        public void Push(byte[] report)
        {
            lock (_gate)
            {
                _inbound.Enqueue(report);
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>
        /// Queues a report of arbitrary length. A short one simulates a
        /// driver that delivered less than 64 bytes while the stream's
        /// reusable buffer still holds the previous report in its tail.
        /// </summary>
        public void PushRaw(byte[] bytes)
        {
            byte[] copy = new byte[bytes.Length];
            Array.Copy(bytes, 0, copy, 0, bytes.Length);
            Push(copy);
        }

        public void PushFrame(HidFrameClass frame, uint epoch, ushort sequence, byte[] payload)
        {
            Push(HidTestReports.Build(frame, epoch, sequence, payload));
        }

        public int OutboundCount(byte frame)
        {
            lock (_gate)
            {
                int count = 0;
                for (int index = 0; index < _outbound.Count; index++)
                {
                    if (_outbound[index][2] == frame)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public byte[] OutboundAt(byte frame, int ordinal)
        {
            lock (_gate)
            {
                int seen = 0;
                for (int index = 0; index < _outbound.Count; index++)
                {
                    if (_outbound[index][2] != frame)
                    {
                        continue;
                    }
                    if (seen == ordinal)
                    {
                        return _outbound[index];
                    }
                    seen++;
                }
                return null;
            }
        }

        public byte[] LastOutbound(byte frame)
        {
            lock (_gate)
            {
                for (int index = _outbound.Count - 1; index >= 0; index--)
                {
                    if (_outbound[index][2] == frame)
                    {
                        return _outbound[index];
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Concatenation of the payloads this peer would apply: a DATA
        /// report whose sequence repeats the last accepted one is a
        /// retransmission and is not applied twice, exactly like the
        /// firmware endpoint's deduplication.
        /// </summary>
        public byte[] DataBytes()
        {
            lock (_gate)
            {
                List<byte> bytes = new List<byte>();
                bool haveLast = false;
                ushort lastSequence = 0;
                for (int index = 0; index < _outbound.Count; index++)
                {
                    byte[] report = _outbound[index];
                    if (report[2] != (byte)HidFrameClass.Data)
                    {
                        continue;
                    }
                    ushort sequence = HidTestReports.SequenceOf(report);
                    if (haveLast && sequence == lastSequence)
                    {
                        continue;
                    }
                    haveLast = true;
                    lastSequence = sequence;
                    int length = report[9];
                    for (int offset = 0; offset < length; offset++)
                    {
                        bytes.Add(report[10 + offset]);
                    }
                }
                return bytes.ToArray();
            }
        }

        /// <summary>The sequence numbers of the recorded DATA reports, in order.</summary>
        public ushort[] DataSequences()
        {
            lock (_gate)
            {
                List<ushort> sequences = new List<ushort>();
                for (int index = 0; index < _outbound.Count; index++)
                {
                    byte[] report = _outbound[index];
                    if (report[2] == (byte)HidFrameClass.Data)
                    {
                        sequences.Add(HidTestReports.SequenceOf(report));
                    }
                }
                return sequences.ToArray();
            }
        }

        public bool WaitFor(int count, byte frame, int timeoutMs)
        {
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                if (OutboundCount(frame) >= count)
                {
                    return true;
                }
                long elapsed = (Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency;
                if (elapsed > timeoutMs)
                {
                    return false;
                }
                Thread.Sleep(1);
            }
        }
    }

    internal static class HidTestReports
    {
        public static byte[] Build(HidFrameClass frame, uint epoch, ushort sequence, byte[] payload)
        {
            byte[] report = new byte[HidWireConstants.ReportBytes];
            report[0] = HidWireConstants.ReportId;
            report[1] = HidWireConstants.ProtocolVersion;
            report[2] = (byte)frame;
            report[3] = unchecked((byte)(epoch & 0xFF));
            report[4] = unchecked((byte)((epoch >> 8) & 0xFF));
            report[5] = unchecked((byte)((epoch >> 16) & 0xFF));
            report[6] = unchecked((byte)((epoch >> 24) & 0xFF));
            report[7] = unchecked((byte)(sequence & 0xFF));
            report[8] = unchecked((byte)((sequence >> 8) & 0xFF));
            if (payload != null && payload.Length > 0)
            {
                report[9] = unchecked((byte)payload.Length);
                Array.Copy(payload, 0, report, 10, payload.Length);
            }
            return report;
        }

        public static uint EpochOf(byte[] report)
        {
            return unchecked((uint)report[3]
                | ((uint)report[4] << 8)
                | ((uint)report[5] << 16)
                | ((uint)report[6] << 24));
        }

        public static ushort SequenceOf(byte[] report)
        {
            return unchecked((ushort)(report[7] | (report[8] << 8)));
        }

        public static int LengthOf(byte[] report)
        {
            return report[9];
        }
    }

    /// <summary>Writer thread that keeps its exception instead of crashing the process.</summary>
    internal sealed class WriteJob
    {
        private readonly Thread _thread;
        private Exception _error;

        public WriteJob(HidByteStream stream, byte[] payload)
        {
            _thread = new Thread(delegate()
            {
                try
                {
                    stream.Write(payload, 0, payload.Length);
                }
                catch (Exception error)
                {
                    _error = error;
                }
            });
            _thread.IsBackground = true;
            _thread.Start();
        }

        public bool IsAlive
        {
            get { return _thread.IsAlive; }
        }

        public Exception Error
        {
            get { return _error; }
        }

        public bool Join(int timeoutMs)
        {
            return _thread.Join(timeoutMs);
        }
    }

    public static class HidProtocolTests
    {
        private const uint Epoch = 0x51ce0001u;
        private const int WaitTimeoutMs = 20000;

        private static int _passed;
        private static int _failed;

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            Run("binding validation", BindingValidation);
            Run("open handshake", OpenHandshake);
            Run("duplicate OPEN re-acknowledges", DuplicateOpenReacknowledges);
            Run("OPEN_ACK loss retransmits OPEN", OpenAckLossRetransmits);
            Run("OPEN retry exhaustion", OpenRetryExhaustion);
            Run("4097-byte transfer", Transfer4097);
            Run("read splits 1/53/54/55", ReadSplits);
            Run("transfer larger than the queue", TransferLargerThanQueue);
            Run("DATA ACK loss retransmits", DataAckLossRetransmits);
            Run("duplicate DATA is re-acknowledged once", DuplicateDataDedup);
            Run("full RX queue backpressures", RxFullBackpressure);
            Run("out-of-sequence DATA dropped", OutOfSequenceDropped);
            Run("short report never feeds the parser", ShortReportIgnored);
            Run("TX sequence wraps 65535 to 0", SequenceWrapTx);
            Run("RX sequence wraps 65535 to 0", SequenceWrapRx);
            Run("old epoch is ignored", OldEpochIgnored);
            Run("new epoch adopts and drops old bytes", NewEpochAdopt);
            Run("simultaneous DATA in both directions", SimultaneousBothDirections);
            Run("peer CLOSE fails the link", PeerClose);
            Run("Dispose with pending reader and writer", DisposePendingReaderWriter);
            Run("device error is observable", DeviceErrorObservable);
            Run("idle Read returns zero, not EOF", IdleReadReturnsZero);
            Run("Flush waits for the acknowledgement", FlushWaitsAck);
            Console.WriteLine();
            Console.WriteLine("hid protocol tests: " + _passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + ": " + error);
            }
        }

        // ------------------------------------------------------------ cases

        private static void BindingValidation()
        {
            Assert(HidByteStream.IsHidBinding("HID:0123456789AB"), "uppercase hex binding");
            Assert(HidByteStream.IsHidBinding("HID:ABCDEF012345"), "digits and letters");
            Assert(!HidByteStream.IsHidBinding("HID:0123456789ab"), "lowercase rejected");
            Assert(!HidByteStream.IsHidBinding("HID:0123456789A"), "eleven characters rejected");
            Assert(!HidByteStream.IsHidBinding("HID:0123456789ABC"), "thirteen rejected");
            Assert(!HidByteStream.IsHidBinding("HID:"), "empty serial rejected");
            Assert(!HidByteStream.IsHidBinding("COM5"), "COM port rejected");
            Assert(!HidByteStream.IsHidBinding("CLIPBOARD"), "clipboard sentinel rejected");
            Assert(!HidByteStream.IsHidBinding(null), "null rejected");
            Assert(!HidByteStream.IsHidBinding("hid:0123456789AB"), "lowercase prefix rejected");
            Assert(!HidByteStream.IsHidBinding("HID:0123456789AG"), "non-hex rejected");

            try
            {
                HidByteStream.Open("COM5");
                throw new InvalidOperationException("a COM port must not open as HID");
            }
            catch (IOException error)
            {
                AssertEqual("invalid_argument", HidErrors.CodeOf(error), "binding error code");
            }
        }

        private static void OpenHandshake()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Open, WaitTimeoutMs), "OPEN was sent");
                byte[] open = peer.OutboundAt((byte)HidFrameClass.Open, 0);
                AssertEqual(HidWireConstants.ReportId, open[0], "OPEN report id");
                AssertEqual(HidWireConstants.ProtocolVersion, open[1], "OPEN version");
                AssertEqual((byte)HidFrameClass.Open, open[2], "OPEN class");
                AssertEqual(Epoch, HidTestReports.EpochOf(open), "OPEN epoch");
                AssertEqual(0, HidTestReports.LengthOf(open), "OPEN carries no payload");

                byte[] payload = new byte[] { 1, 2, 3 };
                stream.Write(payload, 0, payload.Length);
                AssertEqual(1, peer.OutboundCount((byte)HidFrameClass.Data), "one DATA report");
                byte[] data = peer.OutboundAt((byte)HidFrameClass.Data, 0);
                AssertEqual((ushort)0, HidTestReports.SequenceOf(data), "first DATA sequence");
                AssertBytes(payload, peer.DataBytes(), "DATA payload identity");
            }
        }

        private static void DuplicateOpenReacknowledges()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                // A duplicate OPEN of the current epoch only re-acknowledges.
                peer.PushFrame(HidFrameClass.Open, Epoch, 0, null);
                Assert(peer.WaitFor(1, (byte)HidFrameClass.OpenAck, WaitTimeoutMs),
                       "OPEN_ACK sent for the duplicate OPEN");
                AssertEqual(0, peer.OutboundCount((byte)HidFrameClass.Data),
                            "a duplicate OPEN must not touch the data flow");

                byte[] payload = new byte[] { 9, 8 };
                stream.Write(payload, 0, payload.Length);
                byte[] data = peer.OutboundAt((byte)HidFrameClass.Data, 0);
                AssertEqual((ushort)0, HidTestReports.SequenceOf(data),
                            "sequences preserved across the duplicate OPEN");
                AssertBytes(payload, peer.DataBytes(), "payload identity");
            }
        }

        private static void OpenAckLossRetransmits()
        {
            FakePeer peer = new FakePeer();
            peer.SuppressOpenAcks = 1;
            TestClock clock = new TestClock();
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Open, WaitTimeoutMs), "first OPEN");
                Assert(!peer.WaitFor(2, (byte)HidFrameClass.Open, 50), "no retry before the deadline");
                clock.Advance(HidWireConstants.RetryTimeoutMs);
                Assert(peer.WaitFor(2, (byte)HidFrameClass.Open, WaitTimeoutMs),
                       "OPEN retransmitted after 250 ms");
                AssertEqual(
                    HidTestReports.EpochOf(peer.OutboundAt((byte)HidFrameClass.Open, 0)),
                    HidTestReports.EpochOf(peer.OutboundAt((byte)HidFrameClass.Open, 1)),
                    "the retransmission keeps the epoch");

                byte[] payload = new byte[] { 4, 5, 6 };
                stream.Write(payload, 0, payload.Length);
                AssertEqual((ushort)0,
                            HidTestReports.SequenceOf(peer.OutboundAt((byte)HidFrameClass.Data, 0)),
                            "no reset after the recovered OPEN_ACK");
                AssertBytes(payload, peer.DataBytes(), "data flows after recovery");
            }
        }

        private static void OpenRetryExhaustion()
        {
            FakePeer peer = new FakePeer();
            peer.AutoAckOpen = false;
            TestClock clock = new TestClock();
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Open, WaitTimeoutMs), "first OPEN");
                for (int attempt = 0; attempt < HidWireConstants.MaxRetries + 1; attempt++)
                {
                    clock.Advance(HidWireConstants.RetryTimeoutMs);
                    Thread.Sleep(30);
                }
                AssertEqual(1 + HidWireConstants.MaxRetries,
                            peer.OutboundCount((byte)HidFrameClass.Open),
                            "one transmission plus three retries");
                try
                {
                    stream.Write(new byte[] { 1 }, 0, 1);
                    throw new InvalidOperationException("the exhausted OPEN must fail the write");
                }
                catch (IOException error)
                {
                    AssertEqual("hid_retry_exhausted", HidErrors.CodeOf(error), "retry code");
                }
            }
        }

        private static void Transfer4097()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            byte[] payload = Pattern(4097);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                stream.Write(payload, 0, payload.Length);
                AssertBytes(payload, peer.DataBytes(), "4097 bytes in order");
                AssertEqual(76, peer.OutboundCount((byte)HidFrameClass.Data), "54-byte reports");
                byte[] last = peer.OutboundAt((byte)HidFrameClass.Data, 75);
                AssertEqual(47, HidTestReports.LengthOf(last),
                            "final partial report is not truncated");
            }
        }

        private static void ReadSplits()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            byte[] payload = Pattern(4097);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                PushData(peer, Epoch, payload);
                // Wait until the stream admitted every report: each admitted
                // DATA is acknowledged, so the ACK count is the admission
                // signal and the reads below never race an empty queue.
                Assert(peer.WaitFor(76, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "all reports admitted");

                int[] sizes = new int[] { 1, 53, 54, 55 };
                byte[] received = new byte[payload.Length];
                int filled = 0;
                int sizeIndex = 0;
                while (filled < payload.Length)
                {
                    int want = Math.Min(sizes[sizeIndex % sizes.Length], payload.Length - filled);
                    int got = stream.Read(received, filled, want);
                    AssertEqual(want, got, "Read returns exactly the requested count");
                    filled += got;
                    sizeIndex++;
                }
                AssertBytes(payload, received, "read splits keep exact identity and order");
                AssertEqual(0, stream.Read(received, 0, 1), "the queue is empty");
            }
        }

        private static void TransferLargerThanQueue()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            byte[] payload = Pattern(20000);

            // Stream -> peer: the queue is 8192 bytes, so Write refills it
            // while the peer acknowledges report by report.
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                stream.Write(payload, 0, payload.Length);
                AssertBytes(payload, peer.DataBytes(), "20000 bytes out, in order");
                Assert(HidTestReports.LengthOf(peer.OutboundAt((byte)HidFrameClass.Data, 0))
                       <= HidWireConstants.DataMax, "a report never exceeds 54 payload bytes");
                Assert(peer.OutboundCount((byte)HidFrameClass.Ack) == 0,
                       "the stream only acknowledges inbound DATA");
            }

            // Peer -> stream: stop-and-wait admission against the queue.
            FakePeer inboundPeer = new FakePeer();
            TestClock inboundClock = new TestClock();
            inboundPeer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(inboundPeer, inboundClock.Now, Epoch))
            {
                byte[] received = new byte[payload.Length];
                int written = 0;
                int filled = 0;
                int reports = 0;
                ushort sequence = 0;
                while (written < payload.Length)
                {
                    int take = Math.Min(4000, payload.Length - written);
                    for (int offset = 0; offset < take; offset += HidWireConstants.DataMax)
                    {
                        int length = Math.Min(HidWireConstants.DataMax, take - offset);
                        byte[] chunk = new byte[length];
                        Array.Copy(payload, written + offset, chunk, 0, length);
                        inboundPeer.PushFrame(HidFrameClass.Data, Epoch, sequence, chunk);
                        sequence = unchecked((ushort)(sequence + 1));
                        reports++;
                    }
                    written += take;
                    Assert(inboundPeer.WaitFor(reports, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                           "the batch was admitted");
                    int got;
                    while (filled < written
                        && (got = stream.Read(received, filled, written - filled)) > 0)
                    {
                        filled += got;
                    }
                    AssertEqual(written, filled, "the admitted batch is readable");
                }
                AssertBytes(payload, received, "20000 bytes in, in order");
            }
        }

        private static void DataAckLossRetransmits()
        {
            FakePeer peer = new FakePeer();
            peer.SuppressDataAcks = 1;
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                byte[] payload = new byte[] { 11, 12, 13 };
                WriteJob writer = new WriteJob(stream, payload);
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Data, WaitTimeoutMs), "DATA sent");
                Assert(!peer.WaitFor(2, (byte)HidFrameClass.Data, 50), "no retry before the deadline");
                clock.Advance(HidWireConstants.RetryTimeoutMs);
                Assert(peer.WaitFor(2, (byte)HidFrameClass.Data, WaitTimeoutMs),
                       "DATA retransmitted after 250 ms");
                AssertBytes(peer.OutboundAt((byte)HidFrameClass.Data, 0),
                            peer.OutboundAt((byte)HidFrameClass.Data, 1),
                            "the retransmission is byte-identical");
                Assert(writer.Join(WaitTimeoutMs), "the write completes after the ACK");
                Assert(writer.Error == null, "the write reported no error");
                AssertBytes(payload, peer.DataBytes(), "bytes delivered exactly once, in order");
            }
        }

        private static void DuplicateDataDedup()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                byte[] chunk = new byte[] { 7, 7, 7, 7 };
                peer.PushFrame(HidFrameClass.Data, Epoch, 0, chunk);
                peer.PushFrame(HidFrameClass.Data, Epoch, 0, chunk);
                Assert(peer.WaitFor(2, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "the duplicate is re-acknowledged");

                byte[] buffer = new byte[64];
                AssertEqual(chunk.Length, stream.Read(buffer, 0, buffer.Length),
                            "the payload is delivered once");
                byte[] delivered = new byte[chunk.Length];
                Array.Copy(buffer, 0, delivered, 0, chunk.Length);
                AssertBytes(chunk, delivered, "exact payload");
                AssertEqual(0, stream.Read(buffer, 0, 1), "the duplicate was not copied");
            }
        }

        private static void RxFullBackpressure()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                // Fill the 8192-byte RX queue with 54-byte reports: 151
                // reports hold 8154 bytes and leave 38 free.
                for (int index = 0; index < 151; index++)
                {
                    peer.PushFrame(HidFrameClass.Data, Epoch, (ushort)index, Payload54());
                }
                Assert(peer.WaitFor(151, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "every admitted report was acknowledged");

                // One more report does not fit: no ACK, no partial copy.
                peer.PushFrame(HidFrameClass.Data, Epoch, 151, Payload54());
                Thread.Sleep(100);
                AssertEqual(151, peer.OutboundCount((byte)HidFrameClass.Ack),
                            "an inadmissible report is never acknowledged");

                // Drain the queue; the retransmission now fits and is acked.
                byte[] buffer = new byte[200];
                int drained = 0;
                int read;
                while (drained < 8154 && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    drained += read;
                }
                AssertEqual(8154, drained, "the admitted bytes are readable");
                peer.PushFrame(HidFrameClass.Data, Epoch, 151, Payload54());
                Assert(peer.WaitFor(152, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "the retransmission is admitted and acknowledged");
                AssertEqual(54, stream.Read(buffer, 0, buffer.Length),
                            "the retransmitted payload arrives whole");
            }
        }

        private static void OutOfSequenceDropped()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                peer.PushFrame(HidFrameClass.Data, Epoch, 5, new byte[] { 1, 1 });
                Thread.Sleep(100);
                AssertEqual(0, peer.OutboundCount((byte)HidFrameClass.Ack),
                            "an out-of-sequence report is never acknowledged");
                byte[] buffer = new byte[8];
                AssertEqual(0, stream.Read(buffer, 0, buffer.Length),
                            "an out-of-sequence payload is never delivered");

                peer.PushFrame(HidFrameClass.Data, Epoch, 0, new byte[] { 2, 2 });
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "the expected sequence is acknowledged");
                AssertEqual(2, stream.Read(buffer, 0, buffer.Length), "and delivered");
                AssertEqual((byte)2, buffer[0], "payload byte");
            }
        }

        private static void ShortReportIgnored()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                // First a full report so the stream's reusable inbound buffer
                // holds a valid header and payload.
                byte[] full = HidTestReports.Build(
                    HidFrameClass.Data, Epoch, 0, new byte[] { 41, 42, 43 });
                peer.PushRaw(full);
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "the full report was admitted and acknowledged");

                // A short report whose first bytes declare a small payload:
                // its tail still holds the valid previous report. It must be
                // ignored outright — no ACK, no truncated bytes.
                byte[] truncated = new byte[10];
                Array.Copy(full, 0, truncated, 0, truncated.Length);
                truncated[9] = 2;
                peer.PushRaw(truncated);
                Thread.Sleep(100);
                AssertEqual(1, peer.OutboundCount((byte)HidFrameClass.Ack),
                            "a short report is never acknowledged");

                byte[] buffer = new byte[8];
                AssertEqual(3, stream.Read(buffer, 0, buffer.Length),
                            "the short report contributed no bytes");
                AssertEqual((byte)41, buffer[0], "only the admitted payload is present");
                AssertEqual(0, stream.Read(buffer, 0, 1),
                            "nothing of the short report reached the stream");

                // The stream recovers: the next valid report is ordered and
                // acknowledged, so the short one was not treated as EOF.
                byte[] next = HidTestReports.Build(
                    HidFrameClass.Data, Epoch, 1, new byte[] { 51, 52 });
                peer.PushRaw(next);
                Assert(peer.WaitFor(2, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "the following report is acknowledged");
                AssertEqual(2, stream.Read(buffer, 0, buffer.Length), "ordered recovery");
                AssertEqual((byte)51, buffer[0], "recovered payload byte");
            }
        }

        private static void SequenceWrapTx()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            // 65537 full reports: the sequence must pass 65535 and return to
            // zero mid-transfer without losing or duplicating a byte.
            const int reports = 65537;
            byte[] payload = Pattern(reports * HidWireConstants.DataMax);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                stream.Write(payload, 0, payload.Length);
                AssertBytes(payload, peer.DataBytes(), "the wrapped transfer keeps identity");
                ushort[] sequences = peer.DataSequences();
                // Chunking depends on when the writer refills the 8192-byte
                // queue, so the report count is not pinned; the behaviour is.
                Assert(sequences.Length >= (payload.Length + HidWireConstants.DataMax - 1)
                       / HidWireConstants.DataMax,
                       "at least one report per 54-byte chunk");
                for (int index = 0; index < sequences.Length; index++)
                {
                    Assert(HidTestReports.LengthOf(peer.OutboundAt((byte)HidFrameClass.Data, index))
                           <= HidWireConstants.DataMax,
                           "no report exceeds 54 payload bytes");
                }
                bool sawWrap = false;
                for (int index = 1; index < sequences.Length; index++)
                {
                    ushort expected = unchecked((ushort)(sequences[index - 1] + 1));
                    if (expected == 0)
                    {
                        sawWrap = true;
                    }
                    if (expected != sequences[index])
                    {
                        throw new InvalidOperationException(
                            "sequence discontinuity at report " + index);
                    }
                }
                Assert(sawWrap, "the TX sequence wrapped 65535 -> 0");
            }
        }

        private static void SequenceWrapRx()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                // One-byte reports advance rxSequence through the 16-bit wrap;
                // each batch stays well inside the 8192-byte queue.
                const int total = 65536;
                const int batch = 512;
                byte[] buffer = new byte[batch];
                ushort sequence = 0;
                int received = 0;
                int reports = 0;
                for (int sent = 0; sent < total; sent += batch)
                {
                    int take = Math.Min(batch, total - sent);
                    for (int index = 0; index < take; index++)
                    {
                        byte value = unchecked((byte)((sent + index) % 251));
                        peer.PushFrame(HidFrameClass.Data, Epoch, sequence, new byte[] { value });
                        sequence = unchecked((ushort)(sequence + 1));
                        reports++;
                    }
                    Assert(peer.WaitFor(reports, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                           "the batch was admitted at offset " + sent);
                    int got = 0;
                    int read;
                    while (got < take && (read = stream.Read(buffer, 0, take - got)) > 0)
                    {
                        for (int index = 0; index < read; index++)
                        {
                            byte expected = unchecked((byte)((received + index) % 251));
                            if (expected != buffer[index])
                            {
                                throw new InvalidOperationException(
                                    "RX wrap identity at byte " + (received + index));
                            }
                        }
                        got += read;
                        received += read;
                    }
                    AssertEqual(take, got, "the admitted batch was drained");
                }
                AssertEqual(total, received, "every byte of the wrapped RX stream arrived");
                AssertEqual((ushort)0, sequence, "the RX sequence returned to zero");
            }
        }

        private static void OldEpochIgnored()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            uint stale = Epoch - 1;
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                peer.AutoAckData = false;
                byte[] payload = new byte[] { 21, 22 };
                WriteJob writer = new WriteJob(stream, payload);
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Data, WaitTimeoutMs), "DATA in flight");

                // Stale ACK must not release the in-flight DATA.
                peer.PushFrame(HidFrameClass.Ack, stale, 0, null);
                // Stale DATA must not be delivered.
                peer.PushFrame(HidFrameClass.Data, stale, 0, new byte[] { 99 });
                // Stale CLOSE must not kill the link.
                peer.PushFrame(HidFrameClass.Close, stale, 0, null);
                Thread.Sleep(100);
                Assert(writer.IsAlive, "a stale ACK does not release the writer");

                byte[] buffer = new byte[8];
                AssertEqual(0, stream.Read(buffer, 0, buffer.Length), "stale DATA is not delivered");

                // The real ACK still completes the write.
                peer.AutoAckData = true;
                clock.Advance(HidWireConstants.RetryTimeoutMs);
                Assert(writer.Join(WaitTimeoutMs), "the link survived the stale frames");
                Assert(writer.Error == null, "the write succeeded");
                AssertBytes(payload, peer.DataBytes(), "the payload was delivered once");
            }
        }

        private static void NewEpochAdopt()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                peer.AutoAckData = false;
                WriteJob writer = new WriteJob(stream, new byte[] { 31, 32, 33 });
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Data, WaitTimeoutMs), "DATA in flight");

                // An unread inbound payload belongs to the epoch about to be
                // dropped.
                peer.PushFrame(HidFrameClass.Data, Epoch, 0, new byte[] { 44, 45 });

                uint fresh = Epoch + 7;
                peer.PushFrame(HidFrameClass.Open, fresh, 0, null);
                Assert(peer.WaitFor(1, (byte)HidFrameClass.OpenAck, WaitTimeoutMs),
                       "the passive OPEN is acknowledged");
                AssertEqual(fresh,
                            HidTestReports.EpochOf(peer.LastOutbound((byte)HidFrameClass.OpenAck)),
                            "OPEN_ACK carries the adopted epoch");

                byte[] buffer = new byte[8];
                AssertEqual(0, stream.Read(buffer, 0, buffer.Length),
                            "bytes of the previous epoch are not delivered");
                Assert(writer.Join(WaitTimeoutMs), "the writer was released");
                Assert(writer.Error is IOException, "the writer observed an IO failure");
                AssertEqual("hid_session_reset",
                            HidErrors.CodeOf(writer.Error),
                            "a discarded epoch is reported as a session reset");

                // The new epoch is usable: sequences restart at zero.
                peer.AutoAckData = true;
                byte[] payload = new byte[] { 51 };
                stream.Write(payload, 0, payload.Length);
                byte[] data = peer.LastOutbound((byte)HidFrameClass.Data);
                AssertEqual(fresh, HidTestReports.EpochOf(data), "DATA uses the adopted epoch");
                AssertEqual((ushort)0, HidTestReports.SequenceOf(data), "sequences restart");
            }
        }

        private static void SimultaneousBothDirections()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                peer.AutoAckData = false;
                byte[] payload = Pattern(2000);
                WriteJob writer = new WriteJob(stream, payload);
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Data, WaitTimeoutMs), "DATA in flight");

                // The peer sends while our writer waits for its ACK: every
                // inbound report must still be acknowledged, so the ACKs
                // overtake the blocked writer instead of deadlocking.
                for (int index = 0; index < 50; index++)
                {
                    peer.PushFrame(HidFrameClass.Data, Epoch, (ushort)index, Payload54());
                }
                Assert(peer.WaitFor(50, (byte)HidFrameClass.Ack, WaitTimeoutMs),
                       "inbound DATA is acknowledged while a writer waits");
                Assert(writer.IsAlive, "the writer was still waiting");

                byte[] buffer = new byte[8192];
                int drained = 0;
                int read;
                while (drained < 50 * 54 && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    drained += read;
                }
                AssertEqual(50 * 54, drained, "the inbound payload is intact");

                peer.AutoAckData = true;
                clock.Advance(HidWireConstants.RetryTimeoutMs);
                Assert(writer.Join(WaitTimeoutMs), "no deadlock between the two directions");
                Assert(writer.Error == null, "the writer completed without error");
                AssertBytes(payload, peer.DataBytes(), "the outbound payload is intact");
            }
        }

        private static void PeerClose()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                peer.AutoAckData = false;
                WriteJob writer = new WriteJob(stream, new byte[] { 61, 62 });
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Data, WaitTimeoutMs), "DATA in flight");

                peer.PushFrame(HidFrameClass.Close, Epoch, 0, null);
                Assert(writer.Join(WaitTimeoutMs), "the blocked writer failed");
                AssertEqual("hid_peer_closed", HidErrors.CodeOf(writer.Error), "peer close code");
                try
                {
                    stream.Read(new byte[4], 0, 4);
                    throw new InvalidOperationException("the closed link must fail reads");
                }
                catch (IOException error)
                {
                    AssertEqual("hid_peer_closed", HidErrors.CodeOf(error), "read sees the close");
                }
            }
        }

        private static void DisposePendingReaderWriter()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch);
            peer.AutoAckData = false;
            WriteJob writer = new WriteJob(stream, Pattern(300));
            Assert(peer.WaitFor(1, (byte)HidFrameClass.Data, WaitTimeoutMs), "DATA in flight");

            bool readerStopped = false;
            Thread reader = new Thread(delegate()
            {
                try
                {
                    byte[] buffer = new byte[8];
                    while (true)
                    {
                        stream.Read(buffer, 0, buffer.Length);
                    }
                }
                catch (Exception)
                {
                    readerStopped = true;
                }
            });
            reader.IsBackground = true;
            reader.Start();

            long started = Stopwatch.GetTimestamp();
            stream.Dispose();
            long elapsed = (Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency;
            Assert(elapsed <= HidWireConstants.DisposeJoinMs + 500,
                   "Dispose returned within the join bound: " + elapsed + " ms");
            Assert(reader.Join(WaitTimeoutMs), "the pending reader was released");
            Assert(readerStopped, "the reader observed the closed stream");
            Assert(writer.Join(WaitTimeoutMs), "the blocked writer was released");
            AssertEqual("hid_closed", HidErrors.CodeOf(writer.Error), "dispose code for writers");

            try
            {
                stream.Read(new byte[4], 0, 4);
                throw new InvalidOperationException("a disposed stream must fail reads");
            }
            catch (IOException error)
            {
                AssertEqual("hid_closed", HidErrors.CodeOf(error), "dispose code for readers");
            }
        }

        private static void DeviceErrorObservable()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                peer.FailDataWrites = 1;
                IOException error = null;
                try
                {
                    stream.Write(new byte[] { 71, 72 }, 0, 2);
                }
                catch (IOException caught)
                {
                    error = caught;
                }
                Assert(error != null, "the device error surfaced on Write");
                AssertEqual("hid_io_error", HidErrors.CodeOf(error), "device error code");
                try
                {
                    stream.Read(new byte[4], 0, 4);
                    throw new InvalidOperationException("a failed stream must fail reads");
                }
                catch (IOException readError)
                {
                    AssertEqual("hid_io_error", HidErrors.CodeOf(readError),
                                "the failure is sticky for readers");
                }
            }
        }

        private static void IdleReadReturnsZero()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                byte[] buffer = new byte[8];
                long started = Stopwatch.GetTimestamp();
                int read = stream.Read(buffer, 0, buffer.Length);
                long elapsed = (Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency;
                AssertEqual(0, read, "an idle read returns zero");
                Assert(elapsed >= 80, "it waited about the 100 ms idle window: " + elapsed + " ms");
                Assert(elapsed < 1000, "and did not block indefinitely");

                // Zero is not EOF: the stream keeps working afterwards.
                peer.PushFrame(HidFrameClass.Data, Epoch, 0, new byte[] { 81, 82 });
                AssertEqual(2, stream.Read(buffer, 0, buffer.Length), "data still flows");
                AssertEqual((byte)81, buffer[0], "payload byte");
            }
        }

        private static void FlushWaitsAck()
        {
            FakePeer peer = new FakePeer();
            TestClock clock = new TestClock();
            peer.PreloadOpenAck(Epoch);
            using (HidByteStream stream = new HidByteStream(peer, clock.Now, Epoch))
            {
                peer.AutoAckData = false;
                WriteJob writer = new WriteJob(stream, new byte[] { 91, 92, 93 });
                Assert(peer.WaitFor(1, (byte)HidFrameClass.Data, WaitTimeoutMs), "DATA in flight");

                bool flushed = false;
                Thread flusher = new Thread(delegate()
                {
                    try
                    {
                        stream.Flush();
                        flushed = true;
                    }
                    catch (Exception)
                    {
                        flushed = false;
                    }
                });
                flusher.IsBackground = true;
                flusher.Start();
                Thread.Sleep(100);
                Assert(!flushed, "Flush waits for the outstanding acknowledgement");

                peer.AutoAckData = true;
                clock.Advance(HidWireConstants.RetryTimeoutMs);
                Assert(flusher.Join(WaitTimeoutMs), "Flush completed");
                Assert(flushed, "Flush returned after the ACK");
                Assert(writer.Join(WaitTimeoutMs), "the writer completed");
                Assert(writer.Error == null, "the writer reported no error");
            }
        }

        // ----------------------------------------------------------- helpers

        private static void PushData(FakePeer peer, uint epoch, byte[] payload)
        {
            ushort sequence = 0;
            for (int offset = 0; offset < payload.Length; offset += HidWireConstants.DataMax)
            {
                int length = Math.Min(HidWireConstants.DataMax, payload.Length - offset);
                byte[] chunk = new byte[length];
                Array.Copy(payload, offset, chunk, 0, length);
                peer.PushFrame(HidFrameClass.Data, epoch, sequence, chunk);
                sequence = unchecked((ushort)(sequence + 1));
            }
        }

        private static byte[] Payload54()
        {
            byte[] payload = new byte[HidWireConstants.DataMax];
            for (int index = 0; index < payload.Length; index++)
            {
                payload[index] = unchecked((byte)(index + 1));
            }
            return payload;
        }

        private static byte[] Pattern(int length)
        {
            byte[] payload = new byte[length];
            for (int index = 0; index < length; index++)
            {
                payload[index] = (byte)(index % 251);
            }
            return payload;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual(object expected, object actual, string message)
        {
            bool equal;
            if (expected == null)
            {
                equal = actual == null;
            }
            else
            {
                equal = expected.Equals(actual);
            }
            if (!equal)
            {
                throw new InvalidOperationException(
                    message + ": expected " + expected + ", got " + actual);
            }
        }

        private static void AssertBytes(byte[] expected, byte[] actual, string message)
        {
            if (actual == null)
            {
                throw new InvalidOperationException(message + ": no bytes");
            }
            if (expected.Length != actual.Length)
            {
                throw new InvalidOperationException(
                    message + ": expected " + expected.Length + " bytes, got " + actual.Length);
            }
            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                {
                    throw new InvalidOperationException(
                        message + ": byte " + index + " differs ("
                        + expected[index] + " vs " + actual[index] + ")");
                }
            }
        }
    }
}
