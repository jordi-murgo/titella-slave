"""File operations with the permissions of the current user."""
from __future__ import print_function

import base64
import fnmatch
import hashlib
import json
import os
import platform
import stat
import tempfile
from pathlib import Path

MAX_READ_BYTES = 64 * 1024
DEFAULT_READ_BYTES = 32 * 1024
MAX_WRITE_BYTES = 64 * 1024
MAX_LIST_ENTRIES = 200
MAX_SEARCH_RESULTS = 200
MAX_FIND_RESULTS = 500
MAX_SEARCH_READ = 4 * 1024 * 1024


class FileServiceError(RuntimeError):
    def __init__(self, code, message):
        RuntimeError.__init__(self, message)
        self.code = code
        self.message = message


class FileService(object):
    def _path(self, value):
        if not isinstance(value, str) or not value:
            raise FileServiceError("invalid_path", "path is mandatory")
        return Path(value).expanduser()

    def _atomic_write(self, path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix=".%s.titella-" % path.name, dir=str(path.parent))
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, str(path))
        except BaseException:
            try:
                os.close(fd)
            except OSError:
                pass
            try:
                os.unlink(temporary)
            except OSError:
                pass
            raise

    def read(self, path, offset=0, length=DEFAULT_READ_BYTES):
        target = self._path(path)
        offset = max(0, int(offset))
        length = min(max(1, int(length)), MAX_READ_BYTES)
        size = target.stat().st_size
        with target.open("rb") as stream:
            stream.seek(offset)
            data = stream.read(length)
        return {
            "path": str(target),
            "offset": offset,
            "next_offset": offset + len(data),
            "size": size,
            "eof": offset + len(data) >= size,
            "bytes": len(data),
        }, data

    def write(self, path, data):
        if len(data) > MAX_WRITE_BYTES:
            raise FileServiceError("write_too_large", "remote_fs_write accepts up to 64 KiB")
        target = self._path(path)
        self._atomic_write(target, data)
        return {"status": "written", "path": str(target), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}

    def list(self, path, offset=0, limit=MAX_LIST_ENTRIES):
        target = self._path(path)
        offset = max(0, int(offset))
        limit = min(max(1, int(limit)), MAX_LIST_ENTRIES)
        entries = []
        with os.scandir(str(target)) as iterator:
            raw = sorted(iterator, key=lambda item: item.name)
        for item in raw[offset:offset + limit]:
            info = item.stat(follow_symlinks=False)
            if stat.S_ISLNK(info.st_mode):
                kind = "symlink"
            elif stat.S_ISDIR(info.st_mode):
                kind = "directory"
            elif stat.S_ISREG(info.st_mode):
                kind = "file"
            else:
                kind = "other"
            entries.append({
                "name": item.name,
                "path": item.path,
                "type": kind,
                "size": info.st_size,
                "mtime": info.st_mtime,
            })
        next_offset = offset + len(entries)
        return {
            "path": str(target),
            "entries": entries,
            "offset": offset,
            "next_offset": next_offset,
            "has_more": next_offset < len(raw),
            "total": len(raw),
        }

    def delete(self, path):
        target = self._path(path)
        info = target.lstat()
        if stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode):
            raise FileServiceError("directory_delete_denied", "delete is not recursive and does not remove directories")
        target.unlink()
        return {"status": "deleted", "path": str(target)}

    def replace(self, path, old, new):
        target = self._path(path)
        if not isinstance(old, str) or not old:
            raise FileServiceError("invalid_argument", "old must not be empty")
        data = target.read_bytes()
        if len(data) > MAX_SEARCH_READ:
            raise FileServiceError("file_too_large", "replace accepts files of up to 4 MiB")
        old_bytes = old.encode("utf-8")
        count = data.count(old_bytes)
        if count != 1:
            raise FileServiceError("replace_count", "replace requires exactly one occurrence; found %d" % count)
        updated = data.replace(old_bytes, new.encode("utf-8"), 1)
        self._atomic_write(target, updated)
        return {"status": "replaced", "path": str(target), "bytes": len(updated)}

    def search(self, path, query, limit=MAX_SEARCH_RESULTS):
        target = self._path(path)
        if not isinstance(query, str) or not query:
            raise FileServiceError("invalid_argument", "query is mandatory")
        limit = min(max(1, int(limit)), MAX_SEARCH_RESULTS)
        with target.open("rb") as stream:
            data = stream.read(MAX_SEARCH_READ + 1)
        truncated = len(data) > MAX_SEARCH_READ
        data = data[:MAX_SEARCH_READ]
        text = data.decode("utf-8", errors="replace")
        results = []
        for number, line in enumerate(text.splitlines(), 1):
            if query in line:
                results.append({"line": number, "text": line[:1000]})
                if len(results) >= limit:
                    break
        return {"path": str(target), "query": query, "results": results, "truncated": truncated or len(results) >= limit}

    def find(self, path, pattern, limit=MAX_FIND_RESULTS):
        target = self._path(path)
        if not isinstance(pattern, str) or not pattern:
            raise FileServiceError("invalid_argument", "pattern is mandatory")
        limit = min(max(1, int(limit)), MAX_FIND_RESULTS)
        results = []
        if target.is_file() or target.is_symlink():
            if fnmatch.fnmatch(target.name, pattern):
                results.append(str(target))
        else:
            for root, directories, files in os.walk(str(target), followlinks=False):
                directories.sort()
                files.sort()
                for name in directories + files:
                    if fnmatch.fnmatch(name, pattern):
                        results.append(os.path.join(root, name))
                        if len(results) >= limit:
                            return {"path": str(target), "pattern": pattern, "results": results, "truncated": True}
        return {"path": str(target), "pattern": pattern, "results": results, "truncated": False}

    def sysinfo(self):
        uname = platform.uname()
        return {
            "system": uname.system,
            "release": uname.release,
            "version": uname.version,
            "machine": uname.machine,
            "hostname": uname.node,
            "python": platform.python_version(),
            "uid": os.getuid(),
            "gid": os.getgid(),
            "home": str(Path.home()),
            "runtime": {"name": "python", "version": platform.python_version()},
        }

    def dispatch(self, op, body):
        try:
            return self._dispatch(op, body)
        except FileServiceError:
            raise
        except FileNotFoundError as exc:
            raise FileServiceError("not_found", str(exc))
        except PermissionError as exc:
            raise FileServiceError("permission_denied", str(exc))
        except OSError as exc:
            raise FileServiceError("io_error", str(exc))

    def _dispatch(self, op, body):
        if op == "read":
            metadata, data = self.read(body.get("path"), body.get("offset", 0), body.get("length", DEFAULT_READ_BYTES))
            return metadata, data
        if op == "write":
            encoded = body.get("data_b64")
            if not isinstance(encoded, str):
                raise FileServiceError("invalid_argument", "data_b64 is mandatory")
            try:
                data = base64.b64decode(encoded, validate=True)
            except ValueError:
                raise FileServiceError("invalid_base64", "data_b64 is not valid")
            return self.write(body.get("path"), data), None
        if op == "list":
            return self.list(body.get("path"), body.get("offset", 0), body.get("limit", MAX_LIST_ENTRIES)), None
        if op == "delete":
            return self.delete(body.get("path")), None
        if op == "replace":
            return self.replace(body.get("path"), body.get("old"), body.get("new", "")), None
        if op == "search":
            return self.search(body.get("path"), body.get("query"), body.get("limit", MAX_SEARCH_RESULTS)), None
        if op == "find":
            return self.find(body.get("path"), body.get("pattern"), body.get("limit", MAX_FIND_RESULTS)), None
        if op == "sysinfo":
            return self.sysinfo(), None
        raise FileServiceError("unknown_op", "unknown fs operation")
