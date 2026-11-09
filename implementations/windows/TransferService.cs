// Atomic transfers with exact size, SHA-256 and cancellation. Port of
// implementations/python/transfer_service.py: the destination is never
// visible until size and digest are verified, the streaming window is
// 1..64 blocks, and only the upload's own temporary file is ever removed.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;

    internal static class TransferLimits
    {
        public const long MaxTransferBytes = 512L * 1024L * 1024L;
        public const int MaxDownloadWindow = 64;
        public const int ProgressStepBytes = 1024 * 1024;
        public const int DownloadHashBlock = 1024 * 1024;
    }

    internal sealed class Upload
    {
        public string TransferId;
        public string FinalPath;
        public string Temporary;
        public FileStream Stream;
        public long Size;
        public string Checksum;
        public long Received;
        public long ExpectedSequence;
        public IncrementalHash Hasher;
        public string Status = "running";
        public readonly ManualResetEventSlim Cancelled = new ManualResetEventSlim(false);
        public long LastProgress;
    }

    internal sealed class Download
    {
        public string TransferId;
        public string Path;
        public long Size;
        public string Checksum;
        public int Window = WireConstants.DataWindowDefault;
        public long Transferred;
        public string Status = "running";
        public readonly ManualResetEventSlim Cancelled = new ManualResetEventSlim(false);
    }

    /// <summary>Upload and download lifetime with exact verification.</summary>
    internal sealed class TransferService
    {
        private readonly AgentLink _link;
        private readonly Dictionary<string, object> _transfers =
            new Dictionary<string, object>();
        private readonly object _gate = new object();

        public TransferService(AgentLink link)
        {
            _link = link;
        }

        private static string PathOf(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ProtocolError("invalid_path", "path is mandatory", false);
            }
            return System.IO.Path.GetFullPath(value);
        }

        private static string Key(string transferId)
        {
            // Python keys the map with None for a missing id; a single empty
            // bucket reproduces that for the only reachable case.
            return transferId == null ? string.Empty : transferId;
        }

        public IDictionary<string, object> BeginUpload(string transferId, string path,
                                                       long size, string checksum)
        {
            if (size < 0 || size > TransferLimits.MaxTransferBytes)
            {
                throw new ProtocolError(
                    "size_limit", "the transfer exceeds 512 MiB", false);
            }
            if (checksum == null || checksum.Length != 64)
            {
                throw new ProtocolError(
                    "invalid_checksum", "sha256 is mandatory", false);
            }
            string finalPath = PathOf(path);
            string directory = System.IO.Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string temporary = System.IO.Path.Combine(
                directory ?? string.Empty,
                "." + System.IO.Path.GetFileName(finalPath) + ".titella-" +
                Guid.NewGuid().ToString("N"));
            FileStream stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            Upload upload = new Upload();
            upload.TransferId = transferId;
            upload.FinalPath = finalPath;
            upload.Temporary = temporary;
            upload.Stream = stream;
            upload.Size = size;
            upload.Checksum = checksum;
            upload.Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            lock (_gate)
            {
                string key = Key(transferId);
                object existing;
                if (_transfers.TryGetValue(key, out existing))
                {
                    Upload previous = existing as Upload;
                    Download previousDownload = existing as Download;
                    bool running =
                        (previous != null && previous.Status == "running") ||
                        (previousDownload != null && previousDownload.Status == "running");
                    if (running)
                    {
                        stream.Dispose();
                        DeleteOwnTemporary(temporary);
                        throw new ProtocolError(
                            "transfer_exists",
                            "transfer_id is already active",
                            false);
                    }
                }
                _transfers[key] = upload;
            }
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "running";
            result["transfer_id"] = transferId;
            result["bytes_total"] = size;
            result["bytes_transferred"] = 0;
            return result;
        }

        private static void DeleteOwnTemporary(string temporary)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception)
            {
                // Deleting our own partial is best effort.
            }
        }

        public void UploadData(string transferId, long sequence, byte[] data)
        {
            object entry;
            lock (_gate)
            {
                _transfers.TryGetValue(Key(transferId), out entry);
            }
            Upload transfer = entry as Upload;
            if (transfer == null)
            {
                throw new ProtocolError("not_found", "upload not found", true);
            }
            if (transfer.Cancelled.IsSet)
            {
                throw new ProtocolError("cancelled", "upload cancelled", false);
            }
            if (sequence < transfer.ExpectedSequence)
            {
                // Block already written: the previous ACK was lost and the
                // sender is retransmitting. Neither rewrite nor reject it;
                // the caller acknowledges it again.
                return;
            }
            if (sequence != transfer.ExpectedSequence)
            {
                // Gap: neither written nor acknowledged; the sender retries.
                throw new ProtocolError("sequence", "unexpected upload sequence", false);
            }
            if (transfer.Received + data.Length > transfer.Size)
            {
                throw new ProtocolError(
                    "size_mismatch", "upload exceeds the declared size", false);
            }
            transfer.Stream.Write(data, 0, data.Length);
            transfer.Hasher.AppendData(data, 0, data.Length);
            transfer.Received += data.Length;
            transfer.ExpectedSequence++;
            if (transfer.Received - transfer.LastProgress >= TransferLimits.ProgressStepBytes)
            {
                transfer.LastProgress = transfer.Received;
                _link.Event("transfer", "transfer.progress", transferId, Status(transferId));
            }
        }

        public IDictionary<string, object> CommitUpload(string transferId)
        {
            object entry;
            lock (_gate)
            {
                _transfers.TryGetValue(Key(transferId), out entry);
            }
            Upload transfer = entry as Upload;
            if (transfer == null)
            {
                throw new ProtocolError("not_found", "upload not found", true);
            }
            try
            {
                transfer.Stream.Flush(true);
                transfer.Stream.Dispose();
                if (transfer.Received != transfer.Size)
                {
                    throw new ProtocolError(
                        "size_mismatch",
                        "received size differs from the declared one",
                        false);
                }
                string digest = FileService.Hex(transfer.Hasher.GetHashAndReset());
                if (!string.Equals(digest, transfer.Checksum, StringComparison.Ordinal))
                {
                    throw new ProtocolError(
                        "checksum_mismatch", "upload SHA-256 does not match", false);
                }
                if (!NativeMethods.MoveFileExW(
                        transfer.Temporary, transfer.FinalPath,
                        Win32Constants.MOVEFILE_REPLACE_EXISTING |
                            Win32Constants.MOVEFILE_WRITE_THROUGH))
                {
                    throw new IOException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "MoveFileExW failed with {0}",
                            Marshal.GetLastWin32Error()));
                }
                transfer.Status = "finished";
                IDictionary<string, object> result = Status(transferId);
                result["sha256"] = digest;
                return result;
            }
            catch
            {
                transfer.Status = "error";
                try
                {
                    transfer.Stream.Dispose();
                }
                catch (Exception)
                {
                    // Already closed by the failed commit path.
                }
                DeleteOwnTemporary(transfer.Temporary);
                throw;
            }
        }

        public IDictionary<string, object> BeginDownload(string transferId, string path,
                                                         string expectedChecksum, int window)
        {
            string target = PathOf(path);
            FileInfo info = new FileInfo(target);
            long size = info.Length;
            if (size > TransferLimits.MaxTransferBytes)
            {
                throw new ProtocolError(
                    "size_limit", "the transfer exceeds 512 MiB", false);
            }
            string checksum;
            using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (FileStream stream = new FileStream(
                    target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[TransferLimits.DownloadHashBlock];
                    while (true)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }
                        hasher.AppendData(buffer, 0, read);
                    }
                }
                checksum = FileService.Hex(hasher.GetHashAndReset());
            }
            if (!string.IsNullOrEmpty(expectedChecksum) &&
                !string.Equals(checksum, expectedChecksum, StringComparison.Ordinal))
            {
                throw new ProtocolError(
                    "checksum_mismatch",
                    "the remote checksum does not match the expected one",
                    false);
            }
            if (window < 1)
            {
                window = WireConstants.DataWindowDefault;
            }
            window = Math.Min(window, TransferLimits.MaxDownloadWindow);
            Download download = new Download();
            download.TransferId = transferId;
            download.Path = target;
            download.Size = size;
            download.Checksum = checksum;
            download.Window = window;
            lock (_gate)
            {
                object existing;
                if (_transfers.TryGetValue(Key(transferId), out existing))
                {
                    Upload previousUpload = existing as Upload;
                    Download previousDownload = existing as Download;
                    bool running =
                        (previousUpload != null && previousUpload.Status == "running") ||
                        (previousDownload != null && previousDownload.Status == "running");
                    if (running)
                    {
                        throw new ProtocolError(
                            "transfer_exists", "transfer_id is already active", false);
                    }
                }
                _transfers[Key(transferId)] = download;
            }
            Thread thread = new Thread(delegate() { SendDownload(download); });
            thread.IsBackground = true;
            thread.Name = "download-" + ShortId(transferId);
            thread.Start();
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "running";
            result["transfer_id"] = transferId;
            result["bytes_total"] = size;
            result["bytes_transferred"] = 0;
            result["sha256"] = checksum;
            return result;
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "transfer";
            }
            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private void SendDownload(Download transfer)
        {
            IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                using (FileStream stream = new FileStream(
                    transfer.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Func<byte[]> readChunk = delegate()
                    {
                        if (transfer.Cancelled.IsSet)
                        {
                            throw new ProtocolError("cancelled", "download cancelled", false);
                        }
                        byte[] chunk = new byte[WireConstants.DataChunkBytes];
                        int read = stream.Read(chunk, 0, chunk.Length);
                        if (read <= 0)
                        {
                            return new byte[0];
                        }
                        if (read != chunk.Length)
                        {
                            byte[] shorter = new byte[read];
                            Array.Copy(chunk, shorter, read);
                            chunk = shorter;
                        }
                        hasher.AppendData(chunk, 0, chunk.Length);
                        return chunk;
                    };
                    Action<int> onAck = delegate(int total) { transfer.Transferred = total; };
                    Func<bool> stopCheck = delegate() { return transfer.Cancelled.IsSet; };
                    _link.StreamData(
                        "transfer", transfer.TransferId, readChunk,
                        transfer.Window, onAck, stopCheck);
                }
                if (transfer.Cancelled.IsSet)
                {
                    transfer.Status = "cancelled";
                }
                else if (transfer.Transferred != transfer.Size ||
                         !string.Equals(FileService.Hex(hasher.GetHashAndReset()),
                                        transfer.Checksum, StringComparison.Ordinal))
                {
                    transfer.Status = "error";
                }
                else
                {
                    transfer.Status = "finished";
                }
                _link.Event("transfer", "transfer.finished", transfer.TransferId,
                            Status(transfer.TransferId));
            }
            catch (Exception error)
            {
                if (transfer.Cancelled.IsSet)
                {
                    transfer.Status = "cancelled";
                    try
                    {
                        _link.Event("transfer", "transfer.finished", transfer.TransferId,
                                    Status(transfer.TransferId));
                    }
                    catch (Exception)
                    {
                        // The link may be closing; do not leave a traceback.
                    }
                }
                else
                {
                    transfer.Status = "error";
                    IDictionary<string, object> body = new Dictionary<string, object>();
                    body["status"] = "error";
                    body["transfer_id"] = transfer.TransferId;
                    body["code"] = "download_failed";
                    body["message"] = error.Message;
                    body["retryable"] = true;
                    try
                    {
                        _link.Event("transfer", "transfer.finished", transfer.TransferId, body);
                    }
                    catch (Exception)
                    {
                        // Best effort while the link may be closing.
                    }
                }
            }
            finally
            {
                hasher.Dispose();
            }
        }

        public IDictionary<string, object> Status(string transferId)
        {
            object entry;
            lock (_gate)
            {
                _transfers.TryGetValue(Key(transferId), out entry);
            }
            if (entry == null)
            {
                IDictionary<string, object> missing = new Dictionary<string, object>();
                missing["status"] = "not_found";
                missing["transfer_id"] = transferId;
                return missing;
            }
            Upload upload = entry as Upload;
            Download download = entry as Download;
            IDictionary<string, object> result = new Dictionary<string, object>();
            if (upload != null)
            {
                result["status"] = upload.Status;
                result["transfer_id"] = transferId;
                result["bytes_total"] = upload.Size;
                result["bytes_transferred"] = upload.Received;
            }
            else
            {
                result["status"] = download.Status;
                result["transfer_id"] = transferId;
                result["bytes_total"] = download.Size;
                result["bytes_transferred"] = download.Transferred;
            }
            return result;
        }

        public IDictionary<string, object> Cancel(string transferId)
        {
            object entry;
            lock (_gate)
            {
                _transfers.TryGetValue(Key(transferId), out entry);
            }
            if (entry == null)
            {
                IDictionary<string, object> missing = new Dictionary<string, object>();
                missing["status"] = "not_found";
                missing["transfer_id"] = transferId;
                return missing;
            }
            Upload upload = entry as Upload;
            Download download = entry as Download;
            string status = upload != null ? upload.Status : download.Status;
            if (!string.Equals(status, "running", StringComparison.Ordinal))
            {
                return Status(transferId);
            }
            if (upload != null)
            {
                upload.Cancelled.Set();
                upload.Status = "cancelled";
                try
                {
                    upload.Stream.Dispose();
                }
                catch (Exception)
                {
                    // A cancelled upload closes its own stream; failure to
                    // close it does not change the outcome.
                }
                DeleteOwnTemporary(upload.Temporary);
            }
            else
            {
                download.Cancelled.Set();
                download.Status = "cancelled";
            }
            return Status(transferId);
        }

        public void Shutdown()
        {
            List<string> identifiers;
            lock (_gate)
            {
                identifiers = new List<string>(_transfers.Keys);
            }
            foreach (string transferId in identifiers)
            {
                try
                {
                    Cancel(transferId);
                }
                catch (Exception)
                {
                    // Shutdown cancels everything it can.
                }
            }
        }
    }
}
