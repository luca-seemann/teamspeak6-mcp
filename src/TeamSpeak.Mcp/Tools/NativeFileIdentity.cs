using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace TeamSpeak.Mcp.Tools;

/// <summary>
/// Asks the operating system where an open file really lives and how many names it has.
/// </summary>
/// <remarks>
/// A path can be checked and then swapped for a link before it is opened, and a hard link looks like
/// any other file by its path. The open handle cannot be swapped: its final path and link count
/// describe the file that will actually be read or written.
/// </remarks>
internal static partial class NativeFileIdentity
{
    /// <summary>Gets the full path the operating system resolves an open file to, links followed.</summary>
    /// <param name="handle">The open file.</param>
    /// <returns>The final path.</returns>
    public static string FinalPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (OperatingSystem.IsWindows())
        {
            return Windows.FinalPath(handle);
        }

        if (OperatingSystem.IsLinux())
        {
            // The kernel keeps the resolved path of every descriptor here.
            return File.ResolveLinkTarget($"/proc/self/fd/{handle.DangerousGetHandle()}", returnFinalTarget: false)?.FullName
                ?? throw new IOException("The operating system did not report where the open file lives.");
        }

        throw new PlatformNotSupportedException("Local file checks are implemented for Windows and Linux.");
    }

    /// <summary>Gets how many directory entries name an open file.</summary>
    /// <param name="handle">The open file.</param>
    /// <returns>The link count; more than 1 means a hard link elsewhere shares its content.</returns>
    public static long LinkCount(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (OperatingSystem.IsWindows())
        {
            return Windows.LinkCount(handle);
        }

        if (OperatingSystem.IsLinux())
        {
            return Linux.LinkCount(handle);
        }

        throw new PlatformNotSupportedException("Local file checks are implemented for Windows and Linux.");
    }

    /// <summary>Gets the final path of a directory, links in it and above it resolved.</summary>
    /// <param name="directory">An existing directory.</param>
    /// <returns>The resolved path.</returns>
    public static string FinalDirectoryPath(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (OperatingSystem.IsWindows())
        {
            return Windows.FinalDirectoryPath(directory);
        }

        if (OperatingSystem.IsLinux())
        {
            return Linux.RealPath(directory);
        }

        throw new PlatformNotSupportedException("Local file checks are implemented for Windows and Linux.");
    }

    [SupportedOSPlatform("windows")]
    private static partial class Windows
    {
        private const uint FileReadAttributes = 0x80;
        private const uint ShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;

        public static unsafe string FinalPath(SafeFileHandle handle)
        {
            var buffer = new char[1024];
            while (true)
            {
                uint length;
                fixed (char* start = buffer)
                {
                    length = GetFinalPathNameByHandleW(handle, start, (uint)buffer.Length, 0);
                }

                if (length == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                if (length < buffer.Length)
                {
                    var path = new string(buffer, 0, (int)length);

                    // \\?\C:\dir becomes C:\dir; \\?\UNC\server\share becomes \\server\share.
                    return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
                        ? @"\\" + path[8..]
                        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
                }

                buffer = new char[length];
            }
        }

        public static long LinkCount(SafeFileHandle handle) =>
            GetFileInformationByHandle(handle, out var information)
                ? information.NumberOfLinks
                : throw new Win32Exception(Marshal.GetLastPInvokeError());

        public static string FinalDirectoryPath(string directory)
        {
            using var handle = CreateFileW(directory, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            return handle.IsInvalid ? throw new Win32Exception(Marshal.GetLastPInvokeError()) : FinalPath(handle);
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        // The FILETIME members are two DWORDs each, so nothing in the native struct is aligned to 8 bytes.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }

    [SupportedOSPlatform("linux")]
    private static partial class Linux
    {
        private const int AtEmptyPath = 0x1000;
        private const uint StatxNlink = 0x4;

        public static long LinkCount(SafeFileHandle handle)
        {
            // statx lays out its buffer the same on every architecture, unlike stat.
            var buffer = new byte[256];
            var result = Statx((int)handle.DangerousGetHandle(), string.Empty, AtEmptyPath, StatxNlink, buffer);
            return result == 0
                ? BitConverter.ToUInt32(buffer, 16)
                : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        public static string RealPath(string path)
        {
            var resolved = RealPathNative(path, IntPtr.Zero);
            if (resolved == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                return Marshal.PtrToStringUTF8(resolved)!;
            }
            finally
            {
                Free(resolved);
            }
        }

        [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int Statx(int directory, string path, int flags, uint mask, [Out] byte[] buffer);

        [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr RealPathNative(string path, IntPtr resolved);

        [LibraryImport("libc", EntryPoint = "free")]
        private static partial void Free(IntPtr pointer);
    }
}