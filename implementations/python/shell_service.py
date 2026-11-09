"""One-shot jobs and PTY terminals of the slave agent."""
from __future__ import print_function

import errno
import fcntl
import os
import pty
import pwd
import queue
import select
import signal
import struct
import subprocess
import termios
import threading
import time
import uuid


def user_shell():
    """The user's shell, with fallbacks if passwd points to something absent.

    zsh is not assumed: macOS ships it, Arch/Omarchy uses fish or bash. A
    non-existent shell turned any ``remote_shell`` into ENOENT.
    """
    configured = pwd.getpwuid(os.getuid()).pw_shell
    for candidate in (configured, "/bin/bash", "/bin/sh"):
        if candidate and os.path.isabs(candidate) and os.access(candidate, os.X_OK):
            return candidate
    raise RuntimeError("shell_unavailable")


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


def _group_exists(pgid):
    try:
        os.killpg(pgid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def terminate_group(pgid, wait_child=None):
    try:
        os.killpg(pgid, signal.SIGTERM)
    except ProcessLookupError:
        return
    except PermissionError:
        if wait_child and wait_child():
            return
        try:
            os.kill(pgid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError):
            return
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        if wait_child and wait_child():
            return
        if not _group_exists(pgid):
            return
        time.sleep(0.05)
    try:
        os.killpg(pgid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    except PermissionError:
        try:
            os.kill(pgid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass


class Job(object):
    def __init__(self, job_id, process, timeout):
        self.job_id = job_id
        self.process = process
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
        try:
            process = subprocess.Popen(
                [user_shell(), "-lc", command],
                cwd=cwd or None,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                stdin=subprocess.DEVNULL,
                start_new_session=True,
                close_fds=True,
            )
        except BaseException:
            self.registry.release()
            raise
        job = Job(job_id, process, timeout)
        with self.lock:
            self.jobs[job_id] = job
        for stream_name, stream in (("stdout", process.stdout), ("stderr", process.stderr)):
            thread = threading.Thread(
                target=self._read_stream,
                args=(job, stream_name, stream),
                name="job-%s-%s" % (job_id[:8], stream_name),
                daemon=True,
            )
            job.readers.append(thread)
            thread.start()
        threading.Thread(target=self._wait, args=(job,), name="job-%s-wait" % job_id[:8], daemon=True).start()
        return {"status": "running", "job_id": job_id}

    def _read_stream(self, job, stream_name, stream):
        try:
            while True:
                data = os.read(stream.fileno(), 512)
                if not data:
                    return
                self.link.send_data(
                    "shell", job.job_id, job.next_sequence(), data, stream=stream_name,
                )
        except (OSError, RuntimeError):
            return
        finally:
            try:
                stream.close()
            except OSError:
                pass

    def _wait(self, job):
        try:
            try:
                job.returncode = job.process.wait(timeout=job.timeout)
            except subprocess.TimeoutExpired:
                job.timed_out = True
                terminate_group(job.process.pid, lambda: job.process.poll() is not None)
                job.returncode = job.process.wait()
            for reader in job.readers:
                reader.join(timeout=5)
            job.status = "error" if job.timed_out else "finished"
            self.link.event("shell", "job.finished", job.job_id, {
                "status": job.status,
                "returncode": job.returncode,
                "timed_out": job.timed_out,
                "cancelled": job.cancelled,
                "elapsed": time.monotonic() - job.started,
            })
        finally:
            job.finished.set()
            self.registry.release()

    def cancel(self, job_id):
        with self.lock:
            job = self.jobs.get(job_id)
        if job is None:
            return {"status": "not_found", "job_id": job_id}
        if job.finished.is_set():
            return {"status": job.status, "job_id": job_id, "returncode": job.returncode}
        job.cancelled = True
        terminate_group(job.process.pid, lambda: job.process.poll() is not None)
        return {"status": "cancelled", "job_id": job_id}

    def shutdown(self):
        with self.lock:
            jobs = list(self.jobs.values())
        for job in jobs:
            if not job.finished.is_set():
                job.cancelled = True
                terminate_group(job.process.pid, lambda job=job: job.process.poll() is not None)


class Terminal(object):
    def __init__(self, session_id, pid, fd, idle_timeout):
        self.session_id = session_id
        self.pid = pid
        self.fd = fd
        self.idle_timeout = idle_timeout
        self.last_activity = time.monotonic()
        self.queue = queue.Queue(maxsize=512)
        self.sequence = 0
        self.closed = threading.Event()
        self.status = "running"
        self.returncode = None
        self.lock = threading.Lock()


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
        if not self.registry.acquire():
            raise RuntimeError("session_limit")
        selected_shell = shell or user_shell()
        if not os.path.isabs(selected_shell) or not os.access(selected_shell, os.X_OK):
            self.registry.release()
            raise ValueError("shell not executable")
        if cwd is not None and not os.path.isdir(cwd):
            self.registry.release()
            raise ValueError("cwd does not exist")
        pid, fd = pty.fork()
        if pid == 0:
            try:
                if cwd:
                    os.chdir(cwd)
                os.environ["TERM"] = "xterm-256color"
                os.execl(selected_shell, selected_shell, "-l")
            except BaseException:
                os._exit(127)
        os.set_blocking(fd, False)
        session_id = uuid.uuid4().hex
        terminal = Terminal(session_id, pid, fd, idle_timeout)
        self._set_size(terminal, rows, cols)
        with self.lock:
            self.sessions[session_id] = terminal
        threading.Thread(target=self._read, args=(terminal,), name="pty-%s-read" % session_id[:8], daemon=True).start()
        threading.Thread(target=self._send, args=(terminal,), name="pty-%s-send" % session_id[:8], daemon=True).start()
        threading.Thread(target=self._wait, args=(terminal,), name="pty-%s-wait" % session_id[:8], daemon=True).start()
        return {"status": "running", "session_id": session_id, "rows": rows, "cols": cols}

    def _read(self, terminal):
        try:
            while not terminal.closed.is_set():
                if terminal.queue.full():
                    time.sleep(0.02)
                    continue
                readable, _, _ = select.select([terminal.fd], [], [], 0.25)
                if not readable:
                    continue
                try:
                    data = os.read(terminal.fd, 512)
                except OSError as exc:
                    if exc.errno == errno.EIO:
                        break
                    raise
                if not data:
                    break
                terminal.last_activity = time.monotonic()
                terminal.queue.put(data)
        except OSError:
            pass
        finally:
            terminal.queue.put(None)

    def _send(self, terminal):
        while True:
            data = terminal.queue.get()
            if data is None:
                terminal.queue.task_done()
                return
            try:
                self.link.send_data("shell", terminal.session_id, terminal.sequence, data)
                terminal.sequence += 1
            except BaseException:
                return
            finally:
                terminal.queue.task_done()

    def _wait(self, terminal):
        try:
            _, status = os.waitpid(terminal.pid, 0)
            terminal.returncode = os.waitstatus_to_exitcode(status)
        except ChildProcessError:
            terminal.returncode = None
        terminal.status = "closed"
        terminal.closed.set()
        try:
            os.close(terminal.fd)
        except OSError:
            pass
        self.link.event("shell", "terminal.closed", terminal.session_id, {
            "status": "closed", "returncode": terminal.returncode,
        })
        self.registry.release()

    def input(self, session_id, data):
        terminal = self._get(session_id)
        if terminal.closed.is_set():
            return {"status": "closed", "session_id": session_id}
        terminal.last_activity = time.monotonic()
        total = 0
        while total < len(data):
            try:
                written = os.write(terminal.fd, data[total:])
            except BlockingIOError:
                select.select([], [terminal.fd], [], 1)
                continue
            total += written
        return {"status": "written", "session_id": session_id, "bytes": total}

    def resize(self, session_id, rows, cols):
        terminal = self._get(session_id)
        rows = min(max(int(rows), 1), 1000)
        cols = min(max(int(cols), 1), 1000)
        self._set_size(terminal, rows, cols)
        terminal.last_activity = time.monotonic()
        return {"status": terminal.status, "session_id": session_id, "rows": rows, "cols": cols}

    def _set_size(self, terminal, rows, cols):
        fcntl.ioctl(terminal.fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))

    def close(self, session_id):
        terminal = self.sessions.get(session_id)
        if terminal is None:
            return {"status": "closed", "session_id": session_id}
        with terminal.lock:
            if terminal.closed.is_set():
                return {"status": "closed", "session_id": session_id, "returncode": terminal.returncode}
            if terminal.status == "closing":
                return {"status": "closed", "session_id": session_id}
            terminal.status = "closing"
        terminate_group(terminal.pid, lambda: terminal.closed.is_set())
        return {"status": "closed", "session_id": session_id}

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
                if not terminal.closed.is_set() and now - terminal.last_activity > terminal.idle_timeout:
                    self.close(terminal.session_id)

    def shutdown(self):
        self.stop_event.set()
        with self.lock:
            session_ids = list(self.sessions)
        for session_id in session_ids:
            self.close(session_id)
