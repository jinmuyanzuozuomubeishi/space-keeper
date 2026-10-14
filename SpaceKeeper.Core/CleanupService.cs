namespace SpaceKeeper.Core;

public static class PlanBuilder
{
    public static CleanupPlan Build(ScanResult result, IReadOnlyDictionary<string, string> keepers,
        IEnumerable<string> selected, IEnumerable<string> protectedFolders)
    {
        var protection = protectedFolders.Select(Paths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var wanted = selected.Select(Paths.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<CleanupItem>();
        foreach (var group in result.Groups)
        {
            if (!keepers.TryGetValue(group.Id, out var keeperPath)) throw new ArgumentException("每组必须指定保留文件");
            var keeper = group.Files.SingleOrDefault(x => x.Path.Equals(keeperPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("保留文件不在重复组内");
            foreach (var file in group.Files.Where(x => wanted.Contains(x.Path)))
            {
                if (file.Path.Equals(keeper.Path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能清理保留文件");
                if (protection.Any(x => Paths.IsWithin(file.Path, x))) throw new ArgumentException("受保护目录中的文件不能清理");
                if (file.Stamp.Identity == keeper.Stamp.Identity) throw new ArgumentException("同一文件身份不能当作独立副本");
                items.Add(new(file, keeper)); wanted.Remove(file.Path);
            }
        }
        if (wanted.Count > 0) throw new ArgumentException("清理项不属于已验证的重复组");
        return new(items.AsReadOnly(), Array.AsReadOnly(protection));
    }
}

public sealed class CleanupService(IFileInspector inspector, IRecycler recycler, IOperationJournal journal)
{
    public CleanupReport Execute(CleanupPlan plan, IProgress<OperationEntry>? progress, CancellationToken token)
    {
        var entries = new List<OperationEntry>();
        var targets = plan.Items.Select(x => x.Target.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.Items.Any(x => targets.Contains(x.Keeper.Path))) throw new ArgumentException("保留文件也出现在清理清单中");
        if (targets.Count != plan.Items.Count) throw new ArgumentException("清理清单存在重复路径");
        var drives = plan.Items.Select(x => System.IO.Path.GetPathRoot(x.Target.Path)!).Distinct().ToArray();
        var before = drives.ToDictionary(x => x, x => new DriveInfo(x).AvailableFreeSpace);
        foreach (var item in plan.Items)
        {
            string status; string detail;
            if (token.IsCancellationRequested) { status = "已取消"; detail = "尚未处理"; }
            else
            {
                try
                {
                    if (plan.ProtectedFolders.Any(x => Paths.IsWithin(item.Target.Path, x))) throw new IOException("受保护文件已跳过");
                    if (item.Target.Stamp.Identity == item.Keeper.Stamp.Identity) throw new IOException("同一文件身份已跳过");
                    using var keeperGuard = new FileStream(item.Keeper.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var targetGuard = new FileStream(item.Target.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (!FileScanner.ContentEqual(item.Target, item.Keeper, inspector, token)) throw new IOException("副本内容不再相同，请重新扫描");
                    var pending = new OperationEntry(DateTimeOffset.Now, item.Target.Path, item.Keeper.Path, item.Target.Length, "准备回收", "已重新验证，操作尚未确认");
                    journal.Append(pending);
                    token.ThrowIfCancellationRequested();
                    recycler.Recycle(item.Target.Path, () =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (!FileScanner.ContentEqual(item.Target, item.Keeper, inspector, token)) throw new IOException("回收前最终校验失败");
                    });
                    status = "已移入回收站"; detail = "可在 Windows 回收站恢复；尚未清空回收站";
                }
                catch (OperationCanceledException) { status = "已取消"; detail = "操作取消，未开始的项目不再处理"; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                { status = File.Exists(item.Target.Path) ? "已跳过" : "需检查"; detail = e.Message; }
            }
            var entry = new OperationEntry(DateTimeOffset.Now, item.Target.Path, item.Keeper.Path, item.Target.Length, status, detail);
            journal.Append(entry); entries.Add(entry); progress?.Report(entry);
        }
        var change = drives.ToDictionary(x => x, x => new DriveInfo(x).AvailableFreeSpace - before[x]);
        return new(entries, change);
    }
}
