using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace SpaceKeeper.Core;

public sealed class WindowsFileInspector : IFileInspector
{
    private readonly List<string> blocked = [];
    private const FileAttributes Unsupported = FileAttributes.ReparsePoint | FileAttributes.System |
        FileAttributes.Offline | FileAttributes.SparseFile | FileAttributes.Compressed | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    public WindowsFileInspector()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("仅支持 Windows");
        foreach (var variable in new[] { "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            AddBlocked(Environment.GetEnvironmentVariable(variable));
        AddBlocked(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddBlocked(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts != null)
            foreach (var name in accounts.GetSubKeyNames())
            { using var account = accounts.OpenSubKey(name); AddBlocked(account?.GetValue("UserFolder") as string); }
    }

    private void AddBlocked(string? path) { if (!string.IsNullOrWhiteSpace(path)) blocked.Add(Paths.Normalize(path)); }

    public void ValidateRoot(string path)
    {
        path = Paths.Normalize(path);
        if (!Directory.Exists(path)) throw new IOException("目录不存在");
        var root = System.IO.Path.GetPathRoot(path)!;
        if (Paths.Normalize(root).Equals(path, StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择个人文件目录，不支持整盘扫描");
        if (GetDriveType(root) != 3) throw new IOException("首版只支持本机固定磁盘");
        var fs = new char[64];
        if (!GetVolumeInformation(root, null, 0, out _, out _, out _, fs, fs.Length) || new string(fs).TrimEnd('\0') != "NTFS")
            throw new IOException("首版只支持 NTFS 磁盘，以便可靠检查文件身份和回收条件");
        CheckPath(path);
    }

    public void CheckDirectory(string path) => CheckPath(Paths.Normalize(path));

    private void CheckPath(string path)
    {
        if (blocked.Any(x => Paths.IsWithin(path, x))) throw new IOException("系统、软件数据或 OneDrive 目录已排除");
        var cursor = path;
        while (!string.IsNullOrEmpty(cursor))
        {
            var name = System.IO.Path.GetFileName(cursor);
            if (new[] { "$RECYCLE.BIN", "System Volume Information", ".git", "node_modules", "WindowsApps", "Steam", "steamapps" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new IOException("回收站、软件或开发依赖目录已排除");
            var attributes = File.GetAttributes(cursor);
            var mask = Directory.GetParent(cursor) == null ? Unsupported & ~FileAttributes.System : Unsupported;
            if ((attributes & mask) != 0) throw new IOException("链接、云占位、系统、压缩或稀疏文件已排除");
            cursor = Directory.GetParent(cursor)?.FullName;
        }
        // Cloud Files API detects registered sync roots, including locally available files.
        var info = new byte[4096];
        var cloudResult = CfGetSyncRootInfoByPath(path, 0, info, info.Length, out _);
        if (cloudResult >= 0 || cloudResult == unchecked((int)0x8007007A)) throw new IOException("云同步目录已排除，避免同步删除");
    }

    public FileStamp Inspect(string path)
    {
        path = Paths.Normalize(path); ValidateRoot(System.IO.Path.GetDirectoryName(path)!);
        CheckPath(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("不是普通文件");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) throw new Win32Exception();
        if (info.NumberOfLinks != 1) throw new IOException("硬链接文件已排除，避免错误计算空间");
        var handle = FindFirstStream(path, 0, out var data, 0);
        if (handle == new IntPtr(-1)) throw new IOException("无法可靠检查文件数据流");
        try
        {
            if (data.Name != "::$DATA") throw new IOException("多数据流文件已排除");
            if (FindNextStream(handle, out _)) throw new IOException("多数据流文件已排除");
            if (Marshal.GetLastWin32Error() != 38) throw new IOException("数据流检查未完成");
        }
        finally { FindClose(handle); }
        var file = new FileInfo(path);
        return new FileStamp(path, file.Length, file.LastWriteTimeUtc.Ticks,
            $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StreamData
    { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, int flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextStream(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformation(string root, char[]? name, int length, out uint serial, out uint max, out uint flags, char[] fs, int fsLength);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfGetSyncRootInfoByPath(string path, int infoClass, byte[] buffer, int length, out int returned);
}
