using System.Diagnostics;
using System.Security.Cryptography;

namespace SpaceKeeper.Core;

public sealed class FileScanner(IFileInspector inspector)
{
    public ScanResult Scan(IEnumerable<string> folders, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var roots = folders.Select(Paths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0) throw new ArgumentException("请先选择目录");
        roots = roots.Where(x => !roots.Any(y => !x.Equals(y, StringComparison.OrdinalIgnoreCase) && Paths.IsWithin(x, y))).ToList();
        foreach (var root in roots) inspector.ValidateRoot(root);
        var files = new List<ScannedFile>(); var issues = new List<ScanIssue>();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var clock = Stopwatch.StartNew(); long bytes = 0;
        void Report(string stage, string path, bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds < 100) return;
            progress?.Report(new(stage, files.Count, bytes, path)); clock.Restart();
        }
        foreach (var root in roots)
        {
            var queue = new Stack<string>(); queue.Push(root);
            while (queue.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (inspector is WindowsFileInspector windows) windows.CheckDirectory(directory);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested(); Report("枚举文件", path);
                        try
                        {
                            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                            {
                                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { issues.Add(new(path, "链接目录已排除")); continue; }
                                queue.Push(path); continue;
                            }
                            var stamp = inspector.Inspect(path); files.Add(new(stamp, "")); bytes += stamp.Length;
                            var parent = System.IO.Path.GetDirectoryName(stamp.Path);
                            while (parent != null && Paths.IsWithin(parent, root))
                            { sizes[parent] = sizes.GetValueOrDefault(parent) + stamp.Length; parent = Directory.GetParent(parent)?.FullName; }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                        { issues.Add(new(path, e.Message)); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { issues.Add(new(directory, e.Message)); }
            }
        }
        var hashed = new List<ScannedFile>();
        var hashBuffer = new byte[1024 * 1024];
        foreach (var sizeGroup in files.Where(x => x.Length > 0).GroupBy(x => x.Length).Where(x => x.Count() > 1))
            foreach (var file in sizeGroup)
            {
                token.ThrowIfCancellationRequested(); Report("校验内容", file.Path);
                try
                {
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    CheckUnchanged(file, inspector);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int read;
                    while ((read = stream.Read(hashBuffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(hashBuffer, 0, read); Report("校验内容", file.Path); }
                    CheckUnchanged(file, inspector);
                    hashed.Add(file with { Hash = Convert.ToHexString(hash.GetHashAndReset()) });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { issues.Add(new(file.Path, e.Message)); }
            }
        var groups = new List<DuplicateGroup>();
        foreach (var candidate in hashed.GroupBy(x => (x.Length, x.Hash)).Where(x => x.Count() > 1))
        {
            var verified = new List<List<ScannedFile>>();
            foreach (var file in candidate)
            {
                token.ThrowIfCancellationRequested(); Report("逐字节确认", file.Path);
                try
                {
                    CheckUnchanged(file, inspector);
                    var match = verified.FirstOrDefault(g => ContentEqual(g[0], file, inspector, token));
                    if (match == null) verified.Add([file]); else match.Add(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { issues.Add(new(file.Path, e.Message)); }
            }
            foreach (var group in verified.Where(x => x.Count > 1)) groups.Add(new($"G{groups.Count + 1}", group));
        }
        Report("扫描完成", "", true);
        return new(files, groups, sizes.Select(x => new FolderSize(x.Key, x.Value)).OrderByDescending(x => x.Bytes).ToList(), issues);
    }

    public static void CheckUnchanged(ScannedFile file, IFileInspector inspector)
    {
        if (inspector.Inspect(file.Path) != file.Stamp) throw new IOException("文件扫描后发生变化，请重新扫描");
    }

    public static bool ContentEqual(ScannedFile first, ScannedFile second, IFileInspector inspector, CancellationToken token)
    {
        using var a = new FileStream(first.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = new FileStream(second.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckUnchanged(first, inspector); CheckUnchanged(second, inspector);
        var x = new byte[64 * 1024]; var y = new byte[x.Length];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var nx = a.ReadAtLeast(x, x.Length, throwOnEndOfStream: false);
            var ny = b.ReadAtLeast(y, y.Length, throwOnEndOfStream: false);
            if (nx != ny || !x.AsSpan(0, nx).SequenceEqual(y.AsSpan(0, ny))) return false;
            if (nx == 0) break;
            hash.AppendData(x, 0, nx);
        }
        CheckUnchanged(first, inspector); CheckUnchanged(second, inspector);
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        return digest == first.Hash && digest == second.Hash;
    }
}
