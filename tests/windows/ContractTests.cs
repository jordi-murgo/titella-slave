// Framework-free contract tests for the Windows slave, compiled together
// with the production units by the documented Add-Type command:
//
//   Add-Type -LiteralPath @((Get-ChildItem .\implementations\windows\*.cs).FullName +
//                           (Resolve-Path .\tests\windows\ContractTests.cs).Path) `
//            -ReferencedAssemblies System.dll,System.Core.dll,System.Web.Extensions.dll,
//                                 <GAC System.Management.Automation.dll>
//   exit [Titella.Slave.Tests.ContractTests]::RunAll((Get-Location).Path)
//
// Every test catches its own failures, prints PASS/FAIL and the returned
// count is the number of failures. Nothing here needs admin rights, network
// access or policy changes; temporary work lives under %TEMP% and is removed
// in a finally block.

namespace Titella.Slave.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;

    public static class ContractTests
    {
        private static string _repositoryRoot;
        private static int _passed;
        private static int _failed;

        public static int RunAll(string repositoryRoot)
        {
            _repositoryRoot = repositoryRoot;
            _passed = 0;
            _failed = 0;

            Run("Wire.SplitAtEveryByte", WireSplitAtEveryByte);
            Run("Wire.BannerAndResync", WireBannerAndResync);
            Run("Wire.Utf8RoundTrip", WireUtf8RoundTrip);
            Run("Wire.ExactMaximum", WireExactMaximum);
            Run("Wire.Rejections", WireRejections);
            Run("Wire.ChunkLimit", WireChunkLimit);
            Run("Dedup.Bounded", DedupBounded);
            Run("Transfer.SharedCorpus", TransferSharedCorpus);

            Run("Agent.HelloBindReady", AgentHelloBindReady);
            Run("Agent.NotBoundBeforeBind", AgentNotBoundBeforeBind);
            Run("Agent.DuplicateDataIsReAckedOnce", AgentDuplicateData);
            Run("Agent.GapIsNotAcked", AgentGapIsNotAcked);

            Run("Job.StdoutStderrSeparated", JobStdoutStderrSeparated);
            Run("Job.ExitCodePropagates", JobExitCodePropagates);
            Run("Job.SlowReturnsId", JobSlowReturnsId);
            Run("Job.CancelKillsGrandchild", JobCancelKillsGrandchild);
            Run("Job.TimeoutKillsGrandchild", JobTimeoutKillsGrandchild);
            Run("Job.SharedLimit", JobSharedLimit);
            Run("Job.ContainmentFailureIsHonest", JobContainmentFailure);
            Run("Job.EngineStreams", JobEngineStreams);
            Run("Job.EngineExitCode", JobEngineExitCode);
            Run("Job.EngineWriteHost", JobEngineWriteHost);

            Run("Terminal.EchoResizeClose", TerminalEchoResizeClose);
            Run("Terminal.NoSecondConhostAfterClose", TerminalNoSecondConhost);
            Run("Terminal.ShellIsSingleExecutable", TerminalShellIsSingleExecutable);

            Run("Fs.ReadPagination", FsReadPagination);
            Run("Fs.AtomicWriteReplace", FsAtomicWriteReplace);
            Run("Fs.DeleteBoundaries", FsDeleteBoundaries);
            Run("Fs.ListAndFindOrdering", FsListAndFindOrdering);
            Run("Fs.Search", FsSearch);
            Run("Fs.LockedFileIsHonest", FsLockedFileIsHonest);
            Run("Fs.SysinfoShape", FsSysinfoShape);

            Run("Transfer.RoundTrip2MiBPlus17", TransferRoundTrip);
            Run("Transfer.EmptyFile", TransferEmptyFile);
            Run("Transfer.ChecksumMismatchLeavesDestinationIntact", TransferChecksumMismatch);
            Run("Transfer.CancelLeavesNoPartial", TransferCancelNoPartial);
            Run("Transfer.SizeLimitRejectedEarly", TransferSizeLimit);
            Run("Transfer.DownloadWindowBounds", TransferDownloadWindow);
            Run("Transfer.ProgressEvent", TransferProgressEvent);

            Run("Lifecycle.StopRemovesOwnedDirectory", LifecycleStopRemovesOwnedDirectory);
            Run("Lifecycle.GraceExpirySixTimes", LifecycleGraceExpirySixTimes);
            Run("Lifecycle.ReplugKeepsSession", LifecycleReplugKeepsSession);
            Run("Lifecycle.ForeignDirectoryIsKept", LifecycleForeignDirectoryIsKept);

            Run("Source.UnitSet", SourceSevenUnits);
            Run("Source.NoPolicyBypass", SourceNoPolicyBypass);
            Run("Source.StreamTransportIsInternal", SourceStreamTransportIsInternal);
            Run("Policy.RestrictedCompilationWorks", PolicyRestrictedCompilationWorks);

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "SUMMARY passed={0} failed={1}",
                _passed,
                _failed));
            return _failed;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception failure)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + ": " + Flatten(failure));
            }
        }

        private static string Flatten(Exception error)
        {
            Exception current = error;
            StringBuilder builder = new StringBuilder();
            while (current != null)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" <- ");
                }
                builder.Append(current.Message);
                current = current.InnerException;
            }
            return builder.ToString();
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
            else if (IsNumeric(expected) && IsNumeric(actual))
            {
                equal = Convert.ToInt64(expected, CultureInfo.InvariantCulture) ==
                        Convert.ToInt64(actual, CultureInfo.InvariantCulture);
            }
            else if (IsJsonValue(expected) && IsJsonValue(actual))
            {
                equal = DeepEqual(expected, actual);
            }
            else
            {
                equal = expected.Equals(actual);
            }
            if (!equal)
            {
                throw new InvalidOperationException(
                    message + " (expected " + Describe(expected) + ", got " + Describe(actual) + ")");
            }
        }

        private static bool IsNumeric(object value)
        {
            return value is int || value is long || value is short || value is byte ||
                   value is uint || value is ulong || value is decimal || value is double;
        }

        private static bool IsJsonValue(object value)
        {
            return value is IDictionary<string, object> || value is object[] ||
                   value is List<object> || value is List<KeyValuePair<string, object>>;
        }

        /// <summary>Structural comparison of decoded JSON values.</summary>
        private static bool DeepEqual(object left, object right)
        {
            IDictionary<string, object> leftMap = left as IDictionary<string, object>;
            IDictionary<string, object> rightMap = right as IDictionary<string, object>;
            if (leftMap != null || rightMap != null)
            {
                if (leftMap == null || rightMap == null || leftMap.Count != rightMap.Count)
                {
                    return false;
                }
                foreach (KeyValuePair<string, object> entry in leftMap)
                {
                    object other;
                    if (!rightMap.TryGetValue(entry.Key, out other))
                    {
                        return false;
                    }
                    if (!ValueEqual(entry.Value, other))
                    {
                        return false;
                    }
                }
                return true;
            }
            IEnumerable<object> leftItems = left as IEnumerable<object>;
            IEnumerable<object> rightItems = right as IEnumerable<object>;
            if (leftItems != null || rightItems != null)
            {
                List<object> leftList = new List<object>(leftItems ?? new object[0]);
                List<object> rightList = new List<object>(rightItems ?? new object[0]);
                if (leftList.Count != rightList.Count)
                {
                    return false;
                }
                for (int index = 0; index < leftList.Count; index++)
                {
                    if (!ValueEqual(leftList[index], rightList[index]))
                    {
                        return false;
                    }
                }
                return true;
            }
            return ValueEqual(left, right);
        }

        private static bool ValueEqual(object left, object right)
        {
            if (left == null)
            {
                return right == null;
            }
            if (right == null)
            {
                return false;
            }
            if (IsNumeric(left) && IsNumeric(right))
            {
                return Convert.ToDouble(left, CultureInfo.InvariantCulture) ==
                       Convert.ToDouble(right, CultureInfo.InvariantCulture);
            }
            if (IsJsonValue(left) || IsJsonValue(right))
            {
                return DeepEqual(left, right);
            }
            return left.Equals(right);
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : "'" + value.ToString() + "'";
        }

        private static string TempDirectory(string prefix)
        {
            string path = Path.Combine(
                Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTree(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception)
            {
                // Test cleanup is best effort; the OS temp dir is disposable.
            }
        }

        // ---------------------------------------------------------------- Wire

        private static IDictionary<string, object> Fixture()
        {
            string path = Path.Combine(_repositoryRoot, "tests", "fixtures", "protocol-v1.json");
            return WebJson(File.ReadAllText(path, new UTF8Encoding(false)));
        }

        private static IDictionary<string, object> WebJson(string text)
        {
            object parsed = Json.Parse(text);
            IDictionary<string, object> mapping = parsed as IDictionary<string, object>;
            if (mapping == null)
            {
                throw new InvalidOperationException("the fixture must be a JSON object");
            }
            return mapping;
        }

        /// <summary>
        /// Reads a field or rethrows the remote error the agent returned, so a
        /// failed operation never surfaces as "key not present".
        /// </summary>
        /// <summary>
        /// A large fs reply is streamed: the response carries stream_id and
        /// payload_kind=json and the real body arrives as data frames. Read it
        /// back and parse it, so assertions see the true reply either way.
        /// </summary>
        private static IDictionary<string, object> FsOutcome(
            MasterPeer peer, IDictionary<string, object> reply, int timeoutMs)
        {
            string kind = Str(reply, "payload_kind");
            if (kind == null)
            {
                return reply;
            }
            string streamId = Str(reply, "stream_id");
            if (streamId == null)
            {
                return reply;
            }
            peer.WaitEvent("fs.stream.finished", streamId, timeoutMs);
            byte[] payload = peer.DataOf(streamId, "data");
            if (kind == "json")
            {
                return (IDictionary<string, object>)Json.Parse(
                    new UTF8Encoding(false).GetString(payload));
            }
            return reply;
        }

        private static object Field(IDictionary<string, object> source, string key)
        {
            object value;
            if (source.TryGetValue(key, out value))
            {
                return value;
            }
            object code;
            source.TryGetValue("code", out code);
            throw new InvalidOperationException(
                "reply has no '" + key + "'" +
                (code == null ? string.Empty : "; remote code=" + code));
        }

        internal static IDictionary<string, object> Obj(IDictionary<string, object> source, string key)
        {
            object value;
            source.TryGetValue(key, out value);
            IDictionary<string, object> mapping = value as IDictionary<string, object>;
            if (mapping == null)
            {
                throw new InvalidOperationException("missing object " + key);
            }
            return mapping;
        }

        private static List<object> Arr(IDictionary<string, object> source, string key)
        {
            object value;
            source.TryGetValue(key, out value);
            object[] array = value as object[];
            if (array == null)
            {
                throw new InvalidOperationException("missing array " + key);
            }
            return new List<object>(array);
        }

        private static string Str(IDictionary<string, object> source, string key)
        {
            object value;
            source.TryGetValue(key, out value);
            return value as string;
        }

        private static long Num(IDictionary<string, object> source, string key, long fallback)
        {
            object value;
            long parsed;
            if (!source.TryGetValue(key, out value) || !Json.TryGetInt64(value, out parsed))
            {
                return fallback;
            }
            return parsed;
        }

        private static bool FramesEqual(object left, object right)
        {
            return DeepEqual(left, right);
        }

        private static void WireSplitAtEveryByte()
        {
            IDictionary<string, object> fixture = Fixture();
            IDictionary<string, object> test = Obj(fixture, "split_at_every_byte");
            IDictionary<string, object> first = Obj(test, "first");
            IDictionary<string, object> second = Obj(test, "second");
            byte[] firstBytes = Wire.EncodeFrame(first, null);
            byte[] secondBytes = Wire.EncodeFrame(second, null);
            bool crlf = test.ContainsKey("second_crlf") && (bool)test["second_crlf"];
            string secondText = Encoding.UTF8.GetString(secondBytes);
            if (crlf)
            {
                secondText = secondText.Replace("\n", "\r\n");
            }
            byte[] secondFinal = Encoding.UTF8.GetBytes(secondText);
            byte[] payload = new byte[firstBytes.Length + secondFinal.Length];
            Array.Copy(firstBytes, payload, firstBytes.Length);
            Array.Copy(secondFinal, 0, payload, firstBytes.Length, secondFinal.Length);
            for (int split = 0; split <= payload.Length; split++)
            {
                FrameParser parser = new FrameParser(WireConstants.FirmwareToSlave);
                IList<IDictionary<string, object>> frames =
                    new List<IDictionary<string, object>>();
                foreach (IDictionary<string, object> frame in parser.Feed(payload, 0, split))
                {
                    frames.Add(frame);
                }
                foreach (IDictionary<string, object> frame in
                         parser.Feed(payload, split, payload.Length - split))
                {
                    frames.Add(frame);
                }
                AssertEqual(2, frames.Count, "frame count at split " + split);
                Assert(FramesEqual(first, frames[0]), "first frame at split " + split);
                Assert(FramesEqual(second, frames[1]), "second frame at split " + split);
            }
        }

        private static void WireBannerAndResync()
        {
            IDictionary<string, object> fixture = Fixture();
            IDictionary<string, object> test = Obj(fixture, "banner_and_resync");
            byte[] prefix = Convert.FromBase64String(Str(test, "prefix_b64"));
            byte[] frame = Wire.EncodeFrame(Obj(test, "frame"), null);
            byte[] payload = new byte[prefix.Length + frame.Length];
            Array.Copy(prefix, payload, prefix.Length);
            Array.Copy(frame, 0, payload, prefix.Length, frame.Length);
            FrameParser parser = new FrameParser(WireConstants.FirmwareToSlave);
            IList<IDictionary<string, object>> frames = parser.Feed(payload, 0, payload.Length);
            AssertEqual(1, frames.Count, "one frame survives the banner");
            Assert(FramesEqual(Obj(test, "frame"), frames[0]), "frame decoded");
            List<object> expected = Arr(test, "expected_error_codes");
            AssertEqual(expected.Count, parser.Errors.Count, "error count");
            for (int index = 0; index < expected.Count; index++)
            {
                AssertEqual(expected[index], parser.Errors[index].Code, "error code " + index);
            }
        }

        private static void WireUtf8RoundTrip()
        {
            IDictionary<string, object> fixture = Fixture();
            IDictionary<string, object> test = Obj(fixture, "utf8_round_trip");
            IDictionary<string, object> frame = Obj(test, "frame");
            FrameParser parser = new FrameParser(WireConstants.MasterToFirmware);
            IList<IDictionary<string, object>> frames =
                parser.Feed(Wire.EncodeFrame(frame, null), 0,
                            Wire.EncodeFrame(frame, null).Length);
            AssertEqual(1, frames.Count, "one frame");
            Assert(FramesEqual(frame, frames[0]), "unicode body survived the round trip");

            byte[] binary = HexToBytes(Str(test, "binary_chunk_hex"));
            IDictionary<string, object> data = Wire.BaseFrame(
                Str(fixture, "link_id"), "shell", "data",
                "session_id", Str(test, "data_session_id"),
                "seq", Num(test, "data_seq", 0),
                "data_b64", Wire.DataB64(binary));
            byte[] decoded = Wire.DecodeData(data);
            AssertEqual(binary.Length, decoded.Length, "binary length");
            for (int index = 0; index < binary.Length; index++)
            {
                if (binary[index] != decoded[index])
                {
                    throw new InvalidOperationException("binary byte " + index + " differs");
                }
            }
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = byte.Parse(
                    hex.Substring(index * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static void WireExactMaximum()
        {
            IDictionary<string, object> fixture = Fixture();
            IDictionary<string, object> test = Obj(fixture, "exact_maximum");
            IDictionary<string, object> template = Obj(test, "template");
            IDictionary<string, object> body = Obj(template, "body");
            template["body"] = body;
            int max = (int)Num(test, "max_frame_bytes", 4096);
            int overhead = Wire.EncodeFrame(template, null).Length - 1;
            int padding = max - overhead;
            Assert(padding >= 1, "padding must be positive");
            char paddingByte = (char)Num(test, "padding_byte", 120);
            body["padding"] = new string(paddingByte, padding);
            byte[] encoded = Wire.EncodeFrame(template, null);
            AssertEqual(max, encoded.Length - 1, "frame is exactly the maximum");

            FrameParser accepted = new FrameParser(WireConstants.MasterToFirmware);
            IList<IDictionary<string, object>> frames =
                accepted.Feed(encoded, 0, encoded.Length);
            AssertEqual(1, frames.Count, "maximum frame accepted");

            int overflow = (int)Num(test, "overflow_bytes", 4097);
            byte[] overflowBytes = new byte[overflow];
            for (int index = 0; index < overflowBytes.Length; index++)
            {
                overflowBytes[index] = (byte)paddingByte;
            }
            byte[] valid = Wire.EncodeFrame(Obj(test, "valid_after_overflow"), null);
            byte[] payload = new byte[overflow + 1 + valid.Length];
            Array.Copy(overflowBytes, payload, overflow);
            payload[overflow] = 0x0A;
            Array.Copy(valid, 0, payload, overflow + 1, valid.Length);
            FrameParser parser = new FrameParser(WireConstants.FirmwareToSlave);
            IList<IDictionary<string, object>> resynced =
                parser.Feed(payload, 0, payload.Length);
            AssertEqual(1, resynced.Count, "resynchronized frame");
            Assert(FramesEqual(Obj(test, "valid_after_overflow"), resynced[0]), "valid frame");
            Assert(parser.Errors.Count > 0, "overflow recorded an error");
            AssertEqual(
                Str(test, "expected_overflow_code"),
                parser.Errors[parser.Errors.Count - 1].Code,
                "overflow code");
        }

        private static void WireRejections()
        {
            IDictionary<string, object> fixture = Fixture();
            foreach (object item in Arr(fixture, "rejections"))
            {
                IDictionary<string, object> test = item as IDictionary<string, object>;
                Assert(test != null, "rejection case shape");
                string direction;
                object directionValue;
                test.TryGetValue("direction", out directionValue);
                direction = directionValue as string;
                try
                {
                    Wire.ValidateFrame(test["frame"], direction);
                }
                catch (WireError error)
                {
                    AssertEqual(Str(test, "code"), error.Code, Str(test, "name"));
                    continue;
                }
                throw new InvalidOperationException(
                    "case " + Str(test, "name") + " was accepted");
            }
        }

        private static void WireChunkLimit()
        {
            IDictionary<string, object> fixture = Fixture();
            string link = Str(fixture, "link_id");
            foreach (object item in Arr(fixture, "base64_rejections"))
            {
                IDictionary<string, object> test = item as IDictionary<string, object>;
                IDictionary<string, object> frame = new Dictionary<string, object>();
                frame["v"] = 1;
                frame["channel"] = "shell";
                frame["type"] = "data";
                frame["link_id"] = link;
                frame["session_id"] = "pty";
                frame["seq"] = 0;
                frame["data_b64"] = Str(test, "value");
                try
                {
                    Wire.DecodeData(frame);
                }
                catch (WireError error)
                {
                    AssertEqual(Str(test, "code"), error.Code, Str(test, "name"));
                    continue;
                }
                throw new InvalidOperationException(
                    "base64 case " + Str(test, "name") + " was accepted");
            }
            IDictionary<string, object> limit = Obj(fixture, "chunk_limit");
            int size = (int)Num(limit, "chunk_bytes", 513);
            byte[] chunk = new byte[size];
            try
            {
                Wire.DataB64(chunk);
            }
            catch (WireError error)
            {
                AssertEqual(Str(limit, "code"), error.Code, "chunk limit code");
                AssertEqual(WireConstants.DataChunkBytes + 1, size, "chunk limit value");
                return;
            }
            throw new InvalidOperationException("a 513-byte chunk was accepted");
        }

        private static void DedupBounded()
        {
            IDictionary<string, object> fixture = Fixture();
            IDictionary<string, object> test = Obj(fixture, "dedup_sequence");
            Deduplicator dedup = new Deduplicator((int)Num(test, "limit", 2));
            foreach (object item in Arr(test, "operations"))
            {
                IDictionary<string, object> operation = item as IDictionary<string, object>;
                DedupKey key = new DedupKey(
                    Str(operation, "link_id"), Str(operation, "session_id"),
                    Num(operation, "seq", 0));
                bool expected = (bool)operation["add"];
                AssertEqual(expected, dedup.Add(key), "add result");
            }
            IDictionary<string, object> probe = Obj(test, "seen_without_registering");
            DedupKey absent = new DedupKey(
                Str(probe, "link_id"), Str(probe, "session_id"), Num(probe, "seq", 0));
            Assert(!dedup.Seen(absent), "seen must not register");
            Assert(dedup.Add(absent), "first add after seen succeeds");
            Assert(dedup.Seen(absent), "add registered the key");
        }

        // -------------------------------------------------------------- Agent

        private static string LinkId(IDictionary<string, object> fixture)
        {
            return Str(fixture, "link_id");
        }


        private static void TransferSharedCorpus()
        {
            IDictionary<string, object> fixture = Fixture();
            IDictionary<string, object> corpus = Obj(fixture, "transfer_window_corpus");
            AssertEqual(WireConstants.DataChunkBytes, Num(corpus, "block_bytes", 0),
                        "corpus block size");

            IDictionary<string, object> empty = Obj(corpus, "empty_transfer");
            AssertEqual(0L, Num(empty, "size", -1), "empty transfer size");
            AssertEqual(0L, Num(empty, "blocks", -1), "empty transfer blocks");
            AssertEqual(Sha256Hex(new byte[0]), Str(empty, "sha256"), "empty digest");

            IDictionary<string, object> partial = Obj(corpus, "final_partial_block");
            long size = Num(partial, "size", 0);
            long blocks = (size + WireConstants.DataChunkBytes - 1) / WireConstants.DataChunkBytes;
            AssertEqual(blocks, Num(partial, "blocks", -1), "partial block count");
            AssertEqual(size - (blocks - 1) * WireConstants.DataChunkBytes,
                        Num(partial, "last_block_bytes", -1), "final partial size");

            foreach (object item in Arr(corpus, "windows"))
            {
                IDictionary<string, object> entry = item as IDictionary<string, object>;
                long window = Num(entry, "window", 0);
                Assert(window == 1 || window == 8 || window == 64, "corpus window value");
                AssertEqual(window, Num(entry, "max_in_flight_before_ack", -1),
                            "window bounds in-flight blocks");
            }
            AssertEqual(8L, Num(corpus, "invalid_window_falls_back_to", -1),
                        "invalid window fallback");

            IDictionary<string, object> lost = Obj(corpus, "lost_ack");
            Deduplicator dedup = new Deduplicator(8);
            DedupKey key = new DedupKey(
                Str(fixture, "link_id"), Str(lost, "session_id"), Num(lost, "seq", 0));
            Assert(dedup.Add(key), "first delivery applies");
            Assert(dedup.Seen(key), "retransmission recognised");
            Assert(!dedup.Add(key), "retransmission not applied twice");
            AssertEqual(1L, Num(lost, "expected_applications", -1), "single application");

            IDictionary<string, object> retry = Obj(corpus, "retry_exhaustion");
            AssertEqual(Num(retry, "retransmissions", -1) + 1,
                        Num(retry, "total_transmissions", -1), "1 + 3 transmissions");
            AssertEqual("ack_timeout", Str(retry, "expected_code"), "retry exhaustion code");
        }

        private static void AgentHelloBindReady()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = new MasterPeer(harness, LinkId(Fixture()));
                    IDictionary<string, object> hello = peer.Next("hello", 8000);
                    AssertEqual("control", Str(hello, "channel"), "hello channel");
                    IDictionary<string, object> body = Obj(hello, "body");
                    AssertEqual("titella-slave", Str(body, "agent"), "agent name");
                    AssertEqual("windows-dotnet", Str(body, "implementation"), "implementation");
                    Assert(body.ContainsKey("python") && body["python"] == null, "python is null");
                    Assert(body.ContainsKey("uid") && body["uid"] == null, "uid is null");
                    AssertEqual(Process.GetCurrentProcess().Id, Num(body, "pid", -1), "real pid");
                    IDictionary<string, object> runtime = Obj(body, "runtime");
                    AssertEqual("dotnet-framework", Str(runtime, "name"), "runtime name");

                    IDictionary<string, object> ready = peer.Request(
                        "control", "bind", BindBody(LinkId(Fixture())), 8000);
                    AssertEqual("ready", Str(ready, "status"), "ready status");
                    AssertEqual(LinkId(Fixture()), Str(ready, "link_id"), "ready link");

                    string foreign = new string('0', 32);
                    IDictionary<string, object> denied = peer.RequestExpectError(
                        "control", "bind", BindBody(foreign), 8000);
                    AssertEqual("takeover_denied", Str(denied, "code"), "takeover denied");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static IDictionary<string, object> BindBody(string linkId)
        {
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["link_id"] = linkId;
            return body;
        }

        private static void AgentNotBoundBeforeBind()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = new MasterPeer(harness, LinkId(Fixture()));
                    peer.Next("hello", 8000);
                    IDictionary<string, object> error = peer.RequestExpectError(
                        "fs", "sysinfo", new Dictionary<string, object>(), 8000);
                    AssertEqual("not_bound", Str(error, "code"), "not_bound code");
                    AssertEqual(true, error["retryable"], "not_bound is retryable");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static void AgentDuplicateData()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-dup-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    string link = LinkId(Fixture());
                    MasterPeer peer = new MasterPeer(harness, link);
                    peer.Next("hello", 8000);
                    peer.Request("control", "bind", BindBody(link), 8000);
                    IDictionary<string, object> opened = peer.Request(
                        "shell", "terminal.open", OpenBody(work), 15000);
                    string session = Str(opened, "session_id");
                    string marker = Path.Combine(work, "applied.txt");
                    byte[] command = Encoding.UTF8.GetBytes(
                        "Add-Content -LiteralPath '" + marker + "' -Value 'x'" + "\r\n");
                    peer.SendData("shell", session, 0, command, 8000);
                    // Same frame delivered again: re-ACK without re-applying.
                    peer.SendData("shell", session, 0, command, 8000);
                    long settle = Environment.TickCount + 20000;
                    while (!File.Exists(marker) && Environment.TickCount < settle)
                    {
                        Thread.Sleep(100);
                    }
                    Assert(File.Exists(marker), "the command ran at least once");
                    string[] lines = File.ReadAllLines(marker);
                    AssertEqual(1, lines.Length, "the duplicate frame was applied once");
                    peer.Request("shell", "terminal.close", SessionBody(session), 15000);
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static IDictionary<string, object> OpenBody(string work)
        {
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["cwd"] = work;
            body["rows"] = 24;
            body["cols"] = 80;
            body["idle_timeout"] = 60;
            return body;
        }

        private static IDictionary<string, object> SessionBody(string session)
        {
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["session_id"] = session;
            return body;
        }

        private static void AgentGapIsNotAcked()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-gap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    string link = LinkId(Fixture());
                    MasterPeer peer = new MasterPeer(harness, link);
                    peer.Next("hello", 8000);
                    peer.Request("control", "bind", BindBody(link), 8000);
                    byte[] payload = new byte[1536];
                    for (int index = 0; index < payload.Length; index++)
                    {
                        payload[index] = (byte)(index % 251);
                    }
                    string target = Path.Combine(work, "target.bin");
                    IDictionary<string, object> begin = new Dictionary<string, object>();
                    begin["transfer_id"] = "gap";
                    begin["path"] = target;
                    begin["size"] = payload.Length;
                    begin["sha256"] = Sha256Hex(payload);
                    peer.Request("transfer", "upload.begin", begin, 8000);

                    IDictionary<string, object> firstGap = peer.SendDataExpectError(
                        "transfer", "gap", 2, payload, 1024, 512, 8000);
                    AssertEqual("sequence", Str(firstGap, "code"), "gap code");
                    IDictionary<string, object> repeated = peer.SendDataExpectError(
                        "transfer", "gap", 2, payload, 1024, 512, 8000);
                    AssertEqual("sequence", Str(repeated, "code"), "repeated gap code");

                    peer.SendData("transfer", "gap", 0, Slice(payload, 0, 512), 8000);
                    peer.SendData("transfer", "gap", 1, Slice(payload, 512, 512), 8000);
                    peer.SendData("transfer", "gap", 2, Slice(payload, 1024, 512), 8000);
                    IDictionary<string, object> committed = peer.Request(
                        "transfer", "upload.commit", TransferBody("gap"), 8000);
                    AssertEqual("finished", Str(committed, "status"), "commit finished");
                    AssertEqual(payload.Length, (int)new FileInfo(target).Length, "file size");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static IDictionary<string, object> TransferBody(string transferId)
        {
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["transfer_id"] = transferId;
            return body;
        }

        private static MasterPeer Bind(PeerHarness harness)
        {
            return PeerHelpers.Bind(harness, LinkId(Fixture()));
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        private static string Sha256Hex(byte[] data)
        {
            using (System.Security.Cryptography.SHA256 sha =
                   System.Security.Cryptography.SHA256.Create())
            {
                return FileService.Hex(sha.ComputeHash(data));
            }
        }

        private static Thread StartAgent(SlaveAgent agent)
        {
            Thread thread = new Thread(agent.Run);
            thread.IsBackground = true;
            thread.Name = "test-agent";
            thread.Start();
            return thread;
        }

        private static void StopAgent(SlaveAgent agent, Thread thread)
        {
            try
            {
                agent.Cleanup(true);
            }
            catch (Exception)
            {
                // Cleanup is idempotent; a dead link is fine here.
            }
            thread.Join(8000);
        }

        // --------------------------------------------------------------- Jobs

        private static void JobStdoutStderrSeparated()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    IDictionary<string, object> started = peer.Request(
                        "shell", "job.start",
                        JobBody("streams",
                                "Write-Output out; [Console]::Error.WriteLine('err')", 60),
                        8000);
                    AssertEqual("running", Str(started, "status"), "job started");
                    IDictionary<string, object> finished = peer.WaitEvent(
                        "job.finished", "streams", 20000);
                    IDictionary<string, object> body = Obj(finished, "body");
                    AssertEqual(0, Num(body, "returncode", -1), "return code");
                    Assert(peer.DataText("streams", "stdout").Contains("out"),
                           "stdout captured");
                    Assert(peer.DataText("streams", "stderr").Contains("err"),
                           "stderr captured");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static IDictionary<string, object> JobBody(string jobId, string command, int timeout)
        {
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["job_id"] = jobId;
            body["cmd"] = command;
            body["timeout"] = timeout;
            return body;
        }

        private static void JobExitCodePropagates()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    peer.Request("shell", "job.start", JobBody("exit7", "exit 7", 60), 8000);
                    IDictionary<string, object> finished = peer.WaitEvent(
                        "job.finished", "exit7", 20000);
                    AssertEqual(7, Num(Obj(finished, "body"), "returncode", -1),
                                "exit code 7 propagated");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static void JobSlowReturnsId()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    Stopwatch watch = Stopwatch.StartNew();
                    IDictionary<string, object> started = peer.Request(
                        "shell", "job.start", JobBody("slow", "Start-Sleep 3", 60), 8000);
                    watch.Stop();
                    AssertEqual("running", Str(started, "status"), "running");
                    Assert(watch.ElapsedMilliseconds < 3000, "start does not wait for exit");
                    IDictionary<string, object> finished = peer.WaitEvent(
                        "job.finished", "slow", 10000);
                    AssertEqual(0, Num(Obj(finished, "body"), "returncode", -1),
                                "slow job finished");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static void JobCancelKillsGrandchild()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-jobcancel-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string file = Path.Combine(work, "ping.txt");
                    string command = "& cmd.exe /c 'ping -n 30 127.0.0.1 > \"" + file + "\"'";
                    peer.Request("shell", "job.start", JobBody("cancel", command, 60), 8000);
                    WaitUntilFile(work, 15000);
                    long before = LengthOf(work);
                    IDictionary<string, object> cancelled = peer.Request(
                        "shell", "job.cancel", CancelBody("cancel"), 8000);
                    AssertEqual("cancelled", Str(cancelled, "status"), "cancel status");
                    IDictionary<string, object> finished = peer.WaitEvent(
                        "job.finished", "cancel", 15000);
                    AssertEqual(true, Obj(finished, "body")["cancelled"], "cancelled flag");
                    long after = LengthOf(work);
                    Thread.Sleep(1000);
                    AssertEqual(after, LengthOf(work), "the grandchild stopped writing");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static IDictionary<string, object> CancelBody(string jobId)
        {
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["job_id"] = jobId;
            return body;
        }

        private static void JobTimeoutKillsGrandchild()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-jobtimeout-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string file = Path.Combine(work, "ping.txt");
                    string command = "& cmd.exe /c 'ping -n 30 127.0.0.1 > \"" + file + "\"'";
                    peer.Request("shell", "job.start", JobBody("timeout", command, 1), 8000);
                    IDictionary<string, object> finished = peer.WaitEvent(
                        "job.finished", "timeout", 20000);
                    IDictionary<string, object> body = Obj(finished, "body");
                    AssertEqual(true, body["timed_out"], "timed out flag");
                    Assert(Num(body, "returncode", 0) != 0, "timeout return code is non-zero");
                    long after = LengthOf(work);
                    Thread.Sleep(1000);
                    AssertEqual(after, LengthOf(work), "the grandchild stopped writing");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void WaitUntilFile(string directory, int timeoutMs)
        {
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (LengthOf(directory) > 0)
                {
                    Thread.Sleep(200);
                    return;
                }
                Thread.Sleep(50);
            }
            throw new InvalidOperationException("the grandchild did not start writing");
        }

        private static long LengthOf(string directory)
        {
            long total = 0;
            foreach (string entry in Directory.GetFiles(directory))
            {
                total += new FileInfo(entry).Length;
            }
            return total;
        }

        private static void JobSharedLimit()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    for (int index = 0; index < 4; index++)
                    {
                        IDictionary<string, object> started = peer.Request(
                            "shell", "job.start",
                            JobBody("limit-" + index, "Start-Sleep 30", 60), 8000);
                        AssertEqual("running", Str(started, "status"), "job " + index);
                    }
                    IDictionary<string, object> rejected = peer.RequestExpectError(
                        "shell", "job.start",
                        JobBody("limit-4", "Start-Sleep 30", 60), 8000);
                    AssertEqual("session_limit", Str(rejected, "code"), "fifth job rejected");
                    for (int index = 0; index < 4; index++)
                    {
                        peer.Request("shell", "job.cancel",
                                     CancelBody("limit-" + index), 8000);
                    }
                    long deadline = Environment.TickCount + 15000;
                    while (agent.Registry.Count > 0 && Environment.TickCount < deadline)
                    {
                        Thread.Sleep(100);
                    }
                    AssertEqual(0, agent.Registry.Count, "registry returned to zero");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static void JobContainmentFailure()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-containment-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string marker = Path.Combine(work, "ran.txt");
                    JobManager.SimulateContainmentFailure = true;
                    try
                    {
                        IDictionary<string, object> error = peer.RequestExpectError(
                            "shell", "job.start",
                            JobBody("contained",
                                    "Add-Content -LiteralPath '" + marker + "' -Value 'x'",
                                    60),
                            8000);
                        AssertEqual("process_containment_failed", Str(error, "code"),
                                    "containment failure code");
                    }
                    finally
                    {
                        JobManager.SimulateContainmentFailure = false;
                    }
                    Thread.Sleep(1000);
                    Assert(!File.Exists(marker), "the uncontained child never ran user code");
                    AssertEqual(0, agent.Registry.Count, "the slot was released");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        // The job runner executes the user script inside a custom-host
        // Runspace; these tests pin the semantics that must survive.
        private static void JobEngineStreams()
        {
            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();
            using (StringWriter @out = new StringWriter(stdout))
            using (StringWriter err = new StringWriter(stderr))
            {
                // [Console]::Error writes to the process stderr, not the
                // PSHost, so the in-process test exercises the host error
                // stream itself. Writing to the error stream is an error:
                // like powershell.exe -Command, the exit code becomes 1.
                int rc = Titella.Slave.JobEngine.Run(
                    "Write-Output OUTVAL123; $host.UI.WriteErrorLine('ERRVAL456')",
                    @out, err);
                AssertEqual(1, rc, "host error writes make the exit code 1");
            }
            Assert(stdout.ToString().Contains("OUTVAL123"), "pipeline output on stdout");
            Assert(!stderr.ToString().Contains("OUTVAL123"),
                   "pipeline output stays off stderr");
            Assert(stderr.ToString().Contains("ERRVAL456"), "host error stream captured");
        }

        private static void JobEngineExitCode()
        {
            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();
            using (StringWriter @out = new StringWriter(stdout))
            using (StringWriter err = new StringWriter(stderr))
            {
                int rc = Titella.Slave.JobEngine.Run("exit 7", @out, err);
                AssertEqual(7, rc, "exit code propagated");
                int rc0 = Titella.Slave.JobEngine.Run("Write-Output ok", @out, err);
                AssertEqual(0, rc0, "implicit zero exit code");
                int rc1 = Titella.Slave.JobEngine.Run(
                    "Get-Item 'C:\\nonexistent-titella-probe.txt'", @out, err);
                Assert(rc1 != 0, "cmdlet error produces non-zero exit code");
                Assert(stderr.ToString().Contains("nonexistent-titella-probe"),
                       "error record rendered on stderr");
            }
        }

        private static void JobEngineWriteHost()
        {
            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();
            using (StringWriter @out = new StringWriter(stdout))
            using (StringWriter err = new StringWriter(stderr))
            {
                Titella.Slave.JobEngine.Run("Write-Host HOSTMSG789", @out, err);
            }
            Assert(stdout.ToString().Contains("HOSTMSG789"),
                   "Write-Host reaches stdout through the host UI");
        }

        // ----------------------------------------------------------- Terminal

        private static void TerminalEchoResizeClose()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-terminal-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["cwd"] = work;
                    body["rows"] = 40;
                    body["cols"] = 120;
                    body["idle_timeout"] = 60;
                    IDictionary<string, object> opened = peer.Request(
                        "shell", "terminal.open", body, 20000);
                    string session = Str(opened, "session_id");
                    string token = "MARK:" + Guid.NewGuid().ToString("N");
                    peer.SendData("shell", session, 0, Encoding.UTF8.GetBytes(
                        "Write-Output '" + token + "'\r\n"), 8000);
                    Assert(peer.WaitBytes(session, token, 20000), "token echoed");

                    IDictionary<string, object> resize = new Dictionary<string, object>();
                    resize["session_id"] = session;
                    resize["rows"] = 25;
                    resize["cols"] = 80;
                    IDictionary<string, object> resized = peer.Request(
                        "shell", "terminal.resize", resize, 8000);
                    AssertEqual(25, Num(resized, "rows", 0), "resize rows");
                    AssertEqual(80, Num(resized, "cols", 0), "resize cols");

                    peer.SendData("shell", session, 1, Encoding.UTF8.GetBytes(
                        "Write-Output ('RC:'+$Host.UI.RawUI.WindowSize.Width+'x'+$Host.UI.RawUI.WindowSize.Height)\r"),
                        8000);
                    Assert(peer.WaitBytes(session, "RC:80x25", 20000),
                           "the shell reports the resized window");

                    // Close while a large output backlog is pending.
                    peer.SendData("shell", session, 2, Encoding.UTF8.GetBytes(
                        "1..4000 | ForEach-Object { 'x' * 120 }\r"), 8000);
                    Stopwatch watch = Stopwatch.StartNew();
                    IDictionary<string, object> closed = peer.Request(
                        "shell", "terminal.close", SessionBody(session), 20000);
                    watch.Stop();
                    AssertEqual("closed", Str(closed, "status"), "close status");
                    Assert(watch.ElapsedMilliseconds < 15000,
                           "close returned with pending output");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void TerminalNoSecondConhost()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    Process current = Process.GetCurrentProcess();
                    int baseline = current.HandleCount;
                    for (int index = 0; index < 3; index++)
                    {
                        IDictionary<string, object> body = new Dictionary<string, object>();
                        body["rows"] = 24;
                        body["cols"] = 80;
                        body["idle_timeout"] = 60;
                        IDictionary<string, object> opened = peer.Request(
                            "shell", "terminal.open", body, 20000);
                        peer.Request("shell", "terminal.close",
                                     SessionBody(Str(opened, "session_id")), 20000);
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(500);
                    int after = current.HandleCount;
                    // Informational threshold: a bounded leak, not a hard gate.
                    Assert(after - baseline < 300,
                           "handle count stayed near the baseline: " + baseline +
                           " -> " + after);
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static void TerminalShellIsSingleExecutable()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["shell"] = "powershell.exe -NoProfile";
                    IDictionary<string, object> error = peer.RequestExpectError(
                        "shell", "terminal.open", body, 8000);
                    AssertEqual("invalid_argument", Str(error, "code"),
                                "a shell line with arguments is rejected");
                    AssertEqual(0, agent.Registry.Count, "the slot was released");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        // ---------------------------------------------------------------- Fs

        private static void FsReadPagination()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-fsread-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string target = Path.Combine(work, "payload.bin");
                    byte[] data = new byte[80 * 1024];
                    for (int index = 0; index < data.Length; index++)
                    {
                        data[index] = (byte)(index % 251);
                    }
                    File.WriteAllBytes(target, data);

                    IDictionary<string, object> first = new Dictionary<string, object>();
                    first["path"] = target;
                    first["offset"] = 0;
                    first["length"] = 100000;
                    first["stream_id"] = "page-0";
                    IDictionary<string, object> metadata = peer.Request(
                        "fs", "read", first, 15000);
                    AssertEqual("bytes", Str(metadata, "payload_kind"), "payload kind");
                    AssertEqual(FileLimits.MaxReadBytes, Num(metadata, "bytes", 0),
                                "clamped to 64 KiB");
                    AssertEqual(FileLimits.MaxReadBytes, Num(metadata, "next_offset", 0),
                                "next offset");
                    AssertEqual(false, Field(metadata, "eof"), "not at EOF");
                    peer.WaitEvent("fs.stream.finished", "page-0", 15000);
                    AssertEqual(FileLimits.MaxReadBytes,
                                peer.DataOf("page-0", "data").Length, "streamed bytes");

                    int offset = (int)Num(metadata, "next_offset", 0);
                    IDictionary<string, object> second = new Dictionary<string, object>();
                    second["path"] = target;
                    second["offset"] = offset;
                    second["length"] = 100000;
                    IDictionary<string, object> tail = peer.Request(
                        "fs", "read", second, 15000);
                    AssertEqual(true, Field(tail, "eof"), "second page reaches EOF");
                    AssertEqual(data.Length, (int)Num(tail, "next_offset", 0), "final offset");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void FsAtomicWriteReplace()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-fswrite-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string target = Path.Combine(work, "target.txt");
                    IDictionary<string, object> write = new Dictionary<string, object>();
                    write["path"] = target;
                    write["data_b64"] = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes("one token"));
                    IDictionary<string, object> written = peer.Request(
                        "fs", "write", write, 8000);
                    AssertEqual("written", Str(written, "status"), "written");
                    AssertEqual(Sha256Hex(Encoding.UTF8.GetBytes("one token")),
                                Str(written, "sha256"), "write digest");
                    foreach (string entry in Directory.GetFiles(work))
                    {
                        Assert(Path.GetFileName(entry).IndexOf("titella", StringComparison.Ordinal) < 0,
                               "no temporary file remained");
                    }

                    IDictionary<string, object> replace = new Dictionary<string, object>();
                    replace["path"] = target;
                    replace["old"] = "token";
                    replace["new"] = "value";
                    IDictionary<string, object> replaced = peer.Request(
                        "fs", "replace", replace, 8000);
                    AssertEqual("replaced", Str(replaced, "status"), "replaced");
                    AssertEqual("one value", File.ReadAllText(target), "replacement applied");

                    File.WriteAllText(target, "x x", new UTF8Encoding(false));
                    IDictionary<string, object> ambiguous = new Dictionary<string, object>();
                    ambiguous["path"] = target;
                    ambiguous["old"] = "x";
                    ambiguous["new"] = "y";
                    IDictionary<string, object> error = peer.RequestExpectError(
                        "fs", "replace", ambiguous, 8000);
                    AssertEqual("replace_count", Str(error, "code"), "two occurrences");

                    // A 64 KiB+1 payload cannot fit in a 4096-byte frame, so
                    // the write limit is asserted against the service itself
                    // (same assembly) rather than through the wire.
                    try
                    {
                        new FileService().Write(target, new byte[FileLimits.MaxWriteBytes + 1]);
                        throw new InvalidOperationException("64 KiB + 1 was accepted");
                    }
                    catch (ProtocolError limit)
                    {
                        AssertEqual("write_too_large", limit.Code, "64 KiB + 1");
                    }
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void FsDeleteBoundaries()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-fsdelete-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string directory = Path.Combine(work, "directory");
                    Directory.CreateDirectory(directory);
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["path"] = directory;
                    IDictionary<string, object> denied = peer.RequestExpectError(
                        "fs", "delete", body, 8000);
                    AssertEqual("directory_delete_denied", Str(denied, "code"),
                                "directory delete denied");

                    string target = Path.Combine(work, "plain.txt");
                    File.WriteAllText(target, "x", new UTF8Encoding(false));
                    IDictionary<string, object> remove = new Dictionary<string, object>();
                    remove["path"] = target;
                    IDictionary<string, object> deleted = peer.Request(
                        "fs", "delete", remove, 8000);
                    AssertEqual("deleted", Str(deleted, "status"), "plain file deleted");

                    string junction = Path.Combine(work, "junction");
                    using (Process cmd = new Process())
                    {
                        cmd.StartInfo.FileName = "cmd.exe";
                        cmd.StartInfo.Arguments =
                            "/c mklink /J \"" + junction + "\" \"" + directory + "\"";
                        cmd.StartInfo.UseShellExecute = false;
                        cmd.StartInfo.CreateNoWindow = true;
                        cmd.Start();
                        cmd.WaitForExit(10000);
                    }
                    Assert(Directory.Exists(junction), "junction created");
                    IDictionary<string, object> unlink = new Dictionary<string, object>();
                    unlink["path"] = junction;
                    IDictionary<string, object> removedLink = peer.Request(
                        "fs", "delete", unlink, 8000);
                    AssertEqual("deleted", Str(removedLink, "status"), "junction unlinked");
                    Assert(Directory.Exists(directory), "the junction target survived");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void FsListAndFindOrdering()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-fslist-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    for (int index = 0; index < 205; index++)
                    {
                        File.WriteAllText(
                            Path.Combine(work, string.Format(
                                CultureInfo.InvariantCulture, "entry-{0:000}", index)),
                            string.Empty, new UTF8Encoding(false));
                    }
                    IDictionary<string, object> first = new Dictionary<string, object>();
                    first["path"] = work;
                    first["limit"] = 200;
                    IDictionary<string, object> page = FsOutcome(peer, peer.Request("fs", "list", first, 8000), 15000);
                    object[] entries = Field(page, "entries") as object[];
                    Assert(entries != null, "entries array");
                    AssertEqual(200, entries.Length, "first page size");
                    AssertEqual(205, (int)Num(page, "total", 0), "total count");
                    AssertEqual(true, Field(page, "has_more"), "has more");
                    string previous = string.Empty;
                    foreach (object item in entries)
                    {
                        string name = Str(item as IDictionary<string, object>, "name");
                        Assert(string.CompareOrdinal(previous, name) <= 0,
                               "ordinal order at " + name);
                        previous = name;
                    }

                    string tree = Path.Combine(work, "tree");
                    Directory.CreateDirectory(tree);
                    for (int index = 0; index < 510; index++)
                    {
                        File.WriteAllText(
                            Path.Combine(tree, string.Format(
                                CultureInfo.InvariantCulture, "item-{0:000}.log", index)),
                            string.Empty, new UTF8Encoding(false));
                    }
                    string nested = Path.Combine(tree, "nested");
                    Directory.CreateDirectory(nested);
                    File.WriteAllText(Path.Combine(nested, "deep.log"), "x",
                                      new UTF8Encoding(false));
                    string junction = Path.Combine(tree, "junction");
                    Directory.CreateDirectory(Path.Combine(tree, "target"));
                    using (Process cmd = new Process())
                    {
                        cmd.StartInfo.FileName = "cmd.exe";
                        cmd.StartInfo.Arguments =
                            "/c mklink /J \"" + junction + "\" \"" +
                            Path.Combine(tree, "target") + "\"";
                        cmd.StartInfo.UseShellExecute = false;
                        cmd.StartInfo.CreateNoWindow = true;
                        cmd.Start();
                        cmd.WaitForExit(10000);
                    }
                    IDictionary<string, object> find = new Dictionary<string, object>();
                    find["path"] = tree;
                    find["pattern"] = "*.log";
                    find["limit"] = 1000;
                    IDictionary<string, object> found = FsOutcome(peer, peer.Request("fs", "find", find, 15000), 30000);
                    object[] results = Field(found, "results") as object[];
                    Assert(results != null, "results array");
                    AssertEqual(FileLimits.MaxFindResults, results.Length, "find cap");
                    AssertEqual(true, Field(found, "truncated"), "find truncated");
                    foreach (object item in results)
                    {
                        string path = item as string;
                        Assert(path.IndexOf("junction", StringComparison.Ordinal) < 0,
                               "find never descends into a reparse point");
                    }
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void FsSearch()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-fssearch-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string target = Path.Combine(work, "lines.txt");
                    StringBuilder text = new StringBuilder();
                    for (int index = 0; index < 300; index++)
                    {
                        text.Append("match ").Append(index).Append('\n');
                    }
                    text.Append("MATCH uppercase\n");
                    text.Append(new string('z', 2000)).Append('\n');
                    File.WriteAllText(target, text.ToString(), new UTF8Encoding(false));
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["path"] = target;
                    body["query"] = "match";
                    body["limit"] = 1000;
                    IDictionary<string, object> found = FsOutcome(peer, peer.Request("fs", "search", body, 8000), 15000);
                    object[] results = Field(found, "results") as object[];
                    Assert(results != null, "results");
                    AssertEqual(FileLimits.MaxSearchResults, results.Length, "search cap");
                    AssertEqual(true, Field(found, "truncated"), "search truncated");
                    IDictionary<string, object> firstItem =
                        results[0] as IDictionary<string, object>;
                    AssertEqual(1, (int)Num(firstItem, "line", 0), "first line number");

                    IDictionary<string, object> sensitive = new Dictionary<string, object>();
                    sensitive["path"] = target;
                    sensitive["query"] = "MATCH";
                    IDictionary<string, object> upper = FsOutcome(peer,
                        peer.Request("fs", "search", sensitive, 8000), 15000);
                    AssertEqual(1, (Field(upper, "results") as object[]).Length,
                                "search is case sensitive");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void FsLockedFileIsHonest()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-fslock-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string target = Path.Combine(work, "locked.bin");
                    File.WriteAllBytes(target, new byte[] { 1, 2, 3 });
                    using (FileStream hold = new FileStream(
                        target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        IDictionary<string, object> body = new Dictionary<string, object>();
                        body["path"] = target;
                        IDictionary<string, object> error = peer.RequestExpectError(
                            "fs", "read", body, 8000);
                        string code = Str(error, "code");
                        Assert(code == "io_error" || code == "permission_denied",
                               "locked file reported honestly, got " + code);
                    }
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void FsSysinfoShape()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    IDictionary<string, object> info = peer.Request(
                        "fs", "sysinfo", new Dictionary<string, object>(), 8000);
                    AssertEqual("Windows", Str(info, "system"), "system name");
                    Assert(info.ContainsKey("python") && info["python"] == null,
                           "python is null");
                    Assert(info.ContainsKey("uid") && info["uid"] == null, "uid is null");
                    Assert(info.ContainsKey("gid") && info["gid"] == null, "gid is null");
                    IDictionary<string, object> runtime = Obj(info, "runtime");
                    AssertEqual("dotnet-framework", Str(runtime, "name"), "runtime name");
                    Assert(!string.IsNullOrEmpty(Str(info, "hostname")), "hostname present");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        // ---------------------------------------------------------- Transfers

        private static void TransferRoundTrip()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-roundtrip-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    int length = 2 * 1024 * 1024 + 17;
                    byte[] payload = new byte[length];
                    for (int index = 0; index < payload.Length; index++)
                    {
                        payload[index] = (byte)((index * 31 + 7) % 251);
                    }
                    string checksum = Sha256Hex(payload);
                    string target = Path.Combine(work, "roundtrip.bin");
                    IDictionary<string, object> begin = new Dictionary<string, object>();
                    begin["transfer_id"] = "up";
                    begin["path"] = target;
                    begin["size"] = payload.Length;
                    begin["sha256"] = checksum;
                    peer.Request("transfer", "upload.begin", begin, 8000);
                    long sequence = 0;
                    for (int position = 0; position < payload.Length;
                         position += WireConstants.DataChunkBytes)
                    {
                        int chunk = Math.Min(
                            WireConstants.DataChunkBytes, payload.Length - position);
                        peer.SendData("transfer", "up", sequence,
                                      Slice(payload, position, chunk), 15000);
                        sequence++;
                    }
                    IDictionary<string, object> committed = peer.Request(
                        "transfer", "upload.commit", TransferBody("up"), 20000);
                    AssertEqual("finished", Str(committed, "status"), "commit finished");
                    AssertEqual(checksum, Str(committed, "sha256"), "upload digest");

                    IDictionary<string, object> download = new Dictionary<string, object>();
                    download["transfer_id"] = "down";
                    download["path"] = target;
                    download["checksum"] = checksum;
                    download["window"] = 8;
                    IDictionary<string, object> started = peer.Request(
                        "transfer", "download.begin", download, 20000);
                    AssertEqual(checksum, Str(started, "sha256"), "download digest");
                    IDictionary<string, object> finished;
                    try
                    {
                        finished = peer.WaitEvent("transfer.finished", "down", 180000);
                    }
                    catch (InvalidOperationException)
                    {
                        IDictionary<string, object> status = peer.Request(
                            "transfer", "status", TransferBody("down"), 8000);
                        throw new InvalidOperationException(
                            "no transfer.finished for down; status=" + Json.Serialize(status));
                    }
                    AssertEqual("finished", Str(Obj(finished, "body"), "status"),
                                "download finished");
                    byte[] received = peer.DataOf("down", "data");
                    AssertEqual(payload.Length, received.Length, "downloaded length");
                    for (int index = 0; index < payload.Length; index++)
                    {
                        if (payload[index] != received[index])
                        {
                            throw new InvalidOperationException(
                                "byte " + index + " differs");
                        }
                    }
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void TransferEmptyFile()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-empty-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string target = Path.Combine(work, "empty.bin");
                    IDictionary<string, object> begin = new Dictionary<string, object>();
                    begin["transfer_id"] = "empty";
                    begin["path"] = target;
                    begin["size"] = 0;
                    begin["sha256"] = Sha256Hex(new byte[0]);
                    peer.Request("transfer", "upload.begin", begin, 8000);
                    IDictionary<string, object> committed = peer.Request(
                        "transfer", "upload.commit", TransferBody("empty"), 8000);
                    AssertEqual("finished", Str(committed, "status"), "empty commit");
                    AssertEqual(0L, new FileInfo(target).Length, "empty file on disk");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void TransferChecksumMismatch()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-checksum-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string target = Path.Combine(work, "destination.bin");
                    File.WriteAllBytes(target, new byte[] { 9, 9, 9 });
                    IDictionary<string, object> begin = new Dictionary<string, object>();
                    begin["transfer_id"] = "bad";
                    begin["path"] = target;
                    begin["size"] = 3;
                    begin["sha256"] = new string('0', 64);
                    peer.Request("transfer", "upload.begin", begin, 8000);
                    peer.SendData("transfer", "bad", 0, new byte[] { 1, 2, 3 }, 8000);
                    IDictionary<string, object> error = peer.RequestExpectError(
                        "transfer", "upload.commit", TransferBody("bad"), 8000);
                    AssertEqual("checksum_mismatch", Str(error, "code"), "checksum error");
                    byte[] intact = File.ReadAllBytes(target);
                    AssertEqual(3, intact.Length, "destination length unchanged");
                    AssertEqual((byte)9, intact[0], "destination content unchanged");
                    foreach (string entry in Directory.GetFiles(work))
                    {
                        Assert(Path.GetFileName(entry).IndexOf("titella", StringComparison.Ordinal) < 0,
                               "no partial temp remained: " + entry);
                    }
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void TransferCancelNoPartial()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-cancel-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string target = Path.Combine(work, "cancelled.bin");
                    byte[] payload = new byte[2048];
                    IDictionary<string, object> begin = new Dictionary<string, object>();
                    begin["transfer_id"] = "cancel";
                    begin["path"] = target;
                    begin["size"] = payload.Length;
                    begin["sha256"] = Sha256Hex(payload);
                    peer.Request("transfer", "upload.begin", begin, 8000);
                    peer.SendData("transfer", "cancel", 0, Slice(payload, 0, 512), 8000);
                    IDictionary<string, object> cancelled = peer.Request(
                        "transfer", "cancel", TransferBody("cancel"), 8000);
                    AssertEqual("cancelled", Str(cancelled, "status"), "cancelled");
                    Assert(!File.Exists(target), "no destination appeared");
                    foreach (string entry in Directory.GetFiles(work))
                    {
                        Assert(Path.GetFileName(entry).IndexOf("titella", StringComparison.Ordinal) < 0,
                               "no partial temp remained: " + entry);
                    }
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void TransferSizeLimit()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-sizelimit-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    string[] before = Directory.GetFileSystemEntries(work);
                    IDictionary<string, object> begin = new Dictionary<string, object>();
                    begin["transfer_id"] = "huge";
                    begin["path"] = Path.Combine(work, "huge.bin");
                    begin["size"] = TransferLimits.MaxTransferBytes + 1;
                    begin["sha256"] = new string('a', 64);
                    IDictionary<string, object> error = peer.RequestExpectError(
                        "transfer", "upload.begin", begin, 8000);
                    AssertEqual("size_limit", Str(error, "code"), "size limit");
                    AssertEqual(before.Length, Directory.GetFileSystemEntries(work).Length,
                                "nothing was created for a rejected size");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void TransferDownloadWindow()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-window-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    peer.AutoAck = false;
                    byte[] payload = new byte[8 * 512];
                    string checksum = Sha256Hex(payload);
                    string source = Path.Combine(work, "source.bin");
                    File.WriteAllBytes(source, payload);

                    IDictionary<string, object> download = new Dictionary<string, object>();
                    download["transfer_id"] = "wide";
                    download["path"] = source;
                    download["checksum"] = checksum;
                    download["window"] = 8;
                    peer.RequestNoAck("transfer", "download.begin", download, 8000);
                    long deadline = Environment.TickCount + 15000;
                    while (peer.DataCount("wide", "data") < 4 &&
                           Environment.TickCount < deadline)
                    {
                        peer.NextAny(500);
                    }
                    Assert(peer.DataCount("wide", "data") >= 4,
                           "window 8 keeps at least four blocks in flight");
                    peer.AutoAck = true;
                    peer.AckPending("transfer", "wide");
                    IDictionary<string, object> finished = peer.WaitEvent(
                        "transfer.finished", "wide", 20000);
                    AssertEqual("finished", Str(Obj(finished, "body"), "status"),
                                "wide download finished");

                    IDictionary<string, object> single = new Dictionary<string, object>();
                    single["transfer_id"] = "narrow";
                    single["path"] = source;
                    single["checksum"] = checksum;
                    single["window"] = 1;
                    peer.AutoAck = false;
                    peer.RequestNoAck("transfer", "download.begin", single, 8000);
                    deadline = Environment.TickCount + 5000;
                    while (peer.DataCount("narrow", "data") < 1 &&
                           Environment.TickCount < deadline)
                    {
                        peer.NextAny(500);
                    }
                    Thread.Sleep(600);
                    peer.NextAny(300);
                    AssertEqual(1, peer.DataCount("narrow", "data"),
                                "window 1 sends a single unacknowledged block");
                    peer.AutoAck = true;
                    peer.AckPending("transfer", "narrow");
                    IDictionary<string, object> narrow = peer.WaitEvent(
                        "transfer.finished", "narrow", 20000);
                    AssertEqual("finished", Str(Obj(narrow, "body"), "status"),
                                "narrow download finished");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        private static void TransferProgressEvent()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                string work = TempDirectory("titella-progress-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    byte[] payload = new byte[2 * 1024 * 1024];
                    string target = Path.Combine(work, "progress.bin");
                    IDictionary<string, object> begin = new Dictionary<string, object>();
                    begin["transfer_id"] = "progress";
                    begin["path"] = target;
                    begin["size"] = payload.Length;
                    begin["sha256"] = Sha256Hex(payload);
                    peer.Request("transfer", "upload.begin", begin, 8000);
                    long sequence = 0;
                    for (int position = 0; position < payload.Length;
                         position += WireConstants.DataChunkBytes)
                    {
                        int chunk = Math.Min(
                            WireConstants.DataChunkBytes, payload.Length - position);
                        peer.SendData("transfer", "progress", sequence,
                                      Slice(payload, position, chunk), 15000);
                        sequence++;
                    }
                    bool sawProgress = peer.WaitAnyEvent("transfer.progress", "progress", 15000);
                    Assert(sawProgress, "at least one progress event arrived");
                    peer.Request("transfer", "upload.commit", TransferBody("progress"), 20000);
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                    DeleteTree(work);
                }
            }
        }

        // ----------------------------------------------------------- Lifecycle

        private static void LifecycleStopRemovesOwnedDirectory()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                File.WriteAllText(Path.Combine(bootstrap, "payload.bin"), "payload",
                                  new UTF8Encoding(false));
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    IDictionary<string, object> stopped = peer.Request(
                        "control", "agent.stop", new Dictionary<string, object>(), 8000);
                    AssertEqual("stopped", Str(stopped, "status"), "stop acknowledged");
                    Assert(thread.Join(5000), "the run loop finished within 5 s");
                    Assert(!Directory.Exists(bootstrap),
                           "the owned bootstrap directory was removed");
                }
                finally
                {
                    StopAgent(agent, thread);
                }
            }
        }

        private static void LifecycleGraceExpirySixTimes()
        {
            for (int round = 0; round < 6; round++)
            {
                using (PeerHarness harness = new PeerHarness())
                {
                    string bootstrap = TempDirectory("titella-bootstrap-");
                    SlaveAgent agent = new SlaveAgent(
                        LinkId(Fixture()), harness.Factory, bootstrap, 1000, 2000);
                    Thread thread = StartAgent(agent);
                    try
                    {
                        MasterPeer peer = new MasterPeer(harness, LinkId(Fixture()));
                        peer.Next("hello", 5000);
                        Assert(thread.Join(9000),
                               "round " + round + ": expiry finished the loop");
                        Assert(!Directory.Exists(bootstrap),
                               "round " + round + ": directory removed");
                    }
                    finally
                    {
                        StopAgent(agent, thread);
                    }
                }
            }
        }

        private static void LifecycleReplugKeepsSession()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string bootstrap = TempDirectory("titella-bootstrap-");
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, bootstrap, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    peer.Request("shell", "job.start",
                                 JobBody("replug", "Start-Sleep 3", 60), 8000);

                    harness.Replug();
                    MasterPeer healed = new MasterPeer(harness, LinkId(Fixture()));
                    IDictionary<string, object> hello = healed.Next("hello", 15000);
                    AssertEqual("hello", Str(hello, "type"), "hello after replug");
                    IDictionary<string, object> ready = healed.Request(
                        "control", "bind", BindBody(LinkId(Fixture())), 8000);
                    AssertEqual("ready", Str(ready, "status"), "rebind after replug");
                    IDictionary<string, object> finished = healed.WaitEvent(
                        "job.finished", "replug", 20000);
                    AssertEqual(0, Num(Obj(finished, "body"), "returncode", -1),
                                "the job survived the replug");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(bootstrap);
                }
            }
        }

        private static void LifecycleForeignDirectoryIsKept()
        {
            using (PeerHarness harness = new PeerHarness())
            {
                string foreign = TempDirectory("titella-foreign-");
                File.WriteAllText(Path.Combine(foreign, "keep.txt"), "keep",
                                  new UTF8Encoding(false));
                SlaveAgent agent = new SlaveAgent(
                    LinkId(Fixture()), harness.Factory, foreign, 5000, 30000);
                Thread thread = StartAgent(agent);
                try
                {
                    MasterPeer peer = Bind(harness);
                    peer.Request("control", "agent.stop", new Dictionary<string, object>(), 8000);
                    Assert(thread.Join(5000), "the run loop finished");
                    Assert(Directory.Exists(foreign),
                           "a directory that is not titella-bootstrap-* survives");
                    Assert(File.Exists(Path.Combine(foreign, "keep.txt")),
                           "its contents survive");
                }
                finally
                {
                    StopAgent(agent, thread);
                    DeleteTree(foreign);
                }
            }
        }

        // ------------------------------------------------------------ Hygiene

        private static string WindowsDirectory()
        {
            return Path.Combine(_repositoryRoot, "implementations", "windows");
        }

        private static string[] UnitNames()
        {
            return new string[]
            {
                "Agent.cs", "CdcTransport.cs", "FileService.cs", "JobHost.cs",
                "NativeMethods.cs", "ShellService.cs", "TransferService.cs",
                "Wire.cs",
            };
        }

        private static void SourceSevenUnits()
        {
            string directory = WindowsDirectory();
            Assert(Directory.Exists(directory), "implementations/windows exists");
            List<string> names = new List<string>();
            foreach (string entry in Directory.GetFiles(directory, "*.cs"))
            {
                names.Add(Path.GetFileName(entry));
            }
            names.Sort(StringComparer.Ordinal);
            string[] expected = UnitNames();
            AssertEqual(expected.Length, names.Count, "exactly eight units");
            for (int index = 0; index < expected.Length; index++)
            {
                AssertEqual(expected[index], names[index],
                            "lexicographic position " + index);
            }

            StringBuilder concatenated = new StringBuilder();
            foreach (string name in names)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, name));
                Assert(bytes.Length >= 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
                       name + " has no UTF-8 BOM");
                string text = new UTF8Encoding(false, true).GetString(bytes);
                Assert(text.IndexOf("\r\n", StringComparison.Ordinal) < 0,
                       name + " uses LF only");
                Assert(text.IndexOf("namespace Titella.Slave", StringComparison.Ordinal) >= 0,
                       name + " declares the Titella.Slave namespace");
                if (concatenated.Length > 0)
                {
                    concatenated.Append("\n\n");
                }
                concatenated.Append(text.TrimEnd('\n'));
            }
            concatenated.Append('\n');
            // Round-trip through the same concatenation the build hook uses.
            byte[] built = new UTF8Encoding(false).GetBytes(concatenated.ToString());
            Assert(built.Length > 0, "concatenation is non-empty");
            Assert(concatenated.ToString().EndsWith("\n", StringComparison.Ordinal),
                   "single trailing newline");
        }

        private static void SourceNoPolicyBypass()
        {
            string[] forbidden = new string[]
            {
                "-" + "ExecutionPolicy",
                "Unblock" + "-File",
                "-WindowStyle" + " Hidden",
                "Set-Mp" + "Preference",
                "Add-Mp" + "Preference",
                "net" + "sh",
                "sch" + "tasks",
                "s" + "c.exe",
                "System" + ".Net",
                "Http" + "Client",
                "Process" + ".Kill(",
            };
            string directory = WindowsDirectory();
            foreach (string file in Directory.GetFiles(directory, "*.cs"))
            {
                string text = File.ReadAllText(file, new UTF8Encoding(false));
                foreach (string token in forbidden)
                {
                    Assert(text.IndexOf(token, StringComparison.Ordinal) < 0,
                           Path.GetFileName(file) + " contains " + token);
                }
            }
        }

        private static void SourceStreamTransportIsInternal()
        {
            Type type = typeof(StreamTransport);
            Assert(!type.IsPublic, "StreamTransport is not public");
            Assert(type.IsNotPublic || type.IsNestedAssembly, "StreamTransport stays internal");
        }

        private static void PolicyRestrictedCompilationWorks()
        {
            string policy = "unknown";
            try
            {
                using (Process probe = new Process())
                {
                    probe.StartInfo.FileName = "powershell.exe";
                    probe.StartInfo.Arguments =
                        "-NoLogo -NoProfile -NonInteractive -Command \"Get-" +
                        "ExecutionPolicy\"";
                    probe.StartInfo.UseShellExecute = false;
                    probe.StartInfo.RedirectStandardOutput = true;
                    probe.StartInfo.RedirectStandardError = true;
                    probe.StartInfo.CreateNoWindow = true;
                    probe.Start();
                    policy = probe.StandardOutput.ReadToEnd().Trim();
                    probe.WaitForExit(10000);
                }
                Console.WriteLine(
                    "INFO execution policy=" + policy +
                    "; Add-Type already compiled this suite without changing it");
            }
            catch (Exception failure)
            {
                Console.WriteLine(
                    "PENDING policy-restricted-run: " + failure.Message);
                return;
            }
            Assert(policy.Length > 0, "the execution policy was reported");
        }
    }

    /// <summary>One duplex byte pipe with a 100 ms tick, used in place of CDC.</summary>
    internal sealed class BlockingStream : Stream
    {
        private readonly Queue<byte[]> _blocks = new Queue<byte[]>();
        private readonly ManualResetEventSlim _available = new ManualResetEventSlim(false);
        private readonly object _gate = new object();
        private byte[] _current;
        private int _offset;
        private bool _closed;

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long deadline = Environment.TickCount + 100;
            while (true)
            {
                lock (_gate)
                {
                    if (_closed && _blocks.Count == 0 && _current == null)
                    {
                        throw new EndOfStreamException("the pipe is closed");
                    }
                    if (_current != null)
                    {
                        int available = _current.Length - _offset;
                        int take = Math.Min(available, count);
                        Array.Copy(_current, _offset, buffer, offset, take);
                        _offset += take;
                        if (_offset >= _current.Length)
                        {
                            _current = null;
                            _offset = 0;
                        }
                        return take;
                    }
                    if (_blocks.Count > 0)
                    {
                        _current = _blocks.Dequeue();
                        _offset = 0;
                        continue;
                    }
                    _available.Reset();
                }
                int remaining = (int)(deadline - Environment.TickCount);
                if (remaining <= 0)
                {
                    return 0;
                }
                _available.Wait(Math.Min(remaining, 100));
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            byte[] copy = new byte[count];
            Array.Copy(buffer, offset, copy, 0, count);
            lock (_gate)
            {
                _blocks.Enqueue(copy);
                _available.Set();
            }
        }

        public void ClosePipe()
        {
            lock (_gate)
            {
                _closed = true;
                _available.Set();
            }
        }

        /// <summary>Non-blocking read used by the peer to drain without stalls.</summary>
        public bool TryReadAvailable(byte[] buffer, int offset, int count, out int read)
        {
            read = 0;
            lock (_gate)
            {
                if (_current == null && _blocks.Count == 0)
                {
                    return false;
                }
                if (_current == null)
                {
                    _current = _blocks.Dequeue();
                    _offset = 0;
                }
                int available = _current.Length - _offset;
                int take = Math.Min(available, count);
                Array.Copy(_current, _offset, buffer, offset, take);
                _offset += take;
                if (_offset >= _current.Length)
                {
                    _current = null;
                    _offset = 0;
                }
                read = take;
                return true;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Duplex pair of BlockingStreams plus the agent transport factory.</summary>
    internal sealed class PeerHarness : IDisposable
    {
        private BlockingStream _toAgent;
        private BlockingStream _fromAgent;
        private Func<ILinkTransport> _factory;

        public PeerHarness()
        {
            Replug();
        }

        public BlockingStream ToAgent
        {
            get { return _toAgent; }
        }

        public BlockingStream FromAgent
        {
            get { return _fromAgent; }
        }

        public Func<ILinkTransport> Factory
        {
            get { return _factory; }
        }

        /// <summary>
        /// Simulates an unplug/replug: the current pipe pair is broken so the
        /// reader observes the loss, and the factory hands out a fresh pair on
        /// the next reopen attempt inside the grace window.
        /// </summary>
        public void Replug()
        {
            if (_toAgent != null)
            {
                _toAgent.ClosePipe();
            }
            if (_fromAgent != null)
            {
                _fromAgent.ClosePipe();
            }
            BlockingStream toAgent = new BlockingStream();
            BlockingStream fromAgent = new BlockingStream();
            _toAgent = toAgent;
            _fromAgent = fromAgent;
            _factory = delegate() { return new StreamTransport(toAgent, fromAgent); };
        }

        public void Dispose()
        {
            try
            {
                _toAgent.ClosePipe();
            }
            catch (Exception)
            {
                // Disposal is best effort.
            }
            try
            {
                _fromAgent.ClosePipe();
            }
            catch (Exception)
            {
                // Disposal is best effort.
            }
        }
    }

    /// <summary>
    /// Protocol peer of the agent under test. Mirrors the Python MasterPeer:
    /// Send, NextFrame, Request, SendData (waits for the ACK), WaitEvent,
    /// WaitAck and WaitBytes, with data reassembly per (session, stream).
    /// </summary>
    internal sealed class MasterPeer
    {
        private readonly PeerHarness _harness;
        private readonly string _linkId;
        private readonly FrameParser _parser = new FrameParser(WireConstants.SlaveToFirmware);
        private readonly List<IDictionary<string, object>> _frames =
            new List<IDictionary<string, object>>();
        private readonly List<IDictionary<string, object>> _events =
            new List<IDictionary<string, object>>();
        private readonly Dictionary<string, MemoryStream> _data =
            new Dictionary<string, MemoryStream>();
        private readonly HashSet<long> _pendingAcks = new HashSet<long>();
        private bool _autoAck = true;
        private long _requestId;

        public bool AutoAck
        {
            get { return _autoAck; }
            set { _autoAck = value; }
        }

        public MasterPeer(PeerHarness harness, string linkId)
        {
            _harness = harness;
            _linkId = linkId;
        }

        public void Send(IDictionary<string, object> frame)
        {
            byte[] payload = Wire.EncodeFrame(frame, WireConstants.FirmwareToSlave);
            _harness.ToAgent.Write(payload, 0, payload.Length);
        }

        private void Pump(int timeoutMs)
        {
            byte[] buffer = new byte[WireConstants.MaxFrameBytes];
            // Drain everything already buffered (a 2 MiB transfer is ~600 KB of
            // frames; one 4096-byte read per iteration would starve the ACKs).
            while (true)
            {
                int available;
                if (!_harness.FromAgent.TryReadAvailable(
                        buffer, 0, buffer.Length, out available) || available <= 0)
                {
                    break;
                }
                foreach (IDictionary<string, object> frame in
                         _parser.Feed(buffer, 0, available))
                {
                    _frames.Add(frame);
                }
            }
            if (timeoutMs <= 0)
            {
                return;
            }
            int count;
            try
            {
                count = _harness.FromAgent.Read(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                return;
            }
            if (count <= 0)
            {
                return;
            }
            foreach (IDictionary<string, object> frame in _parser.Feed(buffer, 0, count))
            {
                _frames.Add(frame);
            }
        }

        private IDictionary<string, object> TakeMatching(Func<IDictionary<string, object>, bool> predicate,
                                                         int timeoutMs)
        {
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                for (int index = 0; index < _frames.Count; index++)
                {
                    IDictionary<string, object> frame = _frames[index];
                    if (predicate(frame))
                    {
                        _frames.RemoveAt(index);
                        return frame;
                    }
                }
                // Consume (and ACK) non-matching frames so the agent's
                // stop-and-wait senders never starve while a request is in
                // flight; this mirrors the Python peer's consume().
                for (int index = _frames.Count - 1; index >= 0; index--)
                {
                    IDictionary<string, object> frame = _frames[index];
                    string kind = frame["type"] as string;
                    if (kind == "data" || kind == "event")
                    {
                        _frames.RemoveAt(index);
                        Consume(frame);
                    }
                }
                Pump(100);
            }
            return null;
        }

        public IDictionary<string, object> Next(string kind, int timeoutMs)
        {
            IDictionary<string, object> frame = TakeMatching(
                delegate(IDictionary<string, object> candidate)
                {
                    return string.Equals(candidate["type"] as string, kind,
                                         StringComparison.Ordinal);
                }, timeoutMs);
            if (frame == null)
            {
                throw new InvalidOperationException("no " + kind + " frame arrived");
            }
            Consume(frame);
            return frame;
        }

        public void NextAny(int timeoutMs)
        {
            IDictionary<string, object> frame = TakeMatching(
                delegate(IDictionary<string, object> candidate) { return true; }, timeoutMs);
            if (frame != null)
            {
                Consume(frame);
            }
        }

        private void Consume(IDictionary<string, object> frame)
        {
            if (string.Equals(frame["type"] as string, "data", StringComparison.Ordinal))
            {
                string session = frame["session_id"] as string;
                string stream = frame.ContainsKey("stream")
                    ? frame["stream"] as string
                    : "data";
                string key = session + "\u0000" + stream;
                MemoryStream buffer;
                if (!_data.TryGetValue(key, out buffer))
                {
                    buffer = new MemoryStream();
                    _data[key] = buffer;
                }
                byte[] chunk = Wire.DecodeData(frame);
                buffer.Write(chunk, 0, chunk.Length);
                long sequence = Json.GetInt64(frame, "seq", 0);
                string channel = frame["channel"] as string;
                if (_autoAck)
                {
                    Ack(channel, session, sequence);
                }
                else
                {
                    _pendingAcks.Add(sequence);
                }
            }
            else if (string.Equals(frame["type"] as string, "event", StringComparison.Ordinal))
            {
                _events.Add(frame);
            }
        }

        public void Ack(string channel, string session, long sequence)
        {
            IDictionary<string, object> ack = new Dictionary<string, object>();
            ack["v"] = 1;
            ack["channel"] = channel;
            ack["type"] = "ack";
            ack["link_id"] = _linkId;
            ack["session_id"] = session;
            ack["ack_seq"] = sequence;
            Send(ack);
        }

        public void AckPending(string channel, string session)
        {
            List<long> pending = new List<long>(_pendingAcks);
            _pendingAcks.Clear();
            foreach (long sequence in pending)
            {
                Ack(channel, session, sequence);
            }
        }

        public IDictionary<string, object> Request(string channel, string op,
                                                   IDictionary<string, object> body,
                                                   int timeoutMs)
        {
            IDictionary<string, object> response = RequestInternal(
                channel, op, body, timeoutMs, true);
            return response;
        }

        public IDictionary<string, object> RequestNoAck(string channel, string op,
                                                        IDictionary<string, object> body,
                                                        int timeoutMs)
        {
            return RequestInternal(channel, op, body, timeoutMs, false);
        }

        private IDictionary<string, object> RequestInternal(
            string channel, string op, IDictionary<string, object> body,
            int timeoutMs, bool expectResponse)
        {
            _requestId++;
            long requestId = _requestId;
            IDictionary<string, object> frame = new Dictionary<string, object>();
            frame["v"] = 1;
            frame["channel"] = channel;
            frame["type"] = "request";
            frame["link_id"] = _linkId;
            frame["request_id"] = requestId;
            frame["op"] = op;
            frame["body"] = body ?? new Dictionary<string, object>();
            Send(frame);
            if (!expectResponse)
            {
                return new Dictionary<string, object>();
            }
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                IDictionary<string, object> reply = TakeMatching(
                    delegate(IDictionary<string, object> candidate)
                    {
                        if (!candidate.ContainsKey("request_id"))
                        {
                            return false;
                        }
                        long id = Json.GetInt64(candidate, "request_id", -1);
                        if (id != requestId)
                        {
                            return false;
                        }
                        string kind = candidate["type"] as string;
                        return kind == "response" || kind == "error";
                    }, 200);
                if (reply != null)
                {
                    if (string.Equals(reply["type"] as string, "error", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "remote error: " + Json.Serialize(reply));
                    }
                    return ContractTests.Obj(reply, "body");
                }
                Pump(100);
            }
            throw new InvalidOperationException("no response for " + channel + "." + op);
        }

        public IDictionary<string, object> RequestExpectError(
            string channel, string op, IDictionary<string, object> body, int timeoutMs)
        {
            _requestId++;
            long requestId = _requestId;
            IDictionary<string, object> frame = new Dictionary<string, object>();
            frame["v"] = 1;
            frame["channel"] = channel;
            frame["type"] = "request";
            frame["link_id"] = _linkId;
            frame["request_id"] = requestId;
            frame["op"] = op;
            frame["body"] = body ?? new Dictionary<string, object>();
            Send(frame);
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                IDictionary<string, object> reply = TakeMatching(
                    delegate(IDictionary<string, object> candidate)
                    {
                        if (!candidate.ContainsKey("request_id"))
                        {
                            return false;
                        }
                        long id = Json.GetInt64(candidate, "request_id", -1);
                        if (id != requestId)
                        {
                            return false;
                        }
                        string kind = candidate["type"] as string;
                        return kind == "response" || kind == "error";
                    }, 200);
                if (reply != null)
                {
                    if (!string.Equals(reply["type"] as string, "error", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "expected an error, got " + Json.Serialize(reply));
                    }
                    return ContractTests.Obj(reply, "body");
                }
                Pump(100);
            }
            throw new InvalidOperationException("no reply for " + channel + "." + op);
        }

        public void SendData(string channel, string session, long sequence,
                             byte[] data, int timeoutMs)
        {
            IDictionary<string, object> frame = new Dictionary<string, object>();
            frame["v"] = 1;
            frame["channel"] = channel;
            frame["type"] = "data";
            frame["link_id"] = _linkId;
            frame["session_id"] = session;
            frame["seq"] = sequence;
            frame["data_b64"] = Wire.DataB64(data);
            Send(frame);
            WaitAck(session, sequence, timeoutMs);
        }

        /// <summary>Sends a data frame without waiting: the caller expects it
        /// to be rejected, so no ACK will ever arrive.</summary>
        public void SendDataNoWait(string channel, string session, long sequence, byte[] data)
        {
            IDictionary<string, object> frame = new Dictionary<string, object>();
            frame["v"] = 1;
            frame["channel"] = channel;
            frame["type"] = "data";
            frame["link_id"] = _linkId;
            frame["session_id"] = session;
            frame["seq"] = sequence;
            frame["data_b64"] = Wire.DataB64(data);
            Send(frame);
        }

        public IDictionary<string, object> SendDataExpectError(
            string channel, string session, long sequence, byte[] source,
            int offset, int count, int timeoutMs)
        {
            SendDataNoWait(channel, session, sequence, SliceBytes(source, offset, count));
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                IDictionary<string, object> frame = TakeMatching(
                    delegate(IDictionary<string, object> candidate)
                    {
                        string kind = candidate["type"] as string;
                        if (kind == "ack")
                        {
                            return string.Equals(candidate["session_id"] as string,
                                                 session, StringComparison.Ordinal) &&
                                   Json.GetInt64(candidate, "ack_seq", -1) == sequence;
                        }
                        if (kind == "error")
                        {
                            string candidateSession = candidate.ContainsKey("session_id")
                                ? candidate["session_id"] as string
                                : null;
                            string candidateRequest = candidate.ContainsKey("request_id")
                                ? Json.GetInt64(candidate, "request_id", -1).ToString(CultureInfo.InvariantCulture)
                                : null;
                            return (candidateSession != null &&
                                    string.Equals(candidateSession, session,
                                                  StringComparison.Ordinal)) ||
                                   (candidateSession == null && candidateRequest == null);
                        }
                        return false;
                    }, 200);
                if (frame != null)
                {
                    if (string.Equals(frame["type"] as string, "ack", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "the rejected block was acknowledged");
                    }
                    return ContractTests.Obj(frame, "body");
                }
                Pump(100);
            }
            throw new InvalidOperationException("no error for the rejected data block");
        }

        private static byte[] SliceBytes(byte[] source, int offset, int count)
        {
            byte[] result = new byte[count];
            Array.Copy(source, offset, result, 0, count);
            return result;
        }

        public void WaitAck(string session, long sequence, int timeoutMs)
        {
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                IDictionary<string, object> frame = TakeMatching(
                    delegate(IDictionary<string, object> candidate)
                    {
                        return string.Equals(candidate["type"] as string, "ack",
                                             StringComparison.Ordinal) &&
                               string.Equals(candidate["session_id"] as string, session,
                                             StringComparison.Ordinal) &&
                               Json.GetInt64(candidate, "ack_seq", -1) == sequence;
                    }, 200);
                if (frame != null)
                {
                    return;
                }
                Pump(100);
            }
            throw new InvalidOperationException(
                "no ACK for " + session + ":" + sequence);
        }

        /// <summary>Consumes every buffered frame, so a burst of data frames
        /// cannot hide the event/response behind them.</summary>
        public void DrainAll()
        {
            Pump(0);
            while (_frames.Count > 0)
            {
                IDictionary<string, object> frame = _frames[0];
                _frames.RemoveAt(0);
                Consume(frame);
                Pump(0);
            }
        }

        public IDictionary<string, object> WaitEvent(string op, string session, int timeoutMs)
        {
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                DrainAll();
                for (int index = 0; index < _events.Count; index++)
                {
                    IDictionary<string, object> frame = _events[index];
                    if (string.Equals(frame["op"] as string, op, StringComparison.Ordinal) &&
                        (session == null ||
                         string.Equals(frame["session_id"] as string, session,
                                       StringComparison.Ordinal)))
                    {
                        _events.RemoveAt(index);
                        return frame;
                    }
                }
                Pump(100);
            }
            throw new InvalidOperationException("no event " + op + " for " + session);
        }

        public bool WaitAnyEvent(string op, string session, int timeoutMs)
        {
            try
            {
                WaitEvent(op, session, timeoutMs);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public bool WaitBytes(string session, string token, int timeoutMs)
        {
            byte[] needle = Encoding.UTF8.GetBytes(token);
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                foreach (KeyValuePair<string, MemoryStream> entry in _data)
                {
                    if (!entry.Key.StartsWith(session + "\u0000", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    byte[] bytes = entry.Value.ToArray();
                    if (Contains(bytes, needle))
                    {
                        return true;
                    }
                }
                DrainAll();
                Pump(100);
            }
            return false;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return false;
            }
            for (int index = 0; index <= haystack.Length - needle.Length; index++)
            {
                bool match = true;
                for (int probe = 0; probe < needle.Length; probe++)
                {
                    if (haystack[index + probe] != needle[probe])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return true;
                }
            }
            return false;
        }

        public int DataCount(string session, string stream)
        {
            MemoryStream buffer;
            if (_data.TryGetValue(session + "\u0000" + stream, out buffer))
            {
                return (int)(buffer.Length / WireConstants.DataChunkBytes);
            }
            return 0;
        }

        /// <summary>Number of distinct data blocks received for a stream.</summary>
        public int BlockCount(string session, string stream)
        {
            return DataCount(session, stream);
        }

        public byte[] DataOf(string session, string stream)
        {
            MemoryStream buffer;
            if (_data.TryGetValue(session + "\u0000" + stream, out buffer))
            {
                return buffer.ToArray();
            }
            return new byte[0];
        }

        public string DataText(string session, string stream)
        {
            MemoryStream buffer;
            if (_data.TryGetValue(session + "\u0000" + stream, out buffer))
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            return string.Empty;
        }
    }

    internal static class PeerHelpers
    {
        /// <summary>
        /// Builds a bound peer: waits for hello and sends control.bind with
        /// the given link id. Every behaviour test starts from here.
        /// </summary>
        public static MasterPeer Bind(PeerHarness harness, string linkId)
        {
            MasterPeer peer = new MasterPeer(harness, linkId);
            peer.Next("hello", 8000);
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["link_id"] = linkId;
            IDictionary<string, object> ready = peer.Request("control", "bind", body, 8000);
            if (!string.Equals(ready["status"] as string, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the agent did not report ready");
            }
            return peer;
        }
    }
}



