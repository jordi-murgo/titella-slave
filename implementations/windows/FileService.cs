// File operations with the permissions of the current user. Port of
// implementations/python/file_service.py: identical limits, response
// shapes, error codes, ordinal pagination, literal case-sensitive search,
// fnmatch-style find that never descends into reparse points, and atomic
// write-through replace.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;

    internal static class FileLimits
    {
        public const int MaxReadBytes = 64 * 1024;
        public const int DefaultReadBytes = 32 * 1024;
        public const int MaxWriteBytes = 64 * 1024;
        public const int MaxListEntries = 200;
        public const int MaxSearchResults = 200;
        public const int MaxFindResults = 500;
        public const int MaxSearchRead = 4 * 1024 * 1024;
        public const int MaxLineCharacters = 1000;
    }

    /// <summary>File operations and their dispatch table.</summary>
    internal sealed class FileService
    {
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Returns (result, payload); payload is non-null only for read.</summary>
        public sealed class Result
        {
            public IDictionary<string, object> Metadata;
            public byte[] Payload;
        }

        public Result Dispatch(string op, IDictionary<string, object> body)
        {
            try
            {
                return DispatchInner(op, body);
            }
            catch (ProtocolError)
            {
                throw;
            }
            catch (FileNotFoundException error)
            {
                throw new ProtocolError("not_found", error.Message, false);
            }
            catch (DirectoryNotFoundException error)
            {
                throw new ProtocolError("not_found", error.Message, false);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new ProtocolError("permission_denied", error.Message, false);
            }
            catch (System.Security.SecurityException error)
            {
                throw new ProtocolError("permission_denied", error.Message, false);
            }
            catch (IOException error)
            {
                throw new ProtocolError("io_error", error.Message, false);
            }
        }

        private Result DispatchInner(string op, IDictionary<string, object> body)
        {
            if (body == null)
            {
                body = new Dictionary<string, object>();
            }
            if (op == "read")
            {
                Result result = new Result();
                result.Metadata = Read(
                    Str(body, "path"),
                    (int)Json.GetInt64(body, "offset", 0),
                    (int)Json.GetInt64(body, "length", FileLimits.DefaultReadBytes),
                    out result.Payload);
                return result;
            }
            if (op == "write")
            {
                object encodedValue;
                body.TryGetValue("data_b64", out encodedValue);
                string encoded = encodedValue as string;
                if (encoded == null)
                {
                    throw new ProtocolError("invalid_argument", "data_b64 is mandatory", false);
                }
                byte[] data;
                if (!Wire.IsStrictBase64(encoded))
                {
                    throw new ProtocolError(
                        "invalid_base64", "data_b64 is not valid", false);
                }
                try
                {
                    data = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    throw new ProtocolError(
                        "invalid_base64", "data_b64 is not valid", false);
                }
                Result result = new Result();
                result.Metadata = Write(Str(body, "path"), data);
                return result;
            }
            if (op == "list")
            {
                Result result = new Result();
                result.Metadata = List(
                    Str(body, "path"),
                    (int)Json.GetInt64(body, "offset", 0),
                    (int)Json.GetInt64(body, "limit", FileLimits.MaxListEntries));
                return result;
            }
            if (op == "delete")
            {
                Result result = new Result();
                result.Metadata = Delete(Str(body, "path"));
                return result;
            }
            if (op == "replace")
            {
                Result result = new Result();
                result.Metadata = Replace(
                    Str(body, "path"), Str(body, "old"), StrOrEmpty(body, "new"));
                return result;
            }
            if (op == "search")
            {
                Result result = new Result();
                result.Metadata = Search(
                    Str(body, "path"),
                    Str(body, "query"),
                    (int)Json.GetInt64(body, "limit", FileLimits.MaxSearchResults));
                return result;
            }
            if (op == "find")
            {
                Result result = new Result();
                result.Metadata = Find(
                    Str(body, "path"),
                    Str(body, "pattern"),
                    (int)Json.GetInt64(body, "limit", FileLimits.MaxFindResults));
                return result;
            }
            if (op == "sysinfo")
            {
                Result result = new Result();
                result.Metadata = Sysinfo();
                return result;
            }
            throw new ProtocolError("unknown_op", "unknown fs operation", false);
        }

        private static string Str(IDictionary<string, object> body, string key)
        {
            object value;
            body.TryGetValue(key, out value);
            return value as string;
        }

        private static string StrOrEmpty(IDictionary<string, object> body, string key)
        {
            object value;
            body.TryGetValue(key, out value);
            string text = value as string;
            return text == null ? string.Empty : text;
        }

        /// <summary>
        /// Expands a leading ~ (or ~name) to the user profile exactly once and
        /// then applies native path semantics. %VAR% and $env: are never
        /// expanded.
        /// </summary>
        private static string PathOf(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ProtocolError("invalid_path", "path is mandatory", false);
            }
            string expanded = ExpandHome(value);
            return Path.GetFullPath(expanded);
        }

        private static string ExpandHome(string value)
        {
            if (value.Length == 0 || value[0] != '~')
            {
                return value;
            }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (value.Length == 1)
            {
                return profile;
            }
            if (value[1] == '/' || value[1] == '\\')
            {
                return profile + value.Substring(1);
            }
            int separator = value.IndexOfAny(new char[] { '/', '\\' });
            if (separator < 0)
            {
                return profile;
            }
            return profile + value.Substring(separator);
        }

        private static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(data));
            }
        }

        internal static string Hex(byte[] value)
        {
            StringBuilder builder = new StringBuilder(value.Length * 2);
            for (int index = 0; index < value.Length; index++)
            {
                builder.Append(value[index].ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static string RandomSuffix()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Writes to a sibling temporary, flushes it to disk and replaces the
        /// destination with MoveFileExW(REPLACE_EXISTING|WRITE_THROUGH).
        /// Partial content is never visible; only its own temp is removed on
        /// failure.
        /// </summary>
        private void AtomicWrite(string path, byte[] data)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string temporary = Path.Combine(
                directory ?? string.Empty,
                "." + Path.GetFileName(path) + ".titella-" + RandomSuffix());
            try
            {
                using (FileStream stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                if (!NativeMethods.MoveFileExW(
                        temporary, path,
                        Win32Constants.MOVEFILE_REPLACE_EXISTING |
                            Win32Constants.MOVEFILE_WRITE_THROUGH))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "MoveFileExW failed with {0}",
                            error));
                }
            }
            catch
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
                    // The own temp is best-effort cleanup; the original error wins.
                }
                throw;
            }
        }

        public IDictionary<string, object> Read(string path, int offset, int length,
                                                out byte[] payload)
        {
            string target = PathOf(path);
            offset = Math.Max(0, offset);
            length = Math.Min(Math.Max(1, length), FileLimits.MaxReadBytes);
            FileInfo info = new FileInfo(target);
            long size = info.Length;
            byte[] data;
            using (FileStream stream = new FileStream(
                target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(offset, SeekOrigin.Begin);
                long remaining = Math.Max(0, size - offset);
                int wanted = (int)Math.Min(length, remaining);
                data = new byte[wanted];
                int read = 0;
                while (read < wanted)
                {
                    int count = stream.Read(data, read, wanted - read);
                    if (count <= 0)
                    {
                        break;
                    }
                    read += count;
                }
                if (read != wanted)
                {
                    byte[] shorter = new byte[read];
                    Array.Copy(data, shorter, read);
                    data = shorter;
                }
            }
            payload = data;
            IDictionary<string, object> metadata = new Dictionary<string, object>();
            metadata["path"] = target;
            metadata["offset"] = offset;
            metadata["next_offset"] = offset + data.Length;
            metadata["size"] = size;
            metadata["eof"] = offset + data.Length >= size;
            metadata["bytes"] = data.Length;
            return metadata;
        }

        public IDictionary<string, object> Write(string path, byte[] data)
        {
            if (data.Length > FileLimits.MaxWriteBytes)
            {
                throw new ProtocolError(
                    "write_too_large", "remote_fs_write accepts up to 64 KiB", false);
            }
            string target = PathOf(path);
            AtomicWrite(target, data);
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "written";
            result["path"] = target;
            result["bytes"] = data.Length;
            result["sha256"] = Sha256Hex(data);
            return result;
        }

        public IDictionary<string, object> List(string path, int offset, int limit)
        {
            string target = PathOf(path);
            offset = Math.Max(0, offset);
            limit = Math.Min(Math.Max(1, limit), FileLimits.MaxListEntries);
            List<string> entries = new List<string>(Directory.GetFileSystemEntries(target));
            entries.Sort(delegate(string left, string right)
            {
                return string.CompareOrdinal(Path.GetFileName(left), Path.GetFileName(right));
            });
            int start = Math.Min(offset, entries.Count);
            int end = Math.Min(start + limit, entries.Count);
            List<object> page = new List<object>();
            for (int index = start; index < end; index++)
            {
                string entry = entries[index];
                FileAttributes attributes = File.GetAttributes(entry);
                string kind;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    kind = "symlink";
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    kind = "directory";
                }
                else if (File.Exists(entry))
                {
                    kind = "file";
                }
                else
                {
                    kind = "other";
                }
                long size = 0;
                double mtime = 0;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    mtime = Seconds(File.GetLastWriteTimeUtc(entry));
                }
                else
                {
                    FileInfo info = new FileInfo(entry);
                    size = info.Length;
                    mtime = Seconds(info.LastWriteTimeUtc);
                }
                IDictionary<string, object> item = new Dictionary<string, object>();
                item["name"] = Path.GetFileName(entry);
                item["path"] = entry;
                item["type"] = kind;
                item["size"] = size;
                item["mtime"] = mtime;
                page.Add(item);
            }
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["path"] = target;
            result["entries"] = page;
            result["offset"] = offset;
            result["next_offset"] = offset + page.Count;
            result["has_more"] = offset + page.Count < entries.Count;
            result["total"] = entries.Count;
            return result;
        }

        private static double Seconds(DateTime value)
        {
            return (value.ToUniversalTime() - Epoch).TotalSeconds;
        }

        /// <summary>
        /// A directory is never deleted recursively; a reparse-point directory
        /// (junction) is unlinked with RemoveDirectoryW, which removes the link
        /// without touching its target.
        /// </summary>
        public IDictionary<string, object> Delete(string path)
        {
            string target = PathOf(path);
            uint attributes = NativeMethods.GetFileAttributesW(target);
            if (attributes == Win32Constants.INVALID_FILE_ATTRIBUTES)
            {
                throw new ProtocolError(
                    "not_found",
                    "the path does not exist",
                    false);
            }
            bool directory = (attributes & Win32Constants.FILE_ATTRIBUTE_DIRECTORY) != 0;
            bool reparse = (attributes & Win32Constants.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
            if (directory && !reparse)
            {
                throw new ProtocolError(
                    "directory_delete_denied",
                    "delete is not recursive and does not remove directories",
                    false);
            }
            bool removed;
            if (directory && reparse)
            {
                removed = NativeMethods.RemoveDirectoryW(target);
            }
            else
            {
                removed = NativeMethods.DeleteFileW(target);
            }
            if (!removed)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2 || error == 3)
                {
                    throw new ProtocolError("not_found", "the path does not exist", false);
                }
                if (error == 5)
                {
                    throw new ProtocolError("permission_denied", "access denied", false);
                }
                throw new IOException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "delete failed with {0}",
                        error));
            }
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "deleted";
            result["path"] = target;
            return result;
        }

        private static int CountOccurrences(byte[] data, byte[] needle)
        {
            if (needle.Length == 0)
            {
                return 0;
            }
            int count = 0;
            int index = 0;
            while (index <= data.Length - needle.Length)
            {
                bool match = true;
                for (int probe = 0; probe < needle.Length; probe++)
                {
                    if (data[index + probe] != needle[probe])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    count++;
                    index += needle.Length;
                }
                else
                {
                    index++;
                }
            }
            return count;
        }

        public IDictionary<string, object> Replace(string path, string oldValue, string newValue)
        {
            string target = PathOf(path);
            if (string.IsNullOrEmpty(oldValue))
            {
                throw new ProtocolError("invalid_argument", "old must not be empty", false);
            }
            byte[] data = File.ReadAllBytes(target);
            if (data.Length > FileLimits.MaxSearchRead)
            {
                throw new ProtocolError(
                    "file_too_large", "replace accepts files of up to 4 MiB", false);
            }
            byte[] oldBytes = Encoding.UTF8.GetBytes(oldValue);
            byte[] newBytes = Encoding.UTF8.GetBytes(newValue ?? string.Empty);
            int count = CountOccurrences(data, oldBytes);
            if (count != 1)
            {
                throw new ProtocolError(
                    "replace_count",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "replace requires exactly one occurrence; found {0}",
                        count),
                    false);
            }
            int position = IndexOf(data, oldBytes);
            byte[] updated = new byte[data.Length - oldBytes.Length + newBytes.Length];
            Array.Copy(data, 0, updated, 0, position);
            Array.Copy(newBytes, 0, updated, position, newBytes.Length);
            Array.Copy(data, position + oldBytes.Length, updated,
                       position + newBytes.Length,
                       data.Length - position - oldBytes.Length);
            AtomicWrite(target, updated);
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["status"] = "replaced";
            result["path"] = target;
            result["bytes"] = updated.Length;
            return result;
        }

        private static int IndexOf(byte[] data, byte[] needle)
        {
            for (int index = 0; index <= data.Length - needle.Length; index++)
            {
                bool match = true;
                for (int probe = 0; probe < needle.Length; probe++)
                {
                    if (data[index + probe] != needle[probe])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return index;
                }
            }
            return -1;
        }

        public IDictionary<string, object> Search(string path, string query, int limit)
        {
            string target = PathOf(path);
            if (string.IsNullOrEmpty(query))
            {
                throw new ProtocolError("invalid_argument", "query is mandatory", false);
            }
            limit = Math.Min(Math.Max(1, limit), FileLimits.MaxSearchResults);
            byte[] data;
            using (FileStream stream = new FileStream(
                target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int wanted = Math.Min(FileLimits.MaxSearchRead + 1, (int)Math.Min(
                    int.MaxValue, stream.Length + 1));
                data = new byte[wanted];
                int read = 0;
                while (read < wanted)
                {
                    int count = stream.Read(data, read, wanted - read);
                    if (count <= 0)
                    {
                        break;
                    }
                    read += count;
                }
                if (read != wanted)
                {
                    byte[] shorter = new byte[read];
                    Array.Copy(data, shorter, read);
                    data = shorter;
                }
            }
            bool truncated = data.Length > FileLimits.MaxSearchRead;
            if (truncated)
            {
                byte[] shorter = new byte[FileLimits.MaxSearchRead];
                Array.Copy(data, shorter, shorter.Length);
                data = shorter;
            }
            string text = new UTF8Encoding(false, false).GetString(data);
            List<object> results = new List<object>();
            IList<string> lines = SplitLines(text);
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                if (line.IndexOf(query, StringComparison.Ordinal) >= 0)
                {
                    string snippet = line.Length <= FileLimits.MaxLineCharacters
                        ? line
                        : SafePrefix(line, FileLimits.MaxLineCharacters);
                    IDictionary<string, object> item = new Dictionary<string, object>();
                    item["line"] = index + 1;
                    item["text"] = snippet;
                    results.Add(item);
                    if (results.Count >= limit)
                    {
                        break;
                    }
                }
            }
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["path"] = target;
            result["query"] = query;
            result["results"] = results;
            result["truncated"] = truncated || results.Count >= limit;
            return result;
        }

        /// <summary>Truncates to at most count UTF-16 units and drops a lone
        /// trailing high surrogate, which JSON cannot represent.</summary>
        private static string SafePrefix(string line, int count)
        {
            if (line.Length <= count)
            {
                return line;
            }
            int length = count;
            if (char.IsHighSurrogate(line[length - 1]))
            {
                length--;
            }
            return line.Substring(0, length);
        }

        /// <summary>
        /// Python str.splitlines() separators; no trailing empty element. A
        /// terminal separator closes the last line without creating one.
        /// </summary>
        private static IList<string> SplitLines(string text)
        {
            List<string> lines = new List<string>();
            StringBuilder current = new StringBuilder();
            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                    lines.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }
                if (c == '\n' || c == '\v' || c == '\f' || c == '\u001c' ||
                    c == '\u001d' || c == '\u001e' || c == '\u0085' ||
                    c == '\u2028' || c == '\u2029')
                {
                    index++;
                    lines.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }
                current.Append(c);
                index++;
            }
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
            return lines;
        }

        public IDictionary<string, object> Find(string path, string pattern, int limit)
        {
            string target = PathOf(path);
            if (string.IsNullOrEmpty(pattern))
            {
                throw new ProtocolError("invalid_argument", "pattern is mandatory", false);
            }
            limit = Math.Min(Math.Max(1, limit), FileLimits.MaxFindResults);
            List<object> results = new List<object>();
            bool truncated = false;
            FileAttributes attributes = File.GetAttributes(target);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
            if (!isDirectory || isReparse)
            {
                if (GlobMatch(Path.GetFileName(target), pattern))
                {
                    results.Add(target);
                }
            }
            else
            {
                Walk(target, pattern, limit, results, ref truncated);
            }
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["path"] = target;
            result["pattern"] = pattern;
            result["results"] = results;
            result["truncated"] = truncated;
            return result;
        }

        /// <summary>
        /// Top-down walk reproducing os.walk(followlinks=False): at each level
        /// directories are sorted first and matched after the files, then the
        /// sorted subdirectories are visited. Reparse points are listed but
        /// never descended into.
        /// </summary>
        private static void Walk(string root, string pattern, int limit,
                                 List<object> results, ref bool truncated)
        {
            List<string> directories = new List<string>();
            List<string> files = new List<string>();
            foreach (string entry in Directory.GetFileSystemEntries(root))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    directories.Add(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
            directories.Sort(delegate(string left, string right)
            {
                return string.CompareOrdinal(Path.GetFileName(left), Path.GetFileName(right));
            });
            files.Sort(delegate(string left, string right)
            {
                return string.CompareOrdinal(Path.GetFileName(left), Path.GetFileName(right));
            });
            List<string> names = new List<string>(directories.Count + files.Count);
            names.AddRange(directories);
            names.AddRange(files);
            foreach (string entry in names)
            {
                if (GlobMatch(Path.GetFileName(entry), pattern))
                {
                    results.Add(entry);
                    if (results.Count >= limit)
                    {
                        truncated = true;
                        return;
                    }
                }
            }
            foreach (string directory in directories)
            {
                Walk(directory, pattern, limit, results, ref truncated);
                if (truncated)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// fnmatch-style matching over a bare name: * any sequence, ? one
        /// character, [seq]/[!seq] classes, no escape character and no
        /// path-separator special-casing. Matching is ordinal (case
        /// sensitive), which is what the macOS/Linux Python slave exposes.
        /// </summary>
        internal static bool GlobMatch(string name, string pattern)
        {
            return GlobAt(name, 0, pattern, 0);
        }

        private static bool GlobAt(string name, int n, string pattern, int p)
        {
            while (p < pattern.Length)
            {
                char token = pattern[p];
                if (token == '*')
                {
                    while (p + 1 < pattern.Length && pattern[p + 1] == '*')
                    {
                        p++;
                    }
                    if (p + 1 == pattern.Length)
                    {
                        return true;
                    }
                    for (int skip = n; skip <= name.Length; skip++)
                    {
                        if (GlobAt(name, skip, pattern, p + 1))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                if (n >= name.Length)
                {
                    return false;
                }
                if (token == '?')
                {
                    n++;
                    p++;
                    continue;
                }
                if (token == '[')
                {
                    int close = p + 1;
                    bool negate = false;
                    if (close < pattern.Length && pattern[close] == '!')
                    {
                        negate = true;
                        close++;
                    }
                    if (close < pattern.Length && pattern[close] == ']')
                    {
                        close++;
                    }
                    while (close < pattern.Length && pattern[close] != ']')
                    {
                        close++;
                    }
                    if (close >= pattern.Length)
                    {
                        // Unclosed class: the bracket is literal.
                        if (name[n] != '[')
                        {
                            return false;
                        }
                        n++;
                        p++;
                        continue;
                    }
                    bool matched = ClassMatch(name[n], pattern, p + 1, close, negate);
                    if (!matched)
                    {
                        return false;
                    }
                    n++;
                    p = close + 1;
                    continue;
                }
                if (name[n] != token)
                {
                    return false;
                }
                n++;
                p++;
            }
            return n == name.Length;
        }

        private static bool ClassMatch(char value, string pattern, int start, int end, bool negate)
        {
            int index = start;
            if (index < end && pattern[index] == '!')
            {
                index++;
            }
            bool matched = false;
            while (index < end)
            {
                char low = pattern[index];
                if (index + 2 < end && pattern[index + 1] == '-' && pattern[index + 2] != ']')
                {
                    char high = pattern[index + 2];
                    if (value >= low && value <= high)
                    {
                        matched = true;
                    }
                    index += 3;
                    continue;
                }
                if (value == low)
                {
                    matched = true;
                }
                index++;
            }
            return negate ? !matched : matched;
        }

        public IDictionary<string, object> Sysinfo()
        {
            string version = null;
            try
            {
                version = Agent.NdpVersion();
            }
            catch (Exception)
            {
                version = null;
            }
            IDictionary<string, object> runtime = new Dictionary<string, object>();
            runtime["name"] = "dotnet-framework";
            runtime["version"] = version;
            IDictionary<string, object> result = new Dictionary<string, object>();
            result["system"] = "Windows";
            result["release"] = Environment.OSVersion.Version.ToString();
            result["version"] = Environment.OSVersion.VersionString;
            string machine = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            result["machine"] = machine ?? "unknown";
            result["hostname"] = Environment.MachineName;
            result["python"] = null;
            result["uid"] = null;
            result["gid"] = null;
            result["home"] = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            result["runtime"] = runtime;
            return result;
        }
    }
}
