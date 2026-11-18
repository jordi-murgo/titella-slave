"""One-shot PowerShell jobs and ConPTY terminals of the Windows slave agent.

Port of shell_service.py to the Windows process model: every child is
created suspended, assigned to a job object configured with
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, and only then resumed, so a job's or
a terminal's whole descendant tree dies with one TerminateJobObject call
and nothing outlives its registry slot. POSIX signals are neither
available nor simulated: timeout, cancel and close all terminate the
tree through the job object. No Win32 call happens at import time: the
module imports cleanly on any host and only raises when used.
"""
from __future__ import print_function

import ctypes
import os
import queue
import threading
import time
import uuid

# ---------------------------------------------------------------------------
# Win32 declarations. Everything in this section is a pure declaration: no
# library is loaded until _win32() runs, so importing this module never
# touches the OS API (and stays valid on non-Windows hosts). The fixed-width
# aliases keep the structure layouts identical on every platform, so the
# sizes asserted by the tests match the x64 Windows ABI.

_DWORD = ctypes.c_uint32
_BOOL = ctypes.c_int32
_WORD = ctypes.c_ushort
_HANDLE = ctypes.c_void_p


class COORD(ctypes.Structure):
    _fields_ = [("X", ctypes.c_short), ("Y", ctypes.c_short)]


class SECURITY_ATTRIBUTES(ctypes.Structure):
    _fields_ = [
        ("nLength", _DWORD),
        ("lpSecurityDescriptor", ctypes.c_void_p),
        ("bInheritHandle", _BOOL),
    ]


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", _DWORD),
        ("lpReserved", ctypes.c_wchar_p),
        ("lpDesktop", ctypes.c_wchar_p),
        ("lpTitle", ctypes.c_wchar_p),
        ("dwX", _DWORD),
        ("dwY", _DWORD),
        ("dwXSize", _DWORD),
        ("dwYSize", _DWORD),
        ("dwXCountChars", _DWORD),
        ("dwYCountChars", _DWORD),
        ("dwFillAttribute", _DWORD),
        ("dwFlags", _DWORD),
        ("wShowWindow", _WORD),
        ("cbReserved2", _WORD),
        ("lpReserved2", ctypes.c_void_p),
        ("hStdInput", _HANDLE),
        ("hStdOutput", _HANDLE),
        ("hStdError", _HANDLE),
    ]


class STARTUPINFOEXW(ctypes.Structure):
    _fields_ = [
        ("StartupInfo", STARTUPINFOW),
        ("lpAttributeList", ctypes.c_void_p),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("hProcess", _HANDLE),
        ("hThread", _HANDLE),
        ("dwProcessId", _DWORD),
        ("dwThreadId", _DWORD),
    ]


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_ulonglong),
        ("WriteOperationCount", ctypes.c_ulonglong),
        ("OtherOperationCount", ctypes.c_ulonglong),
        ("ReadTransferCount", ctypes.c_ulonglong),
        ("WriteTransferCount", ctypes.c_ulonglong),
        ("OtherTransferCount", ctypes.c_ulonglong),
    ]


class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_longlong),
        ("PerJobUserTimeLimit", ctypes.c_longlong),
        ("LimitFlags", _DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", _DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", _DWORD),
        ("SchedulingClass", _DWORD),
    ]


class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
        ("IoInfo", IO_COUNTERS),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


_JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9
_JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
# Exit code given to a terminated tree: the same NTSTATUS a process killed
# by a console Ctrl+C reports, so a cancelled job looks like an interrupted
# one instead of an arbitrary code.
_STATUS_CONTROL_C_EXIT = 0xC000013A
_CREATE_SUSPENDED = 0x00000004
_CREATE_NO_WINDOW = 0x08000000
_EXTENDED_STARTUPINFO_PRESENT = 0x00080000
_STARTF_USESTDHANDLES = 0x00000100
_PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016
_HANDLE_FLAG_INHERIT = 0x00000001
_WAIT_TIMEOUT = 258
_INFINITE = 0xFFFFFFFF

# One streamed chunk per wire frame: this mirrors DATA_CHUNK_BYTES of the
# wire protocol without importing it (the module must stand alone).
_CHUNK_BYTES = 512

# Forces UTF-8 on the pipes of a job: PowerShell 5.1 otherwise encodes its
# formatted output with the legacy console code page. InputEncoding is
# deliberately left alone: a job child gets an already-EOF stdin pipe (the
# Windows equivalent of the POSIX DEVNULL stdin) and .NET refuses to set
# InputEncoding on a redirected handle.
_JOB_PROLOGUE = (
    "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;"
    "$OutputEncoding=[System.Text.Encoding]::UTF8;"
)


