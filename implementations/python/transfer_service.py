"""Atomic transfers with exact size, SHA-256 and cancellation."""
from __future__ import print_function

import hashlib
import os
import tempfile
import threading
import time
from pathlib import Path

MAX_TRANSFER_BYTES = 512 * 1024 * 1024
# Cap of the download window; it matches wire.DATA_WINDOW_MAX, which this module
# does not import so that it stays importable inside the package.
MAX_DOWNLOAD_WINDOW = 64


class TransferError(RuntimeError):
    def __init__(self, code, message):
        RuntimeError.__init__(self, message)
        self.code = code
        self.message = message


class Upload(object):
    def __init__(self, transfer_id, final_path, temporary, stream, size, checksum):
        self.transfer_id = transfer_id
        self.final_path = final_path
        self.temporary = temporary
        self.stream = stream
        self.size = size
        self.checksum = checksum
        self.received = 0
        self.expected_seq = 0
        self.hasher = hashlib.sha256()
        self.status = "running"
        self.cancelled = threading.Event()
        self.last_progress = 0


class Download(object):
    def __init__(self, transfer_id, path, size, checksum, window=8):
        self.transfer_id = transfer_id
        self.path = path
        self.size = size
        self.checksum = checksum
        self.window = window
        self.transferred = 0
        self.status = "running"
        self.cancelled = threading.Event()


