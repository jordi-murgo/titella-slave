// Win32 interop for the Titella Windows slave: serial CDC, job-object
// process containment, suspended CreateProcessW with ConPTY attributes,
// and atomic replace/delete primitives. Pure declarations plus thin RAII
// wrappers; no protocol policy lives here. Compiled as C# 5 on the
// .NET Framework 4.7.2 in-memory compiler of PowerShell 5.1 Add-Type.

namespace Titella.Slave
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>Win32 constants shared by several units.</summary>
    internal static class Win32Constants
    {
        // CreateFileW
        public const uint GENERIC_READ = 0x80000000u;
        public const uint GENERIC_WRITE = 0x40000000u;
        public const uint OPEN_EXISTING = 3u;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080u;
        public const uint INVALID_ERROR = 0xFFFFFFFFu;

        // Serial
        public const uint CBR_2000000 = 2000000u;
        public const uint NOPARITY = 0u;
        public const byte ONESTOPBIT = 0;
        public const uint MAXDWORD = 0xFFFFFFFFu;
        public const uint SETRTS = 3u;
        public const uint SETDTR = 5u;

        // COMMTIMEOUTS poll recipe: ReadFile returns TRUE with zero bytes
        // after ReadTotalTimeoutConstant instead of blocking forever.
        public const uint READ_POLL_MS = 100u;
        public const uint WRITE_TOTAL_MS = 2000u;

        // Win32 errors that mean "the CDC went away" for the reader loop.
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_BROKEN_PIPE = 109;
        public const int ERROR_BAD_COMMAND = 22;
        public const int ERROR_GEN_FAILURE = 31;
        public const int ERROR_OPERATION_ABORTED = 995;
        public const int ERROR_DEVICE_NOT_CONNECTED = 1167;

        // Job objects
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000u;
        public const uint STATUS_CONTROL_C_EXIT = 0xC000013Au;

        // CreateProcessW
        public const uint CREATE_SUSPENDED = 0x00000004u;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400u;
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000u;
        public const uint CREATE_NO_WINDOW = 0x08000000u;
        public const int STARTF_USESTDHANDLES = 0x00000100;
        public const uint HANDLE_FLAG_INHERIT = 0x00000001u;
        public const uint WAIT_OBJECT_0 = 0u;
        public const uint WAIT_TIMEOUT = 258u;
        public const uint INFINITE = 0xFFFFFFFFu;

        // ProcThreadAttribute (IntPtr is not a legal const type in C#; static
        // readonly keeps the same single instance per AppDomain).
        public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        // MoveFileExW / attributes
        public const uint MOVEFILE_REPLACE_EXISTING = 0x00000001u;
        public const uint MOVEFILE_WRITE_THROUGH = 0x00000008u;
        public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFFu;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010u;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400u;

        // Screen capture (GetSystemMetrics)
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        // Clipboard: CF_UNICODETEXT payloads carried over GMEM_MOVEABLE
        // global allocations, the only form SetClipboardData accepts.
        public const uint CF_UNICODETEXT = 13u;
        public const uint GMEM_MOVEABLE = 0x0002u;
        public const uint GMEM_ZEROINIT = 0x0040u;

        // DPI awareness context (IntPtr is not a legal const type in C#;
        // PER_MONITOR_AWARE_V2 is the documented (HANDLE)-4).
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);
    }

    /// <summary>Native structures. All layout-critical; keep Pack defaults.</summary>
    internal static class Win32Structs
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DCB
        {
            public uint DCBlength;      // sizeof(DCB)
            public uint BaudRate;       // CBR_2000000
            public uint Flags;          // bitfield dword; fBinary must be set
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;       // 8
            public byte Parity;        // NOPARITY
            public byte StopBits;       // ONESTOPBIT
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;         // MAXDWORD
            public uint ReadTotalTimeoutMultiplier;  // MAXDWORD
            public uint ReadTotalTimeoutConstant;    // 100 ms poll
            public uint WriteTotalTimeoutMultiplier; // 0
            public uint WriteTotalTimeoutConstant;  // 2000 ms
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct COMSTAT
        {
            public uint Flags;
            public uint cbInQue;
            public uint cbOutQue;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFOW
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFOW StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SECURITY_ATTRIBUTES
        {
            public uint nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct COORD
        {
            public short X;
            public short Y;
        }
    }

    /// <summary>Raw P/Invoke declarations. SetLastError is mandatory on
    /// every call whose failure becomes a protocol error; failures are read
    /// with Marshal.GetLastWin32Error(), never a GetLastError import.</summary>
    internal static class NativeMethods
    {
        // ---- Files and devices -------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(
            out IntPtr hReadPipe, out IntPtr hWritePipe,
            IntPtr lpPipeAttributes, uint nSize);

        // ---- Serial ------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetCommState(IntPtr hFile, ref Win32Structs.DCB lpDCB);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetCommState(IntPtr hFile, ref Win32Structs.DCB lpDCB);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetCommTimeouts(IntPtr hFile, ref Win32Structs.COMMTIMEOUTS lpCommTimeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetupComm(IntPtr hFile, uint dwInQueue, uint dwOutQueue);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ClearCommError(IntPtr hFile, out uint lpErrors, out Win32Structs.COMSTAT lpStat);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool EscapeCommFunction(IntPtr hFile, uint dwFunc);

        // ---- Job objects -------------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        // ---- Processes ---------------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcessW(
            string lpApplicationName, System.Text.StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref Win32Structs.STARTUPINFOEX lpStartupInfo,
            out Win32Structs.PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList, uint dwAttributeCount, uint dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
            IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        // ---- ConPTY -------------------------------------------------------------

        // HRESULT (not BOOL): negative means failure.
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(
            Win32Structs.COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(IntPtr hPC, Win32Structs.COORD size);

        [DllImport("kernel32.dll")]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FreeConsole();

        // ---- Filesystem --------------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool RemoveDirectoryW(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool DeleteFileW(string lpFileName);

        // ---- Desktop -----------------------------------------------------------

        // Returns the previous context; NULL means the requested one was not
        // applied. Available since Windows 10 1809.
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        // ---- Clipboard ---------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetClipboardData(uint uFormat);

        // On success the system owns hMem; the caller must not free it.
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterClipboardFormatW(string lpszFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern UIntPtr GlobalSize(IntPtr hMem);
    }

    /// <summary>
    /// A job object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
    /// Closing the handle terminates every process still assigned to the
    /// job, including whole descendant trees; that is the only tree-kill
    /// mechanism this agent uses. Dispose is idempotent.
    /// </summary>
    internal sealed class ProcessJob : IDisposable
    {
        private IntPtr _handle;

        public ProcessJob()
        {
            IntPtr handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                throw new ProtocolError(
                    "process_containment_failed",
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "CreateJobObjectW failed with {0}",
                        Marshal.GetLastWin32Error()),
                    false);
            }
            Win32Structs.JOBOBJECT_EXTENDED_LIMIT_INFORMATION info =
                new Win32Structs.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags =
                Win32Constants.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(Win32Structs.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr block = IntPtr.Zero;
            try
            {
                block = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(info, block, false);
                if (!NativeMethods.SetInformationJobObject(
                        handle, Win32Constants.JobObjectExtendedLimitInformation,
                        block, (uint)size))
                {
                    throw new ProtocolError(
                        "process_containment_failed",
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "SetInformationJobObject failed with {0}",
                            Marshal.GetLastWin32Error()),
                        false);
                }
            }
            catch
            {
                NativeMethods.CloseHandle(handle);
                throw;
            }
            finally
            {
                if (block != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(block);
                }
            }
            _handle = handle;
        }

        public IntPtr Handle
        {
            get { return _handle; }
        }

        /// <summary>
        /// Assigns a suspended process to this job and resumes it. Mandated
        /// order: the child never executes user code unless containment is
        /// proven. On failure the child is terminated here and
        /// process_containment_failed is raised; there is no PID-only
        /// fallback.
        /// </summary>
        public void AssignAndResume(IntPtr hProcess, IntPtr hThread)
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, hProcess))
            {
                int error = Marshal.GetLastWin32Error();
                NativeMethods.TerminateProcess(hProcess, 1);
                throw new ProtocolError(
                    "process_containment_failed",
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "AssignProcessToJobObject failed with {0}",
                        error),
                    false);
            }
            if (NativeMethods.ResumeThread(hThread) == 0xFFFFFFFFu)
            {
                int error = Marshal.GetLastWin32Error();
                NativeMethods.TerminateProcess(hProcess, 1);
                throw new ProtocolError(
                    "process_containment_failed",
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "ResumeThread failed with {0}",
                        error),
                    false);
            }
        }

        public void Terminate(uint exitCode)
        {
            IntPtr handle = _handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.TerminateJobObject(handle, exitCode);
            }
        }

        public void Dispose()
        {
            IntPtr handle = System.Threading.Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// One-shot capability probe run before the agent reports ready:
    /// ConPTY must exist in kernel32 and KILL_ON_JOB_CLOSE jobs must be
    /// settable. Returns null when the platform can serve the full
    /// protocol; a partial platform is reported, never papered over.
    /// </summary>
    internal static class Capabilities
    {
        public static ProtocolError Probe()
        {
            IntPtr kernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero ||
                NativeMethods.GetProcAddress(kernel32, "CreatePseudoConsole") == IntPtr.Zero)
            {
                return new ProtocolError(
                    "platform_unsupported",
                    "ConPTY is not available; Windows 10 1809 or later is required",
                    false);
            }
            ProcessJob probe = null;
            try
            {
                probe = new ProcessJob();
            }
            catch (ProtocolError error)
            {
                return error;
            }
            finally
            {
                if (probe != null)
                {
                    probe.Dispose();
                }
            }
            return null;
        }
    }
}