def user_shell():
    """Path of the system Windows PowerShell 5.1.

    It is the default shell of jobs and ConPTY terminals; an operator may
    always pass an explicit ``shell`` to the terminal instead.
    """
    root = os.environ.get("SystemRoot") or "C:\\Windows"
    return os.path.join(
        root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"
    )


def _quote_argument(value):
    """Quote one CreateProcessW argument with the MSVCRT rules.

    Whatever the child uses to split its command line then sees exactly one
    argument: embedded quotes become ``\\"`` and backslashes before them
    double, as every Windows runtime expects.
    """
    pieces = ['"']
    backslashes = 0
    for character in value:
        if character == "\\":
            backslashes += 1
            continue
        if character == '"':
            pieces.append("\\" * (backslashes * 2 + 1))
            pieces.append('"')
        else:
            pieces.append("\\" * backslashes)
            pieces.append(character)
        backslashes = 0
    pieces.append("\\" * (backslashes * 2))
    pieces.append('"')
    return "".join(pieces)


def _win32_error(operation):
    """RuntimeError carrying the operation and the last Win32 error."""
    return RuntimeError(
        "operation_failed: %s failed with %d" % (operation, ctypes.get_last_error())
    )


def _to_signed_exit(value):
    """Mirror the C# unchecked int cast of GetExitCodeProcess.

    A terminated tree reports the negative form of its NTSTATUS, the same
    shape POSIX reports for a killed process (-signal).
    """
    value = int(value)
    if value >= 0x80000000:
        return value - 0x100000000
    return value


class _Win32Api(object):
    """Namespace of the kernel32 entry points bound by _load_win32()."""


_WIN32 = None
_WIN32_LOCK = threading.Lock()


def _load_win32():
    loader = getattr(ctypes, "WinDLL", None)
    if loader is None:
        raise RuntimeError("win32_unavailable: this host has no Win32 API")
    kernel32 = loader("kernel32", use_last_error=True)
    api = _Win32Api()

    def bind(name, restype, argtypes, required=True):
        try:
            proc = getattr(kernel32, name)
        except AttributeError:
            if required:
                raise RuntimeError("win32_unavailable: kernel32 lacks %s" % name)
            setattr(api, name, None)
            return
        proc.restype = restype
        proc.argtypes = argtypes
        setattr(api, name, proc)

    bind("CloseHandle", _BOOL, [_HANDLE])
    bind(
        "CreatePipe",
        _BOOL,
        [
            ctypes.POINTER(_HANDLE),
            ctypes.POINTER(_HANDLE),
            ctypes.POINTER(SECURITY_ATTRIBUTES),
            _DWORD,
        ],
    )
    bind("SetHandleInformation", _BOOL, [_HANDLE, _DWORD, _DWORD])
    bind(
        "CreateProcessW",
        _BOOL,
        [
            ctypes.c_wchar_p,
            ctypes.c_wchar_p,
            ctypes.c_void_p,
            ctypes.c_void_p,
            _BOOL,
            _DWORD,
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.c_void_p,
            ctypes.c_void_p,
        ],
    )
    bind("CreateJobObjectW", _HANDLE, [ctypes.c_void_p, ctypes.c_wchar_p])
    bind("SetInformationJobObject", _BOOL, [_HANDLE, ctypes.c_int, ctypes.c_void_p, _DWORD])
    bind("AssignProcessToJobObject", _BOOL, [_HANDLE, _HANDLE])
    bind("TerminateJobObject", _BOOL, [_HANDLE, _DWORD])
    bind("TerminateProcess", _BOOL, [_HANDLE, _DWORD])
    bind("ResumeThread", _DWORD, [_HANDLE])
    bind("WaitForSingleObject", _DWORD, [_HANDLE, _DWORD])
    bind("GetExitCodeProcess", _BOOL, [_HANDLE, ctypes.POINTER(_DWORD)])
    bind(
        "ReadFile",
        _BOOL,
        [_HANDLE, ctypes.c_void_p, _DWORD, ctypes.POINTER(_DWORD), ctypes.c_void_p],
    )
    bind(
        "WriteFile",
        _BOOL,
        [_HANDLE, ctypes.c_void_p, _DWORD, ctypes.POINTER(_DWORD), ctypes.c_void_p],
    )
    bind(
        "InitializeProcThreadAttributeList",
        _BOOL,
        [ctypes.c_void_p, _DWORD, _DWORD, ctypes.POINTER(ctypes.c_size_t)],
    )
    bind(
        "UpdateProcThreadAttribute",
        _BOOL,
        [
            ctypes.c_void_p,
            _DWORD,
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_size_t,
            ctypes.c_void_p,
            ctypes.c_void_p,
        ],
    )
    bind("DeleteProcThreadAttributeList", None, [ctypes.c_void_p])
    # ConPTY: optional exports, absent before Windows 10 1809.
    bind(
        "CreatePseudoConsole",
        ctypes.c_int32,
        [
            COORD,
            _HANDLE,
            _HANDLE,
            _DWORD,
            ctypes.POINTER(_HANDLE),
        ],
        required=False,
    )
    bind(
        "ResizePseudoConsole",
        ctypes.c_int32,
        [_HANDLE, COORD],
        required=False,
    )
    bind("ClosePseudoConsole", None, [_HANDLE], required=False)
    return api