class TransferService(object):
    def __init__(self, link):
        self.link = link
        self.transfers = {}
        self.lock = threading.Lock()

    def _path(self, value):
        if not isinstance(value, str) or not value:
            raise TransferError("invalid_path", "path is mandatory")
        return Path(value).expanduser()

    def begin_upload(self, transfer_id, path, size, checksum):
        size = int(size)
        if size < 0 or size > MAX_TRANSFER_BYTES:
            raise TransferError("size_limit", "the transfer exceeds 512 MiB")
        if not isinstance(checksum, str) or len(checksum) != 64:
            raise TransferError("invalid_checksum", "sha256 is mandatory")
        final_path = self._path(path)
        final_path.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix=".%s.titella-" % final_path.name, dir=str(final_path.parent))
        stream = os.fdopen(fd, "wb")
        upload = Upload(transfer_id, final_path, temporary, stream, size, checksum)
        with self.lock:
            if transfer_id in self.transfers and self.transfers[transfer_id].status == "running":
                stream.close()
                os.unlink(temporary)
                raise TransferError("transfer_exists", "transfer_id is already active")
            self.transfers[transfer_id] = upload
        return {"status": "running", "transfer_id": transfer_id, "bytes_total": size, "bytes_transferred": 0}

    def upload_data(self, transfer_id, sequence, data):
        transfer = self.transfers.get(transfer_id)
        if not isinstance(transfer, Upload):
            raise TransferError("not_found", "upload not found")
        if transfer.cancelled.is_set():
            raise TransferError("cancelled", "upload cancelled")
        if sequence < transfer.expected_seq:
            # Block already written: the previous ACK was lost and the sender is
            # retransmitting. It is neither rewritten nor rejected; the caller
            # acknowledges it again.
            return
        if sequence != transfer.expected_seq:
            # Gap: it is neither written nor acknowledged; the sender will retransmit it.
            raise TransferError("sequence", "unexpected upload sequence")
        if transfer.received + len(data) > transfer.size:
            raise TransferError("size_mismatch", "upload exceeds the declared size")
        transfer.stream.write(data)
        transfer.hasher.update(data)
        transfer.received += len(data)
        transfer.expected_seq += 1
        if transfer.received - transfer.last_progress >= 1024 * 1024:
            transfer.last_progress = transfer.received
            self.link.event("transfer", "transfer.progress", transfer_id, self.status(transfer_id))

    def commit_upload(self, transfer_id):
        transfer = self.transfers.get(transfer_id)
        if not isinstance(transfer, Upload):
            raise TransferError("not_found", "upload not found")
        try:
            transfer.stream.flush()
            os.fsync(transfer.stream.fileno())
            transfer.stream.close()
            if transfer.received != transfer.size:
                raise TransferError("size_mismatch", "received size differs from the declared one")
            digest = transfer.hasher.hexdigest()
            if digest != transfer.checksum:
                raise TransferError("checksum_mismatch", "upload SHA-256 does not match")
            os.replace(transfer.temporary, str(transfer.final_path))
            transfer.status = "finished"
            result = self.status(transfer_id)
            result["sha256"] = digest
            return result
        except BaseException:
            transfer.status = "error"
            try:
                transfer.stream.close()
            except Exception:
                pass
            try:
                os.unlink(transfer.temporary)
            except OSError:
                pass
            raise

    def begin_download(self, transfer_id, path, expected_checksum=None, window=8):
        target = self._path(path)
        size = target.stat().st_size
        if size > MAX_TRANSFER_BYTES:
            raise TransferError("size_limit", "the transfer exceeds 512 MiB")
        hasher = hashlib.sha256()
        with target.open("rb") as stream:
            while True:
                chunk = stream.read(1024 * 1024)
                if not chunk:
                    break
                hasher.update(chunk)
        checksum = hasher.hexdigest()
        if expected_checksum and checksum != expected_checksum:
            raise TransferError("checksum_mismatch", "the remote checksum does not match the expected one")
        if isinstance(window, bool) or not isinstance(window, int) or window < 1:
            window = 8
        window = min(window, MAX_DOWNLOAD_WINDOW)
        download = Download(transfer_id, target, size, checksum, window)
        with self.lock:
            if transfer_id in self.transfers and self.transfers[transfer_id].status == "running":
                raise TransferError("transfer_exists", "transfer_id is already active")
            self.transfers[transfer_id] = download
        threading.Thread(
            target=self._send_download, args=(download,),
            name="download-%s" % transfer_id[:8], daemon=True,
        ).start()
        return {"status": "running", "transfer_id": transfer_id, "bytes_total": size, "bytes_transferred": 0, "sha256": checksum}

    def _send_download(self, transfer):
        hasher = hashlib.sha256()
        try:
            with transfer.path.open("rb") as stream:
                def read_chunk():
                    if transfer.cancelled.is_set():
                        raise TransferError("cancelled", "download cancelled")
                    chunk = stream.read(512)
                    if chunk:
                        hasher.update(chunk)
                    return chunk

                def on_ack(total):
                    transfer.transferred = total

                self.link.stream_data(
                    "transfer",
                    transfer.transfer_id,
                    read_chunk,
                    window=transfer.window,
                    on_ack=on_ack,
                    stop_check=transfer.cancelled.is_set,
                )
            if transfer.cancelled.is_set():
                transfer.status = "cancelled"
            elif transfer.transferred != transfer.size or hasher.hexdigest() != transfer.checksum:
                transfer.status = "error"
            else:
                transfer.status = "finished"
            self.link.event("transfer", "transfer.finished", transfer.transfer_id, self.status(transfer.transfer_id))
        except BaseException as exc:
            if transfer.cancelled.is_set():
                transfer.status = "cancelled"
                body = self.status(transfer.transfer_id)
            else:
                transfer.status = "error"
                body = {
                    "status": "error", "transfer_id": transfer.transfer_id,
                    "code": "download_failed", "message": str(exc), "retryable": True,
                }
            # The link may be closing: do not leave the thread with a traceback.
            try:
                self.link.event("transfer", "transfer.finished", transfer.transfer_id, body)
            except BaseException:
                pass

    def status(self, transfer_id):
        transfer = self.transfers.get(transfer_id)
        if transfer is None:
            return {"status": "not_found", "transfer_id": transfer_id}
        transferred = transfer.received if isinstance(transfer, Upload) else transfer.transferred
        return {
            "status": transfer.status,
            "transfer_id": transfer_id,
            "bytes_total": transfer.size,
            "bytes_transferred": transferred,
        }

    def cancel(self, transfer_id):
        transfer = self.transfers.get(transfer_id)
        if transfer is None:
            return {"status": "not_found", "transfer_id": transfer_id}
        if transfer.status != "running":
            return self.status(transfer_id)
        transfer.cancelled.set()
        transfer.status = "cancelled"
        if isinstance(transfer, Upload):
            try:
                transfer.stream.close()
            except OSError:
                pass
            try:
                os.unlink(transfer.temporary)
            except OSError:
                pass
        return self.status(transfer_id)

    def shutdown(self):
        with self.lock:
            identifiers = list(self.transfers)
        for transfer_id in identifiers:
            self.cancel(transfer_id)
