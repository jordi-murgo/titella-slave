// Entry point and lifecycle of the ephemeral Windows slave agent. Port of
// implementations/python/__main__.py. The public Run method is invoked by
// the loader as
//   exit [Titella.Slave.Agent]::Run(bootstrapDir, linkId, port, hbMs, graceMs)
// so its return value is the process exit code.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;

    /// <summary>Public entry point called from PowerShell Add-Type.</summary>
    public static class Agent
    {
        /// <summary>
        /// Runs one ephemeral slave. Returns 0 on a clean stop or grace
        /// expiry, 2 on invalid arguments and 1 when an unexpected exception
        /// escapes. Terminal failures are logged inside the bootstrap
        /// directory when one was supplied.
        /// </summary>
        public static int Run(string bootstrapDirectory, string linkId, string port,
                              int heartbeatMilliseconds, int graceMilliseconds,
                              string implementation = AgentHost.ExeImplementation)
        {
            // Detach from the loader console: closing it must not stop the
            // agent, and the agent writes only to its own diagnostic file.
            try
            {
                NativeMethods.FreeConsole();
            }
            catch (Exception)
            {
                // Detaching is best effort; a missing console is not fatal.
            }

            string logPath = null;
            if (!string.IsNullOrEmpty(bootstrapDirectory))
            {
                try
                {
                    Directory.CreateDirectory(bootstrapDirectory);
                    logPath = Path.Combine(bootstrapDirectory, "agent.log");
                }
                catch (Exception)
                {
                    logPath = null;
                }
            }

            if (!string.IsNullOrEmpty(bootstrapDirectory))
            {
                try
                {
                    // Ownership marker of this bootstrap directory: the
                    // loader writes it with the console pid, the agent takes
                    // it over here, and the next bootstrap's sweep deletes
                    // the tombstone once this pid is gone.
                    File.WriteAllText(
                        Path.Combine(bootstrapDirectory, "owner.json"),
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{{\"session_id\":\"{0}\",\"pid\":{1}}}",
                            linkId, ProcessId),
                        new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // A marker that cannot be written never blocks the run.
                }
            }

            if (!WireConstants.IsLinkId(linkId))
            {
                Log(logPath, "invalid link_id: 32 lowercase hexadecimal characters are required");
                return 2;
            }
            // The window rig renders the CLIPBOARD sentinel instead of a COM
            // port: the same protocol then runs over the shared clipboard.
            // Native USB rigs render HID:<serial> instead: the same protocol
            // then runs over the vendor data collection of that device.
            bool clipboardPort = ClipboardTransport.IsClipboardPort(port);
            bool hidPort = HidTransport.IsHidBinding(port);
            if (!clipboardPort && !hidPort && !ComTransport.IsValidPortName(port))
            {
                Log(logPath, string.IsNullOrEmpty(port)
                    ? "cdc_not_provided: the loader must resolve the COM port first"
                    : "invalid cdc port: COM[1-9][0-9]*, CLIPBOARD or HID:<serial> is required");
                return 2;
            }

            int heartbeatMs = Math.Max(1000, heartbeatMilliseconds);
            int graceMs = Math.Max(graceMilliseconds, heartbeatMs);

            SlaveAgent agent = null;
            try
            {
                string resolvedPort = port;
                agent = new SlaveAgent(
                    linkId,
                    delegate()
                    {
                        if (clipboardPort)
                        {
                            ClipboardTransport clipboard = new ClipboardTransport(linkId);
                            clipboard.Open();
                            return clipboard;
                        }
                        if (hidPort)
                        {
                            HidTransport hid = new HidTransport(resolvedPort);
                            hid.Open();
                            return hid;
                        }
                        ComTransport transport = new ComTransport(resolvedPort);
                        transport.Open();
                        return transport;
                    },
                    bootstrapDirectory,
                    heartbeatMs,
                    graceMs,
                    implementation);
                agent.Run();
                return 0;
            }
            catch (Exception error)
            {
                Log(logPath, error.ToString());
                return 1;
            }
            finally
            {
                if (agent != null)
                {
                    agent.Cleanup(true);
                }
            }
        }

        private static void Log(string logPath, string message)
        {
            if (string.IsNullOrEmpty(logPath))
            {
                return;
            }
            try
            {
                string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    " " + message + Environment.NewLine;
                File.AppendAllText(logPath, line, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // A diagnostic log that cannot be written never masks the
                // real failure.
            }
        }

        private static int ProcessId
        {
            get
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id;
            }
        }

        /// <summary>
        /// Version of the installed .NET Framework 4.x, read from the registry
        /// value the framework writes (never Environment.Version, which is the
        /// CLR version). Returns null when the key is unreadable.
        /// </summary>
        public static string NdpVersion()
        {
            try
            {
                object value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
                    "Version",
                    null);
                return value as string;
            }
            catch (Exception)
            {
                return null;
            }
        }
        /// <summary>
        /// Location of the compiled agent assembly: agent.exe when the loader
        /// compiled an executable, agent.dll when it compiled a library for a
        /// PowerShell host. Job children are launched from this location
        /// (see <see cref="JobManager"/>). When the agent is hosted in a test
        /// process the location is that host, which is still the image the
        /// job children run.
        /// </summary>
        internal static string ExecutableAssembly()
        {
            return typeof(SlaveAgent).Assembly.Location;
        }

    }

    /// <summary>One ephemeral agent: link, dispatch loop and cleanup.</summary>
    internal sealed class SlaveAgent
    {
        private const int BindAttempts = 20;
        private const int BindRetryDelayMs = 50;

        private readonly string _linkId;
        private readonly string _bootstrapDirectory;
        private readonly string _implementation;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _cleanupDone = new ManualResetEventSlim(false);
        private readonly object _cleanupLock = new object();
        private readonly Deduplicator _dedup = new Deduplicator();
        private readonly ProcessRegistry _registry = new ProcessRegistry(4);
        private readonly AgentLink _link;
        private readonly JobManager _jobs;
        private readonly TerminalManager _terminals;
        private readonly FileService _files = new FileService();
        private readonly TransferService _transfers;
        // Screenshot blocks in flight. 8 is the transfer channel's default and
        // measured fastest here: a 1.7 MiB stream took 35-55 s at 8 against
        // 48-51 s at 2. Frames lost mid-flight are recovered by the per-block
        // retransmit; the master additionally refuses to publish a payload
        // whose byte count disagrees with the declared size, so a loss it
        // cannot recover surfaces as a retryable screen_capture_failed instead
        // of a corrupt image.
        private const int ScreenWindow = WireConstants.DataWindowDefault;
        private bool _cleaned;
        private bool _bound;
        private ProtocolError _capabilityError;
        private bool _capabilityChecked;

        public SlaveAgent(string linkId, ILinkTransport transport,
                          string bootstrapDirectory, int heartbeatMs, int graceMs,
                          string implementation = AgentHost.ExeImplementation)
        {
            _linkId = linkId;
            _bootstrapDirectory = bootstrapDirectory;
            _implementation = implementation;
            _link = new AgentLink(
                linkId,
                delegate()
                {
                    if (!transport.IsOpen)
                    {
                        throw new LinkLostException(0, "the transport is closed");
                    }
                    return transport;
                },
                heartbeatMs,
                graceMs,
                Hello,
                delegate() { _stop.Set(); });
            _jobs = new JobManager(_link, _registry);
            _terminals = new TerminalManager(_link, _registry);
            _transfers = new TransferService(_link);
        }

        public SlaveAgent(string linkId, Func<ILinkTransport> transportFactory,
                          string bootstrapDirectory, int heartbeatMs, int graceMs,
                          string implementation = AgentHost.ExeImplementation)
        {
            _linkId = linkId;
            _bootstrapDirectory = bootstrapDirectory;
            _implementation = implementation;
            _link = new AgentLink(
                linkId,
                transportFactory,
                heartbeatMs,
                graceMs,
                Hello,
                delegate() { _stop.Set(); });
            _jobs = new JobManager(_link, _registry);
            _terminals = new TerminalManager(_link, _registry);
            _transfers = new TransferService(_link);
        }

        internal ProcessRegistry Registry
        {
            get { return _registry; }
        }

        internal bool Bound
        {
            get { return _bound; }
        }

        private void Hello()
        {
            IDictionary<string, object> runtime = new Dictionary<string, object>();
            runtime["name"] = "dotnet-framework";
            runtime["version"] = Agent.NdpVersion();
            IDictionary<string, object> body = new Dictionary<string, object>();
            body["agent"] = "titella-slave";
            body["python"] = null;
            body["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            body["uid"] = null;
            body["implementation"] = _implementation;
            body["runtime"] = runtime;
            _link.SendFrame(Wire.BaseFrame(
                _linkId, "control", "hello", "body", body));
        }

        public void Run()
        {
            _link.Start();
            try
            {
                while (!_stop.IsSet && !_link.Expired)
                {
                    IDictionary<string, object> frame;
                    if (!_link.Inbound.TryDequeue(250, out frame))
                    {
                        continue;
                    }
                    Dispatch(frame);
                }
            }
            finally
            {
                Cleanup(true);
            }
        }

        public void Cleanup(bool removePayload)
        {
            lock (_cleanupLock)
            {
                if (_cleaned)
                {
                    _cleanupDone.Wait(30000);
                    return;
                }
                _cleaned = true;
            }
            try
            {
                _jobs.Shutdown();
                _terminals.Shutdown();
                _transfers.Shutdown();
                _link.Close();
                if (removePayload && !string.IsNullOrEmpty(_bootstrapDirectory))
                {
                    RemoveOwnedBootstrap();
                }
            }
            finally
            {
                _cleanupDone.Set();
            }
        }

        /// <summary>
        /// Cleans only this agent's own bootstrap directory: the name must
        /// be a bare 16-hex-digit GUID segment and the parent must be the
        /// user TEMP directory. The running image (not deletable while the
        /// process lives) and owner.json are kept as the tombstone the next
        /// bootstrap's sweep removes; everything else is deleted. Reparse
        /// points are unlinked, never descended into, so a foreign target is
        /// never touched.
        /// </summary>
        private void RemoveOwnedBootstrap()
        {
            try
            {
                string full = Path.GetFullPath(_bootstrapDirectory);
                string name = Path.GetFileName(full);
                string parent = Path.GetDirectoryName(full);
                string temporary = Path.GetFullPath(Path.GetTempPath());
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        name, "^[0-9a-f]{16}$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return;
                }
                if (!string.Equals(
                        parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        temporary.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                string image = typeof(SlaveAgent).Assembly.Location;
                foreach (string entry in Directory.GetFileSystemEntries(full))
                {
                    if (string.Equals(entry, image, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (string.Equals(Path.GetFileName(entry), "owner.json",
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    DeleteTree(entry);
                }
            }
            catch (Exception)
            {
                // Cleanup of the owned directory is best effort.
            }
        }

        private static void DeleteTree(string root)
        {
            FileAttributes attributes = File.GetAttributes(root);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    NativeMethods.RemoveDirectoryW(root);
                }
                else
                {
                    File.Delete(root);
                }
                return;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string entry in Directory.GetFileSystemEntries(root))
                {
                    DeleteTree(entry);
                }
                Directory.Delete(root, false);
            }
            else
            {
                File.Delete(root);
            }
        }

        private void Dispatch(IDictionary<string, object> frame)
        {
            string kind = frame["type"] as string;
            if (kind == "heartbeat")
            {
                return;
            }
            if (kind == "data")
            {
                Data(frame);
                return;
            }
            if (kind != "request")
            {
                return;
            }
            try
            {
                string channel = frame["channel"] as string;
                if (channel == "control")
                {
                    Control(frame);
                    return;
                }
                if (!_bound)
                {
                    long attempts = Json.GetInt64(frame, "_bind_attempts", 0);
                    if (attempts < BindAttempts)
                    {
                        frame["_bind_attempts"] = attempts + 1;
                        _link.Inbound.TryEnqueue(frame, 1000);
                        Thread.Sleep(BindRetryDelayMs);
                        return;
                    }
                    _link.SendError(frame, "not_bound", "control.bind is mandatory", true);
                    return;
                }
                if (channel == "shell")
                {
                    Shell(frame);
                }
                else if (channel == "fs")
                {
                    Fs(frame);
                }
                else if (channel == "transfer")
                {
                    Transfer(frame);
                }
                else
                {
                    _link.SendError(frame, "unknown_channel", "channel not served", false);
                }
            }
            catch (ProtocolError error)
            {
                SendError(frame, error.Code, error.Message, error.Retryable);
            }
            catch (WireError error)
            {
                SendError(frame, error.Code, error.Detail, false);
            }
            catch (Exception error)
            {
                string message = string.IsNullOrEmpty(error.Message)
                    ? "operation_failed"
                    : error.Message;
                SendError(frame, "operation_failed", message, false);
            }
        }

        private void SendError(IDictionary<string, object> frame, string code,
                               string message, bool retryable)
        {
            try
            {
                _link.SendError(frame, code, message, retryable);
            }
            catch (Exception)
            {
                // The peer may already be gone; the error cannot be delivered.
            }
        }

        private void Control(IDictionary<string, object> frame)
        {
            IDictionary<string, object> body = Body(frame);
            object opValue;
            frame.TryGetValue("op", out opValue);
            string op = opValue as string;
            if (op == "bind")
            {
                object linkValue;
                body.TryGetValue("link_id", out linkValue);
                string requested = linkValue as string;
                if (!string.Equals(requested, _linkId, StringComparison.Ordinal))
                {
                    throw new ProtocolError(
                        "takeover_denied", "link_id does not match", false);
                }
                ProtocolError capabilities = CapabilityGate();
                if (capabilities != null)
                {
                    throw capabilities;
                }
                _bound = true;
                IDictionary<string, object> ready = new Dictionary<string, object>();
                ready["status"] = "ready";
                ready["link_id"] = _linkId;
                _link.Response(frame, ready);
            }
            else if (op == "agent.stop")
            {
                IDictionary<string, object> stopped = new Dictionary<string, object>();
                stopped["status"] = "stopped";
                _link.Response(frame, stopped);
                Thread closer = new Thread(delegate()
                {
                    Thread.Sleep(250);
                    Cleanup(true);
                    _stop.Set();
                });
                closer.IsBackground = true;
                closer.Name = "agent-stop";
                closer.Start();
            }
            else
            {
                throw new ProtocolError(
                    "unknown_op", "unknown control operation", false);
            }
        }

        /// <summary>
        /// A partial platform is reported, never papered over: ready is only
        /// sent when ConPTY exists and kill-on-close jobs are settable.
        /// </summary>
        private ProtocolError CapabilityGate()
        {
            if (!_capabilityChecked)
            {
                _capabilityError = Capabilities.Probe();
                _capabilityChecked = true;
            }
            return _capabilityError;
        }

        private static IDictionary<string, object> Body(IDictionary<string, object> frame)
        {
            object value;
            frame.TryGetValue("body", out value);
            IDictionary<string, object> body = value as IDictionary<string, object>;
            return body ?? new Dictionary<string, object>();
        }

        private static string Op(IDictionary<string, object> frame)
        {
            object value;
            frame.TryGetValue("op", out value);
            return value as string;
        }

        private void Shell(IDictionary<string, object> frame)
        {
            IDictionary<string, object> body = Body(frame);
            string op = Op(frame);
            IDictionary<string, object> result;
            if (op == "job.start")
            {
                result = _jobs.Start(
                    Str(body, "job_id"),
                    StrOrEmpty(body, "cmd"),
                    Json.GetDouble(body, "timeout", 60.0),
                    Str(body, "cwd"));
            }
            else if (op == "job.cancel")
            {
                result = _jobs.Cancel(Str(body, "job_id"));
            }
            else if (op == "terminal.open")
            {
                result = _terminals.Open(
                    Str(body, "cwd"),
                    Str(body, "shell"),
                    Json.GetInt32(body, "rows", 1, 1000, 40),
                    Json.GetInt32(body, "cols", 1, 1000, 120),
                    Json.GetInt32(body, "idle_timeout", 1, 3600, 900));
            }
            else if (op == "terminal.resize")
            {
                result = _terminals.Resize(
                    Str(body, "session_id"),
                    Json.GetInt32(body, "rows", 1, 1000, 40),
                    Json.GetInt32(body, "cols", 1, 1000, 120));
            }
            else if (op == "terminal.close")
            {
                result = _terminals.Close(Str(body, "session_id"));
            }
            else
            {
                throw new ProtocolError("unknown_shell_op", "unknown shell operation", false);
            }
            _link.Response(frame, result);
        }

        private static string Str(IDictionary<string, object> body, string key)
        {
            object value;
            body.TryGetValue(key, out value);
            return value as string;
        }

        private static string StrOrEmpty(IDictionary<string, object> body, string key)
        {
            string value = Str(body, key);
            return value == null ? string.Empty : value;
        }

        private void Fs(IDictionary<string, object> frame)
        {
            IDictionary<string, object> body = Body(frame);
            string op = Op(frame);
            FileService.Result response = _files.Dispatch(op, body);
            string streamId = Str(body, "stream_id");
            if (string.IsNullOrEmpty(streamId))
            {
                streamId = Guid.NewGuid().ToString("N");
            }
            if (response.Payload != null)
            {
                response.Metadata["stream_id"] = streamId;
                response.Metadata["payload_kind"] = "bytes";
                _link.Response(frame, response.Metadata);
                if (op == "screen")
                {
                    // A screenshot is hundreds of KiB: it uses the windowed
                    // sender the transfer channel already proved on this link
                    // instead of stop-and-wait, which measured ~3.5 KiB/s.
                    StartWindowedStream(
                        "fs", streamId, response.Payload, "fs.stream.finished",
                        ScreenWindow);
                }
                else
                {
                    StartStream("fs", streamId, response.Payload, "fs.stream.finished");
                }
                return;
            }
            string json = Json.Serialize(response.Metadata);
            byte[] encoded = new UTF8Encoding(false).GetBytes(json);
            if (encoded.Length > 2048)
            {
                IDictionary<string, object> metadata = new Dictionary<string, object>();
                metadata["stream_id"] = streamId;
                metadata["payload_kind"] = "json";
                metadata["bytes"] = encoded.Length;
                _link.Response(frame, metadata);
                StartStream("fs", streamId, encoded, "fs.stream.finished");
            }
            else
            {
                _link.Response(frame, response.Metadata);
            }
        }

        /// <summary>
        /// Sends a payload with the sliding window of the transfer channel
        /// (per-block ACK, bounded resends) instead of the stop-and-wait used
        /// by the interactive fs streams. Measured on the rig: ~30-45 KiB/s
        /// windowed against ~3.5 KiB/s stop-and-wait, the difference between a
        /// screenshot that arrives inside the master deadline and one that
        /// cannot.
        /// </summary>
        private void StartWindowedStream(string channel, string streamId, byte[] payload,
                                         string finalOp, int window)
        {
            Thread thread = new Thread(delegate()
            {
                try
                {
                    int position = 0;
                    Func<byte[]> readChunk = delegate()
                    {
                        if (position >= payload.Length)
                        {
                            return new byte[0];
                        }
                        int length = Math.Min(
                            WireConstants.DataChunkBytes, payload.Length - position);
                        byte[] chunk = new byte[length];
                        Array.Copy(payload, position, chunk, 0, length);
                        position += length;
                        return chunk;
                    };
                    int sent = _link.StreamData(channel, streamId, readChunk, window, null, null);
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["status"] = "finished";
                    body["bytes"] = sent;
                    body["window"] = window;
                    _link.Event(channel, finalOp, streamId, body);
                }
                catch (Exception error)
                {
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["status"] = "error";
                    body["code"] = "stream_failed";
                    body["message"] = error.Message;
                    body["retryable"] = true;
                    try
                    {
                        _link.Event(channel, finalOp, streamId, body);
                    }
                    catch (Exception)
                    {
                        // The link may already be closing.
                    }
                }
            });
            thread.IsBackground = true;
            string shortId = streamId.Length <= 8 ? streamId : streamId.Substring(0, 8);
            thread.Name = "fs-window-" + shortId;
            thread.Start();
        }

        private void StartStream(string channel, string streamId, byte[] payload,
                                 string finalOp)
        {
            Thread thread = new Thread(delegate()
            {
                try
                {
                    long sequence = 0;
                    for (int position = 0; position < payload.Length;
                         position += WireConstants.DataChunkBytes)
                    {
                        int length = Math.Min(
                            WireConstants.DataChunkBytes, payload.Length - position);
                        byte[] chunk = new byte[length];
                        Array.Copy(payload, position, chunk, 0, length);
                        _link.SendData(channel, streamId, sequence, chunk, null);
                        sequence++;
                    }
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["status"] = "finished";
                    body["bytes"] = payload.Length;
                    _link.Event(channel, finalOp, streamId, body);
                }
                catch (Exception error)
                {
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["status"] = "error";
                    body["code"] = "stream_failed";
                    body["message"] = error.Message;
                    body["retryable"] = true;
                    try
                    {
                        _link.Event(channel, finalOp, streamId, body);
                    }
                    catch (Exception)
                    {
                        // The link may already be closing.
                    }
                }
            });
            thread.IsBackground = true;
            string shortId = streamId.Length <= 8 ? streamId : streamId.Substring(0, 8);
            thread.Name = "fs-stream-" + shortId;
            thread.Start();
        }

        private void Transfer(IDictionary<string, object> frame)
        {
            IDictionary<string, object> body = Body(frame);
            string op = Op(frame);
            string transferId = Str(body, "transfer_id");
            IDictionary<string, object> result;
            if (op == "upload.begin")
            {
                result = _transfers.BeginUpload(
                    transferId,
                    Str(body, "path"),
                    Json.GetInt64(body, "size", 0),
                    Str(body, "sha256"));
            }
            else if (op == "upload.commit")
            {
                result = _transfers.CommitUpload(transferId);
            }
            else if (op == "download.begin")
            {
                result = _transfers.BeginDownload(
                    transferId,
                    Str(body, "path"),
                    Str(body, "checksum"),
                    WindowOrDefault(body));
            }
            else if (op == "status")
            {
                result = _transfers.Status(transferId);
            }
            else if (op == "cancel")
            {
                result = _transfers.Cancel(transferId);
            }
            else
            {
                throw new ProtocolError(
                    "unknown_transfer_op", "unknown transfer operation", false);
            }
            _link.Response(frame, result);
        }

        private static int WindowOrDefault(IDictionary<string, object> body)
        {
            object value;
            if (!body.TryGetValue("window", out value) || value == null || value is bool)
            {
                return WireConstants.DataWindowDefault;
            }
            long parsed;
            if (!Json.TryGetInt64(value, out parsed) ||
                parsed < 1 || parsed > WireConstants.DataWindowMax)
            {
                return WireConstants.DataWindowDefault;
            }
            return (int)parsed;
        }

        /// <summary>
        /// Data frames apply exactly once. The dedup key is recorded only
        /// after the frame is accepted, so a rejected gap is never
        /// fake-acknowledged on retransmission.
        /// </summary>
        private void Data(IDictionary<string, object> frame)
        {
            if (!_bound)
            {
                SendError(frame, "not_bound", "control.bind is mandatory", true);
                return;
            }
            string sessionId = Str(frame, "session_id");
            long sequence = Json.GetInt64(frame, "seq", 0);
            DedupKey key = new DedupKey(_linkId, sessionId, sequence);
            if (_dedup.Seen(key))
            {
                _link.Ack(frame["channel"] as string, sessionId, sequence);
                return;
            }
            byte[] data;
            try
            {
                data = Wire.DecodeData(frame);
                string channel = frame["channel"] as string;
                if (channel == "shell")
                {
                    _terminals.Input(sessionId, data);
                }
                else if (channel == "transfer")
                {
                    _transfers.UploadData(sessionId, sequence, data);
                }
                else
                {
                    throw new ProtocolError(
                        "data_channel_denied", "data_channel_denied", false);
                }
            }
            catch (ProtocolError error)
            {
                SendError(frame, error.Code, error.Message, false);
                return;
            }
            catch (WireError error)
            {
                SendError(frame, error.Code, error.Detail, false);
                return;
            }
            _dedup.Add(key);
            _link.Ack(frame["channel"] as string, sessionId, sequence);
        }
    }
}