def _win32():
    """The bound kernel32 API, prototyped on first use."""
    global _WIN32
    with _WIN32_LOCK:
        if _WIN32 is None:
            _WIN32 = _load_win32()
        return _WIN32


def _conpty_api():
    """The API when the OS has pseudoconsole support.

    A platform without ConPTY is reported with
    RuntimeError("terminal_unsupported"), never papered over: the module
    refuses to fake a terminal on an OS that cannot serve one.
    """
    try:
        api = _win32()
    except RuntimeError:
        raise RuntimeError("terminal_unsupported") from None
    if (
        getattr(api, "CreatePseudoConsole", None) is None
        or api.ClosePseudoConsole is None
        or api.ResizePseudoConsole is None
    ):
        raise RuntimeError("terminal_unsupported")
    return api


def _create_pipe(api, attributes):
    """Anonymous pipe; ``attributes`` with bInheritHandle when the child
    must inherit one end (None leaves both ends non-inheritable)."""
    read = _HANDLE()
    write = _HANDLE()
    security = ctypes.byref(attributes) if attributes is not None else None
    if not api.CreatePipe(ctypes.byref(read), ctypes.byref(write), security, 0):
        raise _win32_error("CreatePipe")
    return read.value, write.value


def _close_handle(api, handle):
    """Close a Win32 handle; double closes and failures are ignored."""
    if handle:
        try:
            api.CloseHandle(handle)
        except Exception:
            pass


class _ProcessJob(object):
    """A Windows job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.

    Tree-kill mechanism: the child is created suspended and assigned to the
    job object before its first instruction runs, so containment is
    guaranteed. Any process that child later spawns joins the job
    automatically (job membership is inherited; nothing here grants
    CREATE_BREAKAWAY_FROM_JOB), so TerminateJobObject kills the whole
    descendant tree in one kernel call. Closing the handle is the backstop:
    with KILL_ON_JOB_CLOSE the kernel terminates whatever is still assigned
    to the job when the last handle goes away, so a killed job can never
    leak an orphan behind its registry slot.
    """

    def __init__(self):
        api = _win32()
        self._lock = threading.Lock()
        handle = api.CreateJobObjectW(None, None)
        if not handle:
            raise RuntimeError(
                "process_containment_failed: CreateJobObjectW failed with %d"
                % ctypes.get_last_error()
            )
        info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        info.BasicLimitInformation.LimitFlags = _JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not api.SetInformationJobObject(
            handle,
            _JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
            ctypes.byref(info),
            ctypes.sizeof(info),
        ):
            api.CloseHandle(handle)
            raise RuntimeError(
                "process_containment_failed: SetInformationJobObject failed with %d"
                % ctypes.get_last_error()
            )
        self._handle = handle

    def assign_and_resume(self, process_handle, thread_handle):
        """Assign a suspended child to the job, then resume it.

        The mandated order: the child never executes user code unless
        containment is proven. On failure the child is terminated here and
        the error surfaces; there is no PID-only fallback.
        """
        api = _win32()
        if not api.AssignProcessToJobObject(self._handle, process_handle):
            error = ctypes.get_last_error()
            api.TerminateProcess(process_handle, 1)
            raise RuntimeError(
                "process_containment_failed: AssignProcessToJobObject failed with %d"
                % error
            )
        if api.ResumeThread(thread_handle) == 0xFFFFFFFF:
            error = ctypes.get_last_error()
            api.TerminateProcess(process_handle, 1)
            raise RuntimeError(
                "process_containment_failed: ResumeThread failed with %d" % error
            )

    def terminate(self, exit_code):
        """Kill every process in the tree; the handle stays open."""
        api = _win32()
        with self._lock:
            if self._handle:
                api.TerminateJobObject(self._handle, exit_code)

    def close(self):
        """Drop the handle (idempotent); any straggler dies with it."""
        api = _win32()
        with self._lock:
            handle = self._handle
            self._handle = None
        if handle:
            try:
                api.CloseHandle(handle)
            except Exception:
                pass


