namespace SpaceKeeper.Core;

public sealed record FileStamp(string Path, long Length, long ModifiedTicks, string Identity);
public sealed record ScannedFile(FileStamp Stamp, string Hash)
{
    public string Path => Stamp.Path;
    public long Length => Stamp.Length;
}
public sealed record DuplicateGroup(string Id, IReadOnlyList<ScannedFile> Files);
public sealed record FolderSize(string Path, long Bytes);
public sealed record ScanIssue(string Path, string Reason);
public sealed record ScanProgress(string Stage, int Files, long Bytes, string CurrentPath);
public sealed record ScanResult(IReadOnlyList<ScannedFile> Files, IReadOnlyList<DuplicateGroup> Groups,
    IReadOnlyList<FolderSize> Folders, IReadOnlyList<ScanIssue> Issues);
public sealed record CleanupItem(ScannedFile Target, ScannedFile Keeper);
public sealed record CleanupPlan(IReadOnlyList<CleanupItem> Items, IReadOnlyList<string> ProtectedFolders)
{
    public long LogicalBytes => Items.Sum(x => x.Target.Length);
}
public sealed record OperationEntry(DateTimeOffset Time, string Target, string Keeper, long Bytes, string Status, string Detail);
public sealed record CleanupReport(IReadOnlyList<OperationEntry> Entries, IReadOnlyDictionary<string, long> ObservedFreeSpaceChanges);

public interface IFileInspector
{
    FileStamp Inspect(string path);
    void ValidateRoot(string path);
}
public interface IRecycler { void Recycle(string path, Action validateBeforeRecycle); }
public interface IOperationJournal { void Append(OperationEntry entry); }

public static class Paths
{
    public static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    public static bool IsWithin(string path, string folder)
    {
        var p = Normalize(path); var f = Normalize(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
