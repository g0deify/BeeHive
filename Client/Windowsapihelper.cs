using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Client {
    public static class WindowsAPIHelper {
        // ================= FILE OPERATIONS =================

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CopyFile(string lpExistingFileName, string lpNewFileName, bool bFailIfExists);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteFile(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFile(string lpExistingFileName, string lpNewFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            IntPtr hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            IntPtr hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        // ================= DIRECTORY OPERATIONS =================

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateDirectory(string lpPathName, IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool RemoveDirectory(string lpPathName);

        // ================= SYSTEM INFORMATION =================

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetSystemDirectory(StringBuilder lpBuffer, uint uSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetWindowsDirectory(StringBuilder lpBuffer, uint uSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetComputerName(StringBuilder lpBuffer, ref uint lpnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        // ================= PROCESS OPERATIONS =================

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcesses(
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U4)]
            [In][Out] uint[] processIds,
            uint arraySizeBytes,
            out uint bytesCopied);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleBaseName(
            IntPtr hProcess,
            IntPtr hModule,
            StringBuilder lpBaseName,
            uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessMemoryInfo(
            IntPtr hProcess,
            out PROCESS_MEMORY_COUNTERS counters,
            uint size);

        [StructLayout(LayoutKind.Sequential, Size = 40)]
        public struct PROCESS_MEMORY_COUNTERS {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        // ================= SCREEN CAPTURE =================

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(
            IntPtr hdcDest,
            int xDest,
            int yDest,
            int wDest,
            int hDest,
            IntPtr hdcSource,
            int xSrc,
            int ySrc,
            CopyPixelOperation rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        public enum CopyPixelOperation : int {
            SRCCOPY = 0x00CC0020
        }

        // ================= PRIVILEGE OPERATIONS =================

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            IntPtr ProcessHandle,
            uint DesiredAccess,
            out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(
            IntPtr TokenHandle,
            TOKEN_INFORMATION_CLASS TokenInformationClass,
            IntPtr TokenInformation,
            uint TokenInformationLength,
            out uint ReturnLength);

        public enum TOKEN_INFORMATION_CLASS {
            TokenUser = 1,
            TokenGroups,
            TokenPrivileges,
            TokenOwner,
            TokenPrimaryGroup,
            TokenDefaultDacl,
            TokenSource,
            TokenType,
            TokenImpersonationLevel,
            TokenStatistics,
            TokenRestrictedSids,
            TokenSessionId,
            TokenGroupsAndPrivileges,
            TokenSessionReference,
            TokenSandBoxInert,
            TokenAuditPolicy,
            TokenOrigin,
            TokenElevationType,
            TokenLinkedToken,
            TokenElevation,
            TokenHasRestrictions,
            TokenAccessInformation,
            TokenVirtualizationAllowed,
            TokenVirtualizationEnabled,
            TokenIntegrityLevel,
            TokenUIAccess,
            TokenMandatoryPolicy,
            TokenLogonSid,
            MaxTokenInfoClass
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_ELEVATION {
            public uint TokenIsElevated;
        }

        // Constants
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint CREATE_NEW = 1;
        public const uint CREATE_ALWAYS = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint OPEN_ALWAYS = 4;
        public const uint TRUNCATE_EXISTING = 5;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint INVALID_HANDLE_VALUE = 0xFFFFFFFF;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;

        // ================= HELPER METHODS =================

        public static bool IsElevated() {
            IntPtr tokenHandle;
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, TOKEN_QUERY, out tokenHandle)) {
                return false;
            }

            try {
                TOKEN_ELEVATION elevation = new TOKEN_ELEVATION();
                uint size = (uint)Marshal.SizeOf(elevation);
                IntPtr elevationPtr = Marshal.AllocHGlobal((int)size);

                try {
                    if (GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenElevation, elevationPtr, size, out size)) {
                        elevation = (TOKEN_ELEVATION)Marshal.PtrToStructure(elevationPtr, typeof(TOKEN_ELEVATION));
                        return elevation.TokenIsElevated != 0;
                    }
                }
                finally {
                    Marshal.FreeHGlobal(elevationPtr);
                }
            }
            finally {
                CloseHandle(tokenHandle);
            }

            return false;
        }

        public static string ReadFileContent(string filePath) {
            IntPtr hFile = CreateFile(
                filePath,
                GENERIC_READ,
                FILE_SHARE_READ,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (hFile.ToInt64() == -1) {
                throw new IOException($"Cannot open file: {filePath}");
            }

            try {
                FileInfo fi = new FileInfo(filePath);
                byte[] buffer = new byte[fi.Length];
                uint bytesRead;

                if (!ReadFile(hFile, buffer, (uint)buffer.Length, out bytesRead, IntPtr.Zero)) {
                    throw new IOException("Failed to read file");
                }

                return Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
            }
            finally {
                CloseHandle(hFile);
            }
        }

        public static void WriteFileContent(string filePath, byte[] content) {
            IntPtr hFile = CreateFile(
                filePath,
                GENERIC_WRITE,
                0,
                IntPtr.Zero,
                CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (hFile.ToInt64() == -1) {
                throw new IOException($"Cannot create file: {filePath}");
            }

            try {
                uint bytesWritten;
                if (!WriteFile(hFile, content, (uint)content.Length, out bytesWritten, IntPtr.Zero)) {
                    throw new IOException("Failed to write file");
                }
            }
            finally {
                CloseHandle(hFile);
            }
        }
    }
}