class ProcessRegistry(object):
    def __init__(self, maximum=4):
        self.maximum = maximum
        self.count = 0
        self.lock = threading.Lock()

    def acquire(self):
        with self.lock:
            if self.count >= self.maximum:
                return False
            self.count += 1
            return True

    def release(self):
        with self.lock:
            self.count = max(0, self.count - 1)


class Job(object):
    def __init__(self, job_id, timeout):
        self.job_id = job_id
        self.timeout = timeout
        self.started = time.monotonic()
        self.status = "running"
        self.returncode = None
        self.timed_out = False
        self.cancelled = False
        self.sequence = 0
        self.sequence_lock = threading.Lock()
        self.finished = threading.Event()
        self.readers = []
        self.container = None
        self.process_handle = None
        self.stdout_handle = None
        self.stderr_handle = None

    def next_sequence(self):
        with self.sequence_lock:
            value = self.sequence
            self.sequence += 1
            return value


class JobManager(object):
    def __init__(self, link, registry):
        self.link = link
        self.registry = registry
        self.jobs = {}
        self.lock = threading.Lock()

    def start(self, job_id, command, timeout=60, cwd=None):
        if not self.registry.acquire():
            raise RuntimeError("session_limit")
        timeout = min(max(float(timeout), 1), 3600)
        if not job_id:
            job_id = uuid.uuid4().hex
        job = Job(job_id, timeout)
        try:
            self._launch(job, command, cwd)
        except BaseException:
            self.registry.release()
            raise
        with self.lock:
            self.jobs[job_id] = job
        for stream_name, handle in (
            ("stdout", job.stdout_handle),
            ("stderr", job.stderr_handle),
        ):
            thread = threading.Thread(
                target=self._read_stream,
                args=(job, stream_name, handle),
                name="job-%s-%s" % (job_id[:8], stream_name),
                daemon=True,
            )
            job.readers.append(thread)
            thread.start()
        threading.Thread(
            target=self._wait, args=(job,), name="job-%s-wait" % job_id[:8], daemon=True
        ).start()
        return {"status": "running", "job_id": job_id}

    def _launch(self, job, command, cwd):
        """Create the PowerShell job child: pipes, suspended start, job
        assignment, resume. Every handle this side keeps is transferred to
        the job on success; every other handle is closed here."""
        api = _win32()
        # The prologue runs inside the same -Command script, so every byte
        # the child writes to the pipes is UTF-8 and the command line
        # itself is UTF-16 all the way to the child.
        script = _JOB_PROLOGUE + command
        command_line = "%s -NoProfile -NonInteractive -Command %s" % (
            _quote_argument(user_shell()),
            _quote_argument(script),
        )
        inheritable = SECURITY_ATTRIBUTES()
        inheritable.nLength = ctypes.sizeof(SECURITY_ATTRIBUTES)
        inheritable.bInheritHandle = 1
        startup = STARTUPINFOW()
        startup.cb = ctypes.sizeof(STARTUPINFOW)
        startup.dwFlags = _STARTF_USESTDHANDLES
        information = PROCESS_INFORMATION()
        stdout_read = stdout_write = None
        stderr_read = stderr_write = None
        stdin_read = stdin_write = None
        container = None
        created = False
        transferred = False
        try:
            stdout_read, stdout_write = _create_pipe(api, inheritable)
            stderr_read, stderr_write = _create_pipe(api, inheritable)
            # stdin carries nothing: the child sees EOF at once.
            stdin_read, stdin_write = _create_pipe(api, inheritable)
            # Our ends must not leak into any other child; the child's ends
            # stay inheritable.
            api.SetHandleInformation(stdout_read, _HANDLE_FLAG_INHERIT, 0)
            api.SetHandleInformation(stderr_read, _HANDLE_FLAG_INHERIT, 0)
            api.SetHandleInformation(stdin_write, _HANDLE_FLAG_INHERIT, 0)
            startup.hStdInput = stdin_read
            startup.hStdOutput = stdout_write
            startup.hStdError = stderr_write
            if not api.CreateProcessW(
                user_shell(),
                command_line,
                None,
                None,
                True,
                _CREATE_SUSPENDED | _CREATE_NO_WINDOW,
                None,
                cwd or None,
                ctypes.byref(startup),
                ctypes.byref(information),
            ):
                raise _win32_error("CreateProcessW")
            created = True
            # The child inherited its ends; ours must close now or the
            # drains never see EOF.
            _close_handle(api, stdout_write)
            stdout_write = None
            _close_handle(api, stderr_write)
            stderr_write = None
            _close_handle(api, stdin_write)
            stdin_write = None
            container = _ProcessJob()
            try:
                container.assign_and_resume(
                    information.hProcess, information.hThread
                )
            except BaseException:
                container.close()
                container = None
                raise
            _close_handle(api, information.hThread)
            information.hThread = None
            job.container = container
            job.process_handle = information.hProcess
            information.hProcess = None
            job.stdout_handle = stdout_read
            stdout_read = None
            job.stderr_handle = stderr_read
            stderr_read = None
            transferred = True
        finally:
            if not transferred:
                if created:
                    _close_handle(api, information.hThread)
                    _close_handle(api, information.hProcess)
                if container is not None:
                    container.close()
                _close_handle(api, stdout_read)
                _close_handle(api, stderr_read)
            # Our copy of the child's stdin end is never needed: the child
            # owns its inherited duplicate.
            _close_handle(api, stdin_read)
            _close_handle(api, stdout_write)
            _close_handle(api, stderr_write)
            _close_handle(api, stdin_write)

    def _read_stream(self, job, stream_name, handle):
        api = _win32()
        buffer = (ctypes.c_ubyte * _CHUNK_BYTES)()
        try:
            while True:
                read = _DWORD(0)
                if not api.ReadFile(
                    handle, buffer, _CHUNK_BYTES, ctypes.byref(read), None
                ):
                    return
                if read.value == 0:
                    return
                self.link.send_data(
                    "shell",
                    job.job_id,
                    job.next_sequence(),
                    bytes(buffer[: read.value]),
                    stream=stream_name,
                )
        except (OSError, RuntimeError):
            return
        finally:
            _close_handle(api, handle)

    def _wait(self, job):
        api = _win32()
        try:
            try:
                wait = api.WaitForSingleObject(
                    job.process_handle, int(job.timeout * 1000)
                )
                if wait == _WAIT_TIMEOUT:
                    job.timed_out = True
                    if job.container is not None:
                        job.container.terminate(_STATUS_CONTROL_C_EXIT)
                    api.WaitForSingleObject(job.process_handle, 5000)
                exit_code = _DWORD(0)
                if api.GetExitCodeProcess(
                    job.process_handle, ctypes.byref(exit_code)
                ):
                    job.returncode = _to_signed_exit(exit_code.value)
                for reader in job.readers:
                    reader.join(5)
                job.status = "error" if job.timed_out else "finished"
                try:
                    self.link.event(
                        "shell",
                        "job.finished",
                        job.job_id,
                        {
                            "status": job.status,
                            "returncode": job.returncode,
                            "timed_out": job.timed_out,
                            "cancelled": job.cancelled,
                            "elapsed": time.monotonic() - job.started,
                        },
                    )
                except Exception:
                    pass  # the link may already be closing: best effort
            finally:
                _close_handle(api, job.process_handle)
                job.process_handle = None
                container = job.container
                job.container = None
                if container is not None:
                    container.close()
        finally:
            job.finished.set()
            self.registry.release()

    def cancel(self, job_id):
        with self.lock:
            job = self.jobs.get(job_id)
        if job is None:
            return {"status": "not_found", "job_id": job_id}
        if job.finished.is_set():
            return {
                "status": job.status,
                "job_id": job_id,
                "returncode": job.returncode,
            }
        job.cancelled = True
        if job.container is not None:
            job.container.terminate(_STATUS_CONTROL_C_EXIT)
        return {"status": "cancelled", "job_id": job_id}

    def shutdown(self):
        with self.lock:
            jobs = list(self.jobs.values())
        for job in jobs:
            if not job.finished.is_set():
                job.cancelled = True
                if job.container is not None:
                    job.container.terminate(_STATUS_CONTROL_C_EXIT)


