// One-shot PowerShell jobs and ConPTY terminals of the Windows slave.
// Port of implementations/python/shell_service.py with the Windows process
// model: every child is created suspended, assigned to a job object with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, and only then resumed. Cancellation
// and timeouts use TerminateJobObject; POSIX signals are neither available
// nor simulated. A job child is the agent executable itself (--job): no
// powershell.exe process is spawned for jobs, so no classic PowerShell log
// channel records a job start.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// Error carrying the protocol code and retryability. The dispatch mapper
    /// turns it into {code, message, retryable}.
    /// </summary>
    internal sealed class ProtocolError : Exception
    {
        public ProtocolError(string code, string message, bool retryable)
            : base(message)
        {
            Code = code;
            Retryable = retryable;
        }

        public string Code { get; private set; }
        public bool Retryable { get; private set; }
    }

    /// <summary>Shared capacity-4 slots for jobs and terminals.</summary>
    internal sealed class ProcessRegistry
    {
        private readonly int _maximum;
        private readonly object _gate = new object();
        private int _count;

        public ProcessRegistry(int maximum)
        {
            _maximum = maximum;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        public bool Acquire()
        {
            lock (_gate)
            {
                if (_count >= _maximum)
                {
                    return false;
                }
                _count++;
                return true;
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _count = Math.Max(0, _count - 1);
            }
        }
    }

    /// <summary>
    /// Path of the system Windows PowerShell. It is only the default shell
    /// of ConPTY terminals (operator interaction); jobs never run it, and the
    /// job child is the agent executable itself with --job.
    /// </summary>
    internal static class PowerShell
    {
        public static string ExecutablePath()
        {
            string root = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(root))
            {
                root = @"C:\Windows";
            }
            return System.IO.Path.Combine(
                root, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        }
    }

    /// <summary>One detached PowerShell job with separated stdout/stderr.</summary>
    internal sealed class Job
    {
        public string JobId;
        public long StartedTicks;
        public double TimeoutSeconds;
        public string Status = "running";
        public int ReturnCode;
        public bool TimedOut;
        public bool Cancelled;
        public long Sequence;
        public readonly ManualResetEventSlim Finished = new ManualResetEventSlim(false);
        public readonly object SequenceLock = new object();
        public readonly object ContainerLock = new object();
        public ProcessJob Container;
        public IntPtr ProcessHandle;
        public IntPtr StdoutHandle;
        public IntPtr StderrHandle;
        public Thread StdoutReader;
        public Thread StderrReader;
        public Thread Waiter;

        public long NextSequence()
        {
            lock (SequenceLock)
            {
                long value = Sequence;
                Sequence++;
                return value;
            }
        }

        public ProcessJob CurrentContainer()
        {
            lock (ContainerLock)
            {
                return Container;
            }
        }
    }

    /// <summary>Jobs: start, cancel and shutdown with job-object containment.</summary>
    internal sealed class JobManager
    {
        private readonly AgentLink _link;
        private readonly ProcessRegistry _registry;
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();
        private readonly object _gate = new object();

        // Test-only containment fault injection: when set, the launcher
        // refuses to assign the suspended child and reports
        // process_containment_failed exactly as a real failure would.
        internal static bool SimulateContainmentFailure = false;

        public JobManager(AgentLink link, ProcessRegistry registry)
        {
            _link = link;
            _registry = registry;
        }

        public IDictionary<string, object> Start(string jobId, string command,
                                                 double timeoutSeconds, string cwd)
        {
            if (!_registry.Acquire())
            {
                throw new ProtocolError("session_limit", "session_limit", false);
            }
            double timeout = Math.Min(Math.Max(timeoutSeconds, 1.0), 3600.0);
            if (string.IsNullOrEmpty(jobId))
            {
                jobId = Guid.NewGuid().ToString("N");
            }
            Job job = new Job();
            job.JobId = jobId;
            job.TimeoutSeconds = timeout;
            try
            {
                Launch(job, command, cwd);
            }
            catch
            {
                _registry.Release();
                throw;
            }
            lock (_gate)
            {
                _jobs[jobId] = job;
            }
            job.StdoutReader = new Thread(delegate() { Drain(job, "stdout", job.StdoutHandle); });
            job.StdoutReader.IsBackground = true;
            job.StdoutReader.Name = "job-" + ShortId(jobId) + "-stdout";
            job.StderrReader = new Thread(delegate() { Drain(job, "stderr", job.StderrHandle); });
            job.StderrReader.IsBackground = true;
            job.StderrReader.Name = "job-" + ShortId(jobId) + "-stderr";
            job.Waiter = new Thread(delegate() { Wait(job); });
            job.Waiter.IsBackground = true;
            job.Waiter.Name = "job-" + ShortId(jobId) + "-wait";
            job.StdoutReader.Start();
            job.StderrReader.Start();
            job.Waiter.Start();
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "running";
            result["job_id"] = jobId;
            return result;
        }

        private static string ShortId(string value)
        {
            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private void Launch(Job job, string command, string cwd)
        {
            // The user script is delivered over stdin to the in-process
            // Runspace runner (JobEntry): it never appears in the child
            // command line, so no classic PowerShell log channel records
            // its text. UTF-8 output encoding is forced by JobEntry.
            string script =
                "[Console]::InputEncoding=[System.Text.Encoding]::UTF8;" +
                "$OutputEncoding=[System.Text.Encoding]::UTF8;" + command;
            // The child is the agent executable itself: "<agent.exe>" --job
            // boots JobEntry, which reads the script from stdin. No
            // powershell.exe child and no assembly-loading command line.
            string image = Agent.ExecutableAssembly();
            StringBuilder commandLine = new StringBuilder();
            commandLine.Append('"').Append(image).Append("\" --job");

            Win32Structs.SECURITY_ATTRIBUTES inheritable =
                new Win32Structs.SECURITY_ATTRIBUTES();
            inheritable.nLength = (uint)Marshal.SizeOf(typeof(Win32Structs.SECURITY_ATTRIBUTES));
            inheritable.lpSecurityDescriptor = IntPtr.Zero;
            inheritable.bInheritHandle = true;

            IntPtr inheritableBlock = IntPtr.Zero;
            IntPtr stdoutRead = IntPtr.Zero;
            IntPtr stdoutWrite = IntPtr.Zero;
            IntPtr stderrRead = IntPtr.Zero;
            IntPtr stderrWrite = IntPtr.Zero;
            IntPtr stdinHandle = IntPtr.Zero;
            IntPtr stdinRead = IntPtr.Zero;
            Win32Structs.PROCESS_INFORMATION process =
                new Win32Structs.PROCESS_INFORMATION();
            bool processCreated = false;
            bool transferred = false;
            try
            {
                inheritableBlock = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(Win32Structs.SECURITY_ATTRIBUTES)));
                Marshal.StructureToPtr(inheritable, inheritableBlock, false);

                if (!NativeMethods.CreatePipe(out stdoutRead, out stdoutWrite, inheritableBlock, 0))
                {
                    throw LaunchError("CreatePipe(stdout)");
                }
                if (!NativeMethods.CreatePipe(out stderrRead, out stderrWrite, inheritableBlock, 0))
                {
                    throw LaunchError("CreatePipe(stderr)");
                }
                NativeMethods.SetHandleInformation(
                    stdoutRead, Win32Constants.HANDLE_FLAG_INHERIT, 0u);
                NativeMethods.SetHandleInformation(
                    stderrRead, Win32Constants.HANDLE_FLAG_INHERIT, 0u);

                // stdin carries the user script to JobEntry: the child
                // inherits the READ end (its stdin) and the parent keeps the
                // WRITE end to deliver the script and close it for EOF.
                // Marking directions correctly matters: inheriting the write
                // end as stdin makes every read fail with access denied.
                if (!NativeMethods.CreatePipe(out stdinRead, out stdinHandle, inheritableBlock, 0))
                {
                    throw LaunchError("CreatePipe(stdin)");
                }
                NativeMethods.SetHandleInformation(
                    stdinHandle, Win32Constants.HANDLE_FLAG_INHERIT, 0u);

                Win32Structs.STARTUPINFOEX startup = new Win32Structs.STARTUPINFOEX();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(Win32Structs.STARTUPINFOW));
                startup.StartupInfo.dwFlags = Win32Constants.STARTF_USESTDHANDLES;
                startup.StartupInfo.hStdInput = stdinRead;
                startup.StartupInfo.hStdOutput = stdoutWrite;
                startup.StartupInfo.hStdError = stderrWrite;
                startup.lpAttributeList = IntPtr.Zero;

                if (!NativeMethods.CreateProcessW(
                        image,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        Win32Constants.CREATE_SUSPENDED | Win32Constants.CREATE_NO_WINDOW,
                        IntPtr.Zero,
                        string.IsNullOrEmpty(cwd) ? null : cwd,
                        ref startup,
                        out process))
                {
                    throw LaunchError("CreateProcessW");
                }
                processCreated = true;

                // The child inherited its ends; ours must close now or the
                // drains never see EOF.
                NativeMethods.CloseHandle(stdoutWrite);
                stdoutWrite = IntPtr.Zero;
                NativeMethods.CloseHandle(stderrWrite);
                stderrWrite = IntPtr.Zero;


                ProcessJob container = new ProcessJob();
                bool assigned = false;
                try
                {
                    if (SimulateContainmentFailure)
                    {
                        throw new ProtocolError(
                            "process_containment_failed",
                            "the child could not be assigned to its job",
                            false);
                    }
                    container.AssignAndResume(process.hProcess, process.hThread);
                    assigned = true;
                }
                finally
                {
                    if (!assigned)
                    {
                        NativeMethods.TerminateProcess(process.hProcess, 1u);
                        container.Dispose();
                    }
                }

                // The child is running and reading stdin: deliver the
                // script, then close our end so JobEntry sees EOF. A write
                // failure here (child died instantly) surfaces as an
                // operation_failed launch error.
                try
                {
                    byte[] scriptBytes = Encoding.UTF8.GetBytes(script);
                    uint written;
                    if (!NativeMethods.WriteFile(stdinHandle, scriptBytes,
                                                  (uint)scriptBytes.Length,
                                                  out written, IntPtr.Zero)
                        || written != (uint)scriptBytes.Length)
                    {
                        throw LaunchError("WriteFile(stdin)");
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(stdinHandle);
                    stdinHandle = IntPtr.Zero;
                }

                NativeMethods.CloseHandle(process.hThread);
                process.hThread = IntPtr.Zero;

                lock (job.ContainerLock)
                {
                    job.Container = container;
                }
                job.ProcessHandle = process.hProcess;
                process.hProcess = IntPtr.Zero;
                job.StdoutHandle = stdoutRead;
                stdoutRead = IntPtr.Zero;
                job.StderrHandle = stderrRead;
                stderrRead = IntPtr.Zero;
                job.StartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                transferred = true;
            }
            finally
            {
                if (!transferred)
                {
                    if (processCreated)
                    {
                        NativeMethods.CloseHandle(process.hThread);
                        process.hThread = IntPtr.Zero;
                        NativeMethods.CloseHandle(process.hProcess);
                        process.hProcess = IntPtr.Zero;
                    }
                    CloseHandle(ref stdoutRead);
                    CloseHandle(ref stderrRead);
                }
                CloseHandle(ref stdoutWrite);
                CloseHandle(ref stderrWrite);
                CloseHandle(ref stdinHandle);
                CloseHandle(ref stdinRead);
                if (inheritableBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(inheritableBlock);
                }
            }
        }

        private static void CloseHandle(ref IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        private static ProtocolError LaunchError(string operation)
        {
            return new ProtocolError(
                "operation_failed",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} failed with {1}",
                    operation,
                    Marshal.GetLastWin32Error()),
                false);
        }

        private void Drain(Job job, string streamName, IntPtr handle)
        {
            byte[] buffer = new byte[WireConstants.DataChunkBytes];
            try
            {
                while (true)
                {
                    uint read;
                    if (!NativeMethods.ReadFile(handle, buffer, (uint)buffer.Length,
                                                out read, IntPtr.Zero))
                    {
                        return;
                    }
                    if (read == 0)
                    {
                        return;
                    }
                    byte[] chunk = new byte[read];
                    Array.Copy(buffer, chunk, (int)read);
                    IDictionary<string, object> extra = new Dictionary<string, object>();
                    extra["stream"] = streamName;
                    try
                    {
                        _link.SendData("shell", job.JobId, job.NextSequence(), chunk, extra);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
        }

        private void Wait(Job job)
        {
            try
            {
                uint timeoutMs = (uint)Math.Min(job.TimeoutSeconds * 1000.0, int.MaxValue);
                uint wait = NativeMethods.WaitForSingleObject(job.ProcessHandle, timeoutMs);
                if (wait == Win32Constants.WAIT_TIMEOUT)
                {
                    job.TimedOut = true;
                    ProcessJob container = job.CurrentContainer();
                    if (container != null)
                    {
                        container.Terminate(Win32Constants.STATUS_CONTROL_C_EXIT);
                    }
                    NativeMethods.WaitForSingleObject(job.ProcessHandle, 5000u);
                }
                uint exitCode;
                if (NativeMethods.GetExitCodeProcess(job.ProcessHandle, out exitCode))
                {
                    job.ReturnCode = unchecked((int)exitCode);
                }
                if (job.StdoutReader != null)
                {
                    job.StdoutReader.Join(5000);
                }
                if (job.StderrReader != null)
                {
                    job.StderrReader.Join(5000);
                }
                job.Status = job.TimedOut ? "error" : "finished";
                IDictionary<string, object> body = new Dictionary<string, object>();
                body["status"] = job.Status;
                body["returncode"] = job.ReturnCode;
                body["timed_out"] = job.TimedOut;
                body["cancelled"] = job.Cancelled;
                body["elapsed"] = (System.Diagnostics.Stopwatch.GetTimestamp() - job.StartedTicks)
                    / (double)System.Diagnostics.Stopwatch.Frequency;
                try
                {
                    _link.Event("shell", "job.finished", job.JobId, body);
                }
                catch (Exception)
                {
                    // The link may already be closing: the event is best effort.
                }
            }
            finally
            {
                if (job.ProcessHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(job.ProcessHandle);
                    job.ProcessHandle = IntPtr.Zero;
                }
                ProcessJob container;
                lock (job.ContainerLock)
                {
                    container = job.Container;
                    job.Container = null;
                }
                if (container != null)
                {
                    container.Dispose();
                }
                job.Finished.Set();
                _registry.Release();
            }
        }

        public IDictionary<string, object> Cancel(string jobId)
        {
            Job job;
            lock (_gate)
            {
                _jobs.TryGetValue(jobId ?? string.Empty, out job);
            }
            if (job == null)
            {
                IDictionary<string, object> missing = new Dictionary<string, object>();
                missing["status"] = "not_found";
                missing["job_id"] = jobId;
                return missing;
            }
            if (job.Finished.IsSet)
            {
                IDictionary<string, object> finished = new Dictionary<string, object>();
                finished["status"] = job.Status;
                finished["job_id"] = jobId;
                finished["returncode"] = job.ReturnCode;
                return finished;
            }
            job.Cancelled = true;
            ProcessJob container = job.CurrentContainer();
            if (container != null)
            {
                container.Terminate(Win32Constants.STATUS_CONTROL_C_EXIT);
            }
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "cancelled";
            result["job_id"] = jobId;
            return result;
        }

        public void Shutdown()
        {
            List<Job> jobs;
            lock (_gate)
            {
                jobs = new List<Job>(_jobs.Values);
            }
            foreach (Job job in jobs)
            {
                if (!job.Finished.IsSet)
                {
                    job.Cancelled = true;
                    ProcessJob container = job.CurrentContainer();
                    if (container != null)
                    {
                        container.Terminate(Win32Constants.STATUS_CONTROL_C_EXIT);
                    }
                }
            }
        }
    }

    /// <summary>A ConPTY terminal session.</summary>
    internal sealed class Terminal
    {
        public string SessionId;
        public string Status = "running";
        public int ReturnCode;
        public int Rows;
        public int Cols;
        public int IdleTimeoutSeconds;
        public long LastActivityTicks;
        public long Sequence;
        public readonly BoundedQueue<byte[]> Output = new BoundedQueue<byte[]>(512);
        public readonly ManualResetEventSlim Closed = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim TeardownDone = new ManualResetEventSlim(false);
        public readonly object WriteLock = new object();
        public readonly object StateLock = new object();
        public ProcessJob Container;
        public IntPtr PseudoConsole;
        public IntPtr ProcessHandle;
        public IntPtr InputWrite;
        public IntPtr OutputRead;
        public Thread Reader;
        public Thread Sender;
        public Thread Waiter;
        public bool Closing;
        public bool Released;
        public bool TeardownStarted;
        public volatile bool Stopping;
        public volatile bool Draining;

        public ProcessJob CurrentContainer()
        {
            lock (StateLock)
            {
                return Container;
            }
        }

        public void ClearContainer()
        {
            lock (StateLock)
            {
                Container = null;
            }
        }
    }

    /// <summary>ConPTY terminals: open, input, resize, close and idle expiry.</summary>
    internal sealed class TerminalManager
    {
        private readonly AgentLink _link;
        private readonly ProcessRegistry _registry;
        private readonly Dictionary<string, Terminal> _sessions =
            new Dictionary<string, Terminal>();
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);

        public TerminalManager(AgentLink link, ProcessRegistry registry)
        {
            _link = link;
            _registry = registry;
            Thread idle = new Thread(IdleLoop);
            idle.IsBackground = true;
            idle.Name = "pty-idle";
            idle.Start();
        }

        public IDictionary<string, object> Open(string cwd, string shell,
                                                int rows, int cols, int idleTimeout)
        {
            rows = Math.Min(Math.Max(rows, 1), 1000);
            cols = Math.Min(Math.Max(cols, 1), 1000);
            idleTimeout = Math.Min(Math.Max(idleTimeout, 1), 3600);
            if (!_registry.Acquire())
            {
                throw new ProtocolError("session_limit", "session_limit", false);
            }
            string selected;
            if (shell == null)
            {
                selected = PowerShell.ExecutablePath();
            }
            else
            {
                selected = shell;
                if (!System.IO.Path.IsPathRooted(selected) || !File.Exists(selected))
                {
                    _registry.Release();
                    throw new ProtocolError("invalid_argument", "shell not executable", false);
                }
            }
            if (!string.IsNullOrEmpty(cwd) && !Directory.Exists(cwd))
            {
                _registry.Release();
                throw new ProtocolError("invalid_argument", "cwd does not exist", false);
            }
            Terminal terminal;
            try
            {
                terminal = Launch(selected, string.IsNullOrEmpty(cwd) ? null : cwd,
                                  rows, cols, idleTimeout);
            }
            catch
            {
                _registry.Release();
                throw;
            }
            lock (_gate)
            {
                _sessions[terminal.SessionId] = terminal;
            }
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "running";
            result["session_id"] = terminal.SessionId;
            result["rows"] = rows;
            result["cols"] = cols;
            return result;
        }

        private Terminal Launch(string shell, string cwd, int rows, int cols,
                                int idleTimeout)
        {
            IntPtr inputRead = IntPtr.Zero;
            IntPtr inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr pseudoConsole = IntPtr.Zero;
            Win32Structs.PROCESS_INFORMATION process =
                new Win32Structs.PROCESS_INFORMATION();
            ProcessJob container = null;
            bool processCreated = false;
            bool transferred = false;
            StringBuilder commandLine = new StringBuilder();
            commandLine.Append('"').Append(shell).Append('"').Append(" -NoLogo -NoProfile");
            try
            {
                // ConPTY pipes are non-inheritable; the pseudoconsole
                // duplicates what it needs. This is the inverse of the job
                // pipes, which must be inheritable for the child.
                if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
                {
                    throw LaunchError("CreatePipe(input)");
                }
                if (!NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                {
                    throw LaunchError("CreatePipe(output)");
                }
                Win32Structs.COORD size = new Win32Structs.COORD();
                size.X = (short)cols;
                size.Y = (short)rows;
                int hresult = NativeMethods.CreatePseudoConsole(
                    size, inputRead, outputWrite, 0u, out pseudoConsole);
                if (hresult < 0)
                {
                    throw new ProtocolError(
                        "io_error",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CreatePseudoConsole failed with 0x{0:X8}",
                            hresult),
                        false);
                }

                // Documented double call: learn the buffer size, allocate,
                // then initialize for real.
                IntPtr required = IntPtr.Zero;
                NativeMethods.InitializeProcThreadAttributeList(
                    IntPtr.Zero, 1u, 0u, ref required);
                attributeList = Marshal.AllocHGlobal(required);
                if (!NativeMethods.InitializeProcThreadAttributeList(
                        attributeList, 1u, 0u, ref required))
                {
                    throw LaunchError("InitializeProcThreadAttributeList");
                }
                // The Microsoft ConPTY sample passes the HPCON **value**
                // directly as lpValue with cbSize = sizeof(HPCON). Passing a
                // pointer to a variable holding it makes the child receive a
                // pointer-shaped but invalid console handle, which is why the
                // terminal child never acted on its input.
                if (!NativeMethods.UpdateProcThreadAttribute(
                        attributeList,
                        0u,
                        Win32Constants.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        pseudoConsole,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw LaunchError("UpdateProcThreadAttribute");
                }

                Win32Structs.STARTUPINFOEX startup = new Win32Structs.STARTUPINFOEX();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(Win32Structs.STARTUPINFOEX));
                startup.lpAttributeList = attributeList;

                if (!NativeMethods.CreateProcessW(
                        shell,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        Win32Constants.EXTENDED_STARTUPINFO_PRESENT |
                            Win32Constants.CREATE_SUSPENDED,
                        IntPtr.Zero,
                        cwd,
                        ref startup,
                        out process))
                {
                    throw LaunchError("CreateProcessW");
                }
                processCreated = true;

                container = new ProcessJob();
                bool assigned = false;
                try
                {
                    if (JobManager.SimulateContainmentFailure)
                    {
                        throw new ProtocolError(
                            "process_containment_failed",
                            "the terminal child could not be assigned to its job",
                            false);
                    }
                    container.AssignAndResume(process.hProcess, process.hThread);
                    assigned = true;
                }
                finally
                {
                    if (!assigned)
                    {
                        NativeMethods.TerminateProcess(process.hProcess, 1u);
                        container.Dispose();
                        container = null;
                    }
                }
                NativeMethods.CloseHandle(process.hThread);
                process.hThread = IntPtr.Zero;

                // The walkthrough says the handles given to
                // CreatePseudoConsole should be freed from this process so the
                // pipe can report a broken channel.
                CloseHandle(ref inputRead);
                CloseHandle(ref outputWrite);

                Terminal terminal = new Terminal();
                terminal.SessionId = Guid.NewGuid().ToString("N");
                terminal.Rows = rows;
                terminal.Cols = cols;
                terminal.IdleTimeoutSeconds = idleTimeout;
                terminal.LastActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                terminal.Container = container;
                terminal.PseudoConsole = pseudoConsole;
                terminal.ProcessHandle = process.hProcess;
                terminal.InputWrite = inputWrite;
                terminal.OutputRead = outputRead;
                terminal.Reader = new Thread(delegate() { ReadLoop(terminal); });
                terminal.Reader.IsBackground = true;
                terminal.Reader.Name = "pty-" + ShortId(terminal.SessionId) + "-read";
                terminal.Sender = new Thread(delegate() { SendLoop(terminal); });
                terminal.Sender.IsBackground = true;
                terminal.Sender.Name = "pty-" + ShortId(terminal.SessionId) + "-send";
                terminal.Waiter = new Thread(delegate() { WaitLoop(terminal); });
                terminal.Waiter.IsBackground = true;
                terminal.Waiter.Name = "pty-" + ShortId(terminal.SessionId) + "-wait";

                container = null;
                process.hProcess = IntPtr.Zero;
                inputWrite = IntPtr.Zero;
                outputRead = IntPtr.Zero;
                pseudoConsole = IntPtr.Zero;
                transferred = true;
                terminal.Reader.Start();
                terminal.Sender.Start();
                terminal.Waiter.Start();
                return terminal;
            }
            finally
            {
                if (!transferred)
                {
                    if (processCreated)
                    {
                        NativeMethods.CloseHandle(process.hThread);
                        process.hThread = IntPtr.Zero;
                        NativeMethods.CloseHandle(process.hProcess);
                        process.hProcess = IntPtr.Zero;
                    }
                    CloseHandle(ref inputRead);
                    CloseHandle(ref inputWrite);
                    CloseHandle(ref outputRead);
                    CloseHandle(ref outputWrite);
                    if (pseudoConsole != IntPtr.Zero)
                    {
                        NativeMethods.ClosePseudoConsole(pseudoConsole);
                        pseudoConsole = IntPtr.Zero;
                    }
                    if (container != null)
                    {
                        container.Dispose();
                        container = null;
                    }
                }
                if (attributeList != IntPtr.Zero)
                {
                    NativeMethods.DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }
        }

        private static void CloseHandle(ref IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        private static ProtocolError LaunchError(string operation)
        {
            return new ProtocolError(
                "operation_failed",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} failed with {1}",
                    operation,
                    Marshal.GetLastWin32Error()),
                false);
        }

        private static string ShortId(string value)
        {
            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private void ReadLoop(Terminal terminal)
        {
            byte[] buffer = new byte[WireConstants.DataChunkBytes];
            try
            {
                while (!terminal.Stopping)
                {
                    if (terminal.Output.Count >= 512)
                    {
                        if (terminal.Stopping)
                        {
                            return;
                        }
                        if (terminal.Draining)
                        {
                            // Teardown is draining: keep reading (and
                            // discarding) so ClosePseudoConsole never waits on
                            // an unread pipe.
                            uint discarded;
                            if (!NativeMethods.ReadFile(
                                    terminal.OutputRead, buffer, (uint)buffer.Length,
                                    out discarded, IntPtr.Zero) || discarded == 0)
                            {
                                return;
                            }
                            continue;
                        }
                        Thread.Sleep(20);
                        continue;
                    }
                    uint read;
                    if (!NativeMethods.ReadFile(
                            terminal.OutputRead, buffer, (uint)buffer.Length,
                            out read, IntPtr.Zero))
                    {
                        return;
                    }
                    if (read == 0)
                    {
                        return;
                    }
                    byte[] chunk = new byte[read];
                    Array.Copy(buffer, chunk, (int)read);
                    terminal.LastActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    terminal.Output.TryEnqueue(chunk, 1000);
                }
            }
            finally
            {
                terminal.Output.TryEnqueue(null, 5000);
            }
        }

        private void SendLoop(Terminal terminal)
        {
            while (true)
            {
                byte[] data;
                if (!terminal.Output.TryDequeue(100, out data))
                {
                    if (terminal.Closed.IsSet && terminal.Output.Count == 0)
                    {
                        return;
                    }
                    continue;
                }
                if (data == null)
                {
                    return;
                }
                long sequence;
                lock (terminal.StateLock)
                {
                    sequence = terminal.Sequence;
                    terminal.Sequence++;
                }
                try
                {
                    _link.SendData("shell", terminal.SessionId, sequence, data, null);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private void WaitLoop(Terminal terminal)
        {
            try
            {
                NativeMethods.WaitForSingleObject(
                    terminal.ProcessHandle, Win32Constants.INFINITE);
                uint exitCode;
                if (NativeMethods.GetExitCodeProcess(terminal.ProcessHandle, out exitCode))
                {
                    terminal.ReturnCode = unchecked((int)exitCode);
                }
                lock (terminal.StateLock)
                {
                    terminal.Status = "closed";
                }
                terminal.Closed.Set();
                IDictionary<string, object> body = new Dictionary<string, object>();
                body["status"] = "closed";
                body["returncode"] = terminal.ReturnCode;
                try
                {
                    _link.Event("shell", "terminal.closed", terminal.SessionId, body);
                }
                catch (Exception)
                {
                    // Best effort while the link may be closing.
                }
                ReleaseSlot(terminal);
                // The exit path must not wait behind a closer that may be
                // joining this very thread.
                Teardown(terminal, false);
            }
            finally
            {
                ReleaseSlot(terminal);
                if (terminal.ProcessHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(terminal.ProcessHandle);
                    terminal.ProcessHandle = IntPtr.Zero;
                }
            }
        }

        private void ReleaseSlot(Terminal terminal)
        {
            bool release = false;
            lock (terminal.StateLock)
            {
                if (!terminal.Released)
                {
                    terminal.Released = true;
                    release = true;
                }
            }
            if (release)
            {
                _registry.Release();
            }
        }

        private Terminal Get(string sessionId)
        {
            Terminal terminal;
            lock (_gate)
            {
                _sessions.TryGetValue(sessionId ?? string.Empty, out terminal);
            }
            if (terminal == null)
            {
                throw new ProtocolError("terminal_not_found", "terminal not found", false);
            }
            return terminal;
        }

        public IDictionary<string, object> Input(string sessionId, byte[] data)
        {
            Terminal terminal = Get(sessionId);
            bool closed;
            bool closing;
            lock (terminal.StateLock)
            {
                closed = terminal.Status == "closed";
                closing = terminal.Closing;
            }
            if (closed || closing)
            {
                IDictionary<string, object> result = new Dictionary<string, object>();
                result["status"] = "closed";
                result["session_id"] = sessionId;
                return result;
            }
            terminal.LastActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (terminal.WriteLock)
            {
                int total = 0;
                while (total < data.Length)
                {
                    byte[] part = new byte[data.Length - total];
                    Array.Copy(data, total, part, 0, part.Length);
                    uint written;
                    if (!NativeMethods.WriteFile(
                            terminal.InputWrite, part, (uint)part.Length,
                            out written, IntPtr.Zero))
                    {
                        throw new ProtocolError(
                            "io_error", "WriteFile to the terminal failed", false);
                    }
                    if (written == 0)
                    {
                        throw new ProtocolError(
                            "io_error", "short write to the terminal", false);
                    }
                    total += (int)written;
                }
                IDictionary<string, object> result = new Dictionary<string, object>();
                result["status"] = "written";
                result["session_id"] = sessionId;
                result["bytes"] = total;
                return result;
            }
        }

        public IDictionary<string, object> Resize(string sessionId, int rows, int cols)
        {
            Terminal terminal = Get(sessionId);
            rows = Math.Min(Math.Max(rows, 1), 1000);
            cols = Math.Min(Math.Max(cols, 1), 1000);
            Win32Structs.COORD size = new Win32Structs.COORD();
            size.X = (short)cols;
            size.Y = (short)rows;
            int hresult = NativeMethods.ResizePseudoConsole(terminal.PseudoConsole, size);
            if (hresult < 0)
            {
                throw new ProtocolError(
                    "io_error",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "ResizePseudoConsole failed with 0x{0:X8}",
                        hresult),
                    false);
            }
            terminal.Rows = rows;
            terminal.Cols = cols;
            terminal.LastActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            IDictionary<string, object> result = new Dictionary<string, object>();
            lock (terminal.StateLock)
            {
                result["status"] = terminal.Status;
            }
            result["session_id"] = sessionId;
            result["rows"] = rows;
            result["cols"] = cols;
            return result;
        }

        /// <summary>
        /// Closes a terminal with the ordering that matters on Windows:
        /// reject new writes, terminate the job tree, keep draining output,
        /// and only then close the pseudoconsole from this thread (never the
        /// reader), free the pipes and join the drains.
        /// </summary>
        public IDictionary<string, object> Close(string sessionId)
        {
            Terminal terminal;
            lock (_gate)
            {
                _sessions.TryGetValue(sessionId ?? string.Empty, out terminal);
            }
            if (terminal == null)
            {
                IDictionary<string, object> missing = new Dictionary<string, object>();
                missing["status"] = "closed";
                missing["session_id"] = sessionId;
                return missing;
            }
            lock (terminal.StateLock)
            {
                if (terminal.Status == "closed")
                {
                    IDictionary<string, object> closed = new Dictionary<string, object>();
                    closed["status"] = "closed";
                    closed["session_id"] = sessionId;
                    closed["returncode"] = terminal.ReturnCode;
                    return closed;
                }
                if (terminal.Closing)
                {
                    IDictionary<string, object> closing = new Dictionary<string, object>();
                    closing["status"] = "closed";
                    closing["session_id"] = sessionId;
                    return closing;
                }
                terminal.Closing = true;
                terminal.Status = "closing";
            }
            ProcessJob container = terminal.CurrentContainer();
            if (container != null)
            {
                container.Terminate(Win32Constants.STATUS_CONTROL_C_EXIT);
            }
            // Python's close() returns immediately: the drain and the
            // pseudoconsole teardown continue in the background. Doing that
            // here keeps the dispatch loop responsive with a full 256 KiB
            // output backlog, while the wait thread still performs the
            // ordered teardown off the reading thread.
            Thread closer = new Thread(delegate() { Teardown(terminal, true); });
            closer.IsBackground = true;
            closer.Name = "pty-close-" + ShortId(sessionId);
            closer.Start();
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "closed";
            result["session_id"] = sessionId;
            return result;
        }

        /// <summary>
        /// Exactly-once teardown; every caller waits for the owner to finish.
        /// Ordering that matters: the reader keeps draining while
        /// ClosePseudoConsole waits (it must not run on the reading thread and
        /// must not run after the reader was stopped), then the read end is
        /// closed to unblock a pending ReadFile, and finally the drains are
        /// joined.
        /// </summary>
        private void Teardown(Terminal terminal, bool waitForOwner)
        {
            bool owner = false;
            lock (terminal.StateLock)
            {
                if (!terminal.TeardownStarted)
                {
                    terminal.TeardownStarted = true;
                    owner = true;
                }
            }
            if (!owner)
            {
                if (waitForOwner)
                {
                    terminal.TeardownDone.Wait(15000);
                }
                return;
            }
            try
            {
                // 0. Once teardown starts the reader must never block on a
                //    full output queue: it discards until the pipe breaks,
                //    because ClosePseudoConsole waits for the drain.
                terminal.Draining = true;
                // 1. Close the pseudoconsole while the reader still drains.
                if (terminal.PseudoConsole != IntPtr.Zero)
                {
                    NativeMethods.ClosePseudoConsole(terminal.PseudoConsole);
                    terminal.PseudoConsole = IntPtr.Zero;
                }
                // 2. Stop the reader after ClosePseudoConsole has broken the
                //    pipe; joining before closing the handle avoids closing a
                //    handle another thread is inside ReadFile with.
                terminal.Stopping = true;
                if (terminal.Reader != null && terminal.Reader != Thread.CurrentThread)
                {
                    terminal.Reader.Join(10000);
                }
                if (terminal.OutputRead != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(terminal.OutputRead);
                    terminal.OutputRead = IntPtr.Zero;
                }
                if (terminal.InputWrite != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(terminal.InputWrite);
                    terminal.InputWrite = IntPtr.Zero;
                }
                ProcessJob container = terminal.CurrentContainer();
                terminal.ClearContainer();
                if (container != null)
                {
                    container.Dispose();
                }
                if (terminal.Sender != null && terminal.Sender != Thread.CurrentThread)
                {
                    terminal.Sender.Join(2000);
                }
                if (terminal.Waiter != null && terminal.Waiter != Thread.CurrentThread)
                {
                    terminal.Waiter.Join(5000);
                }
            }
            finally
            {
                terminal.TeardownDone.Set();
            }
        }

        private void IdleLoop()
        {
            while (!_stop.Wait(1000))
            {
                List<Terminal> snapshot;
                lock (_gate)
                {
                    snapshot = new List<Terminal>(_sessions.Values);
                }
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (Terminal terminal in snapshot)
                {
                    if (terminal.Closed.IsSet)
                    {
                        continue;
                    }
                    double idle = (now - terminal.LastActivityTicks)
                        / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (idle > terminal.IdleTimeoutSeconds)
                    {
                        try
                        {
                            Close(terminal.SessionId);
                        }
                        catch (Exception)
                        {
                            // An idle close is best effort.
                        }
                    }
                }
            }
        }

        public void Shutdown()
        {
            _stop.Set();
            List<string> sessions;
            lock (_gate)
            {
                sessions = new List<string>(_sessions.Keys);
            }
            foreach (string sessionId in sessions)
            {
                try
                {
                    Close(sessionId);
                }
                catch (Exception)
                {
                    // Shutdown closes every session it can; a dead one is fine.
                }
            }
        }
    }
}