class Terminal(object):
    def __init__(self, session_id, rows, cols, idle_timeout):
        self.session_id = session_id
        self.rows = rows
        self.cols = cols
        self.idle_timeout = idle_timeout
        self.last_activity = time.monotonic()
        self.queue = queue.Queue(maxsize=512)
        self.sequence = 0
        self.closed = threading.Event()
        self.status = "running"
        self.returncode = None
        self.lock = threading.Lock()
        self.write_lock = threading.Lock()
        self.closing = False
        self.released = False
        self.stopping = False
        self.draining = False
        self.teardown_started = False
        self.teardown_done = threading.Event()
        self.container = None
        self.pseudo_console = None
        self.process_handle = None
        self.input_write = None
        self.output_read = None
        self.reader_thread = None
        self.sender_thread = None
        self.waiter_thread = None


class ShellSessionManager(object):
    def __init__(self, link, registry):
        self.link = link
        self.registry = registry
        self.sessions = {}
        self.lock = threading.Lock()
        self.stop_event = threading.Event()
        threading.Thread(target=self._idle_loop, name="pty-idle", daemon=True).start()

    def open(self, cwd=None, shell=None, rows=40, cols=120, idle_timeout=900):
        rows = min(max(int(rows), 1), 1000)
        cols = min(max(int(cols), 1), 1000)
        idle_timeout = min(max(int(idle_timeout), 1), 3600)
        # A platform without ConPTY reports terminal_unsupported before a
        # registry slot is spent.
        _conpty_api()
        if not self.registry.acquire():
            raise RuntimeError("session_limit")
        selected_shell = shell or user_shell()
        if not os.path.isabs(selected_shell) or not os.path.isfile(selected_shell):
            self.registry.release()
            raise ValueError("shell not executable")
        if cwd is not None and not os.path.isdir(cwd):
            self.registry.release()
            raise ValueError("cwd does not exist")
        try:
            terminal = self._launch(
                selected_shell, cwd or None, rows, cols, idle_timeout
            )
        except BaseException:
            self.registry.release()
            raise
        with self.lock:
            self.sessions[terminal.session_id] = terminal
        terminal.reader_thread.start()
        terminal.sender_thread.start()
        terminal.waiter_thread.start()
        return {
            "status": "running",
            "session_id": terminal.session_id,
            "rows": rows,
            "cols": cols,
        }

    def _launch(self, shell, cwd, rows, cols, idle_timeout):
        """Create the ConPTY terminal child: pipes, pseudoconsole, attribute
        list, suspended start, job assignment, resume."""
        api = _conpty_api()
        input_read = input_write = None
        output_read = output_write = None
        attribute_list = None
        pseudo_console = None
        information = PROCESS_INFORMATION()
        container = None
        created = False
        transferred = False
        try:
            # ConPTY pipes are non-inheritable; the pseudoconsole duplicates
            # what it needs. This is the inverse of the job pipes, which
            # must be inheritable for the child.
            input_read, input_write = _create_pipe(api, None)
            output_read, output_write = _create_pipe(api, None)
            size = COORD(cols, rows)
            pseudo = _HANDLE()
            hresult = api.CreatePseudoConsole(
                size, input_read, output_write, 0, ctypes.byref(pseudo)
            )
            if hresult < 0:
                raise RuntimeError(
                    "io_error: CreatePseudoConsole failed with 0x%08X"
                    % (hresult & 0xFFFFFFFF)
                )
            pseudo_console = pseudo.value
            attribute_list = self._pseudoconsole_attribute_list(
                api, pseudo_console
            )
            startup = STARTUPINFOEXW()
            startup.StartupInfo.cb = ctypes.sizeof(STARTUPINFOEXW)
            startup.lpAttributeList = ctypes.cast(attribute_list, ctypes.c_void_p)
            command_line = "%s -NoLogo -NoProfile" % _quote_argument(shell)
            if not api.CreateProcessW(
                shell,
                command_line,
                None,
                None,
                False,
                _EXTENDED_STARTUPINFO_PRESENT | _CREATE_SUSPENDED,
                None,
                cwd,
                ctypes.byref(startup),
                ctypes.byref(information),
            ):
                raise _win32_error("CreateProcessW")
            created = True
            container = _ProcessJob()
            try:
                container.assign_and_resume(
                    information.hProcess, information.hThread
                )
            except BaseException:
                container.close()
                container = None
                raise
            _close_handle(api, information.hThread)
            information.hThread = None
            # The handles given to CreatePseudoConsole are freed from this
            # process so the pipe can report a broken channel.
            _close_handle(api, input_read)
            input_read = None
            _close_handle(api, output_write)
            output_write = None
            session_id = uuid.uuid4().hex
            terminal = Terminal(session_id, rows, cols, idle_timeout)
            terminal.container = container
            terminal.pseudo_console = pseudo_console
            terminal.process_handle = information.hProcess
            information.hProcess = None
            terminal.input_write = input_write
            input_write = None
            terminal.output_read = output_read
            output_read = None
            terminal.reader_thread = threading.Thread(
                target=self._read,
                args=(terminal,),
                name="pty-%s-read" % session_id[:8],
                daemon=True,
            )
            terminal.sender_thread = threading.Thread(
                target=self._send,
                args=(terminal,),
                name="pty-%s-send" % session_id[:8],
                daemon=True,
            )
            terminal.waiter_thread = threading.Thread(
                target=self._wait,
                args=(terminal,),
                name="pty-%s-wait" % session_id[:8],
                daemon=True,
            )
            container = None
            pseudo_console = None
            transferred = True
            return terminal
        finally:
            if not transferred:
                if created:
                    _close_handle(api, information.hThread)
                    _close_handle(api, information.hProcess)
                if container is not None:
                    container.close()
                if pseudo_console is not None:
                    api.ClosePseudoConsole(pseudo_console)
                _close_handle(api, input_read)
                _close_handle(api, input_write)
                _close_handle(api, output_read)
                _close_handle(api, output_write)
            if attribute_list is not None:
                api.DeleteProcThreadAttributeList(attribute_list)

    @staticmethod
    def _pseudoconsole_attribute_list(api, pseudo_console):
        """Attribute list binding the child to the pseudoconsole.

        The documented double call learns the buffer size first. The HPCON
        *value* is passed as lpValue with cbSize = sizeof(HPCON): passing a
        pointer to the variable holding it hands the child an
        invalid console handle, which is why a naive port never acts on
        its input.
        """
        needed = ctypes.c_size_t(0)
        api.InitializeProcThreadAttributeList(None, 1, 0, ctypes.byref(needed))
        buffer = ctypes.create_string_buffer(needed.value)
        if not api.InitializeProcThreadAttributeList(
            buffer, 1, 0, ctypes.byref(needed)
        ):
            raise _win32_error("InitializeProcThreadAttributeList")
        if not api.UpdateProcThreadAttribute(
            buffer,
            0,
            _PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            pseudo_console,
            ctypes.sizeof(ctypes.c_void_p),
            None,
            None,
        ):
            api.DeleteProcThreadAttributeList(buffer)
            raise _win32_error("UpdateProcThreadAttribute")
        return buffer

    def _read(self, terminal):
        api = _win32()
        buffer = (ctypes.c_ubyte * _CHUNK_BYTES)()
        try:
            while not terminal.stopping:
                if terminal.queue.full():
                    if terminal.stopping:
                        return
                    if terminal.draining:
                        # Teardown is draining: keep the pipe empty so
                        # ClosePseudoConsole never waits on an unread pipe.
                        read = _DWORD(0)
                        if not api.ReadFile(
                            terminal.output_read,
                            buffer,
                            _CHUNK_BYTES,
                            ctypes.byref(read),
                            None,
                        ) or read.value == 0:
                            return
                        continue
                    time.sleep(0.02)
                    continue
                read = _DWORD(0)
                if not api.ReadFile(
                    terminal.output_read,
                    buffer,
                    _CHUNK_BYTES,
                    ctypes.byref(read),
                    None,
                ):
                    return
                if read.value == 0:
                    return
                terminal.last_activity = time.monotonic()
                try:
                    # Bounded enqueue: a link that stopped acknowledging
                    # must drop output instead of wedging this reader.
                    terminal.queue.put(bytes(buffer[: read.value]), timeout=1)
                except queue.Full:
                    continue
        except OSError:
            pass
        finally:
            try:
                terminal.queue.put(None, timeout=5)
            except queue.Full:
                pass

    def _send(self, terminal):
        while True:
            try:
                data = terminal.queue.get(timeout=0.1)
            except queue.Empty:
                if terminal.closed.is_set() and terminal.queue.empty():
                    return
                continue
            if data is None:
                terminal.queue.task_done()
                return
            try:
                self.link.send_data(
                    "shell", terminal.session_id, terminal.sequence, data
                )
                terminal.sequence += 1
            except BaseException:
                return
            finally:
                terminal.queue.task_done()

    def _wait(self, terminal):
        api = _win32()
        try:
            api.WaitForSingleObject(terminal.process_handle, _INFINITE)
            exit_code = _DWORD(0)
            if api.GetExitCodeProcess(
                terminal.process_handle, ctypes.byref(exit_code)
            ):
                terminal.returncode = _to_signed_exit(exit_code.value)
            with terminal.lock:
                terminal.status = "closed"
            terminal.closed.set()
            try:
                self.link.event(
                    "shell",
                    "terminal.closed",
                    terminal.session_id,
                    {"status": "closed", "returncode": terminal.returncode},
                )
            except Exception:
                pass  # best effort while the link may be closing
            self._release_slot(terminal)
            # The exit path must not wait behind a closer that may be
            # joining this very thread.
            self._teardown(terminal, False)
        finally:
            self._release_slot(terminal)
            _close_handle(api, terminal.process_handle)
            terminal.process_handle = None

    def input(self, session_id, data):
        terminal = self._get(session_id)
        if terminal.closed.is_set() or terminal.status == "closing":
            return {"status": "closed", "session_id": session_id}
        api = _win32()
        terminal.last_activity = time.monotonic()
        with terminal.write_lock:
            total = 0
            while total < len(data):
                written = _DWORD(0)
                chunk = data[total:]
                if not api.WriteFile(
                    terminal.input_write,
                    chunk,
                    len(chunk),
                    ctypes.byref(written),
                    None,
                ):
                    raise RuntimeError("io_error: WriteFile to the terminal failed")
                if written.value == 0:
                    raise RuntimeError("io_error: short write to the terminal")
                total += written.value
        return {"status": "written", "session_id": session_id, "bytes": total}

    def resize(self, session_id, rows, cols):
        terminal = self._get(session_id)
        rows = min(max(int(rows), 1), 1000)
        cols = min(max(int(cols), 1), 1000)
        api = _win32()
        with terminal.lock:
            pseudo_console = terminal.pseudo_console
        size = COORD(cols, rows)
        hresult = api.ResizePseudoConsole(pseudo_console, size)
        if hresult < 0:
            raise RuntimeError(
                "io_error: ResizePseudoConsole failed with 0x%08X"
                % (hresult & 0xFFFFFFFF)
            )
        with terminal.lock:
            terminal.rows = rows
            terminal.cols = cols
            status = terminal.status
        terminal.last_activity = time.monotonic()
        return {
            "status": status,
            "session_id": session_id,
            "rows": rows,
            "cols": cols,
        }

    def close(self, session_id):
        terminal = self.sessions.get(session_id)
        if terminal is None:
            return {"status": "closed", "session_id": session_id}
        with terminal.lock:
            if terminal.closed.is_set():
                return {
                    "status": "closed",
                    "session_id": session_id,
                    "returncode": terminal.returncode,
                }
            if terminal.closing:
                return {"status": "closed", "session_id": session_id}
            terminal.closing = True
            terminal.status = "closing"
        container = terminal.container
        if container is not None:
            container.terminate(_STATUS_CONTROL_C_EXIT)
        # The drain, the pseudoconsole teardown and the thread joins
        # continue in the background: the dispatcher must never wait
        # behind them.
        threading.Thread(
            target=self._teardown,
            args=(terminal, True),
            name="pty-close-%s" % session_id[:8],
            daemon=True,
        ).start()
        return {"status": "closed", "session_id": session_id}

    def _teardown(self, terminal, wait_for_owner):
        """Exactly-once teardown; every caller waits for the owner to finish.

        The ordering that matters on Windows: the reader keeps draining
        while ClosePseudoConsole waits (so it must not run on the reading
        thread and must not run after the reader stopped), then the read
        end is closed to break a pending ReadFile, and only then the
        pipes are freed and the threads joined.
        """
        api = _win32()
        owner = False
        with terminal.lock:
            if not terminal.teardown_started:
                terminal.teardown_started = True
                owner = True
        if not owner:
            if wait_for_owner:
                terminal.teardown_done.wait(15)
            return
        try:
            # 0. Once teardown starts the reader must never block on a full
            #    output queue: it discards until the pipe breaks, because
            #    ClosePseudoConsole waits for the drain.
            terminal.draining = True
            # 1. Close the pseudoconsole while the reader still drains.
            if terminal.pseudo_console is not None:
                api.ClosePseudoConsole(terminal.pseudo_console)
                terminal.pseudo_console = None
            # 2. Stop the reader after the pseudoconsole broke the pipe:
            #    joining before closing the handle avoids closing a handle
            #    another thread is inside ReadFile with.
            terminal.stopping = True
            if (
                terminal.reader_thread is not None
                and terminal.reader_thread is not threading.current_thread()
            ):
                terminal.reader_thread.join(10)
            _close_handle(api, terminal.output_read)
            terminal.output_read = None
            _close_handle(api, terminal.input_write)
            terminal.input_write = None
            with terminal.lock:
                container = terminal.container
                terminal.container = None
            if container is not None:
                container.close()
            if (
                terminal.sender_thread is not None
                and terminal.sender_thread is not threading.current_thread()
            ):
                terminal.sender_thread.join(2)
            if (
                terminal.waiter_thread is not None
                and terminal.waiter_thread is not threading.current_thread()
            ):
                terminal.waiter_thread.join(5)
        finally:
            terminal.teardown_done.set()

    def _release_slot(self, terminal):
        release = False
        with terminal.lock:
            if not terminal.released:
                terminal.released = True
                release = True
        if release:
            self.registry.release()

    def _get(self, session_id):
        with self.lock:
            terminal = self.sessions.get(session_id)
        if terminal is None:
            raise KeyError("terminal_not_found")
        return terminal

    def _idle_loop(self):
        while not self.stop_event.wait(1):
            now = time.monotonic()
            with self.lock:
                terminals = list(self.sessions.values())
            for terminal in terminals:
                if (
                    not terminal.closed.is_set()
                    and now - terminal.last_activity > terminal.idle_timeout
                ):
                    self.close(terminal.session_id)

    def shutdown(self):
        self.stop_event.set()
        with self.lock:
            session_ids = list(self.sessions)
        for session_id in session_ids:
            self.close(session_id)
