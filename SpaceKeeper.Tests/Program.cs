using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using SpaceKeeper.Core;

internal static class Program
{
    private static string root = "";
    private static int passed, skipped;
    private static readonly WindowsFileInspector inspector = new();
    [STAThread]
    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetFullPath(".scratch"), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Test("完全相同、不同名称；同名不同内容", () =>
            {
                var dir = Folder(); Write(dir, "a.txt", "hello"); Write(dir, "b.txt", "hello"); Write(dir, "sub/a.txt", "other");
                var result = Scan(dir); Assert(result.Groups.Count == 1 && result.Groups[0].Files.Count == 2);
                Assert(result.Files.Count == 3 && result.Folders.First().Bytes == 15);
            });
            Test("空文件不产生清理候选", () => { var dir = Folder(); Write(dir, "a", ""); Write(dir, "b", ""); Assert(Scan(dir).Groups.Count == 0); });
            Test("中文名称与重叠扫描根目录去重", () =>
            {
                var dir = Folder(); Write(dir, "中文/副本.txt", "hello"); Write(dir, "原件.txt", "hello");
                var result = new FileScanner(inspector).Scan([dir, Path.Combine(dir, "中文")], null, default);
                Assert(result.Files.Count == 2 && result.Groups.Count == 1);
            });
            Test("长路径扫描", () =>
            {
                var dir = Folder(); var nested = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('a', 45), 6));
                Write(dir, nested + "/first.txt", "long-path"); Write(dir, "second.txt", "long-path"); Assert(Scan(dir).Groups.Count == 1);
            });
            Test("路径保护不会误伤相似前缀", () => { Assert(Paths.IsWithin(Path.Combine(root, "abc", "x"), Path.Combine(root, "abc"))); Assert(!Paths.IsWithin(Path.Combine(root, "abcd", "x"), Path.Combine(root, "abc"))); });
            Test("默认清单为空；必须保留一份", () =>
            {
                var (result, group) = Pair(); var keepers = Keepers(group);
                Assert(PlanBuilder.Build(result, keepers, [], []).Items.Count == 0);
                Reject(() => PlanBuilder.Build(result, keepers, [group.Files[0].Path], []));
                Reject(() => PlanBuilder.Build(result, new Dictionary<string, string>(), [], []));
            });
            Test("保护目录和未知文件不能清理", () =>
            {
                var (result, group) = Pair(); Reject(() => PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], [Path.GetDirectoryName(group.Files[1].Path)!]));
                Reject(() => PlanBuilder.Build(result, Keepers(group), [Path.Combine(root, "unknown")], []));
            });
            Test("文件变化后跳过，保留副本完整", () =>
            {
                var (result, group) = Pair(); var plan = PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], []);
                File.WriteAllText(group.Files[1].Path, "CHANGED"); var recycler = new FakeRecycler();
                var report = new CleanupService(inspector, recycler, new MemoryJournal()).Execute(plan, null, default);
                Assert(report.Entries[0].Status == "已跳过" && recycler.Paths.Count == 0 && File.Exists(group.Files[0].Path));
            });
            Test("保留副本缺失时拒绝清理", () =>
            {
                var (result, group) = Pair(); var plan = PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], []);
                File.Move(group.Files[0].Path, group.Files[0].Path + ".moved"); var recycler = new FakeRecycler();
                new CleanupService(inspector, recycler, new MemoryJournal()).Execute(plan, null, default); Assert(recycler.Paths.Count == 0);
            });
            Test("两个文件同时变更且日期恢复，内容摘要仍拦截", () =>
            {
                var (result, group) = Pair(); var plan = PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], []);
                foreach (var file in group.Files) { File.WriteAllText(file.Path, "changed"); File.SetLastWriteTimeUtc(file.Path, new DateTime(file.Stamp.ModifiedTicks, DateTimeKind.Utc)); }
                var recycler = new FakeRecycler(); var report = new CleanupService(inspector, recycler, new MemoryJournal()).Execute(plan, null, default);
                Assert(recycler.Paths.Count == 0 && report.Entries[0].Status == "已跳过");
            });
            Test("文件被替换但大小日期相同时仍拒绝", () =>
            {
                var (result, group) = Pair(); var file = group.Files[1]; var plan = PlanBuilder.Build(result, Keepers(group), [file.Path], []);
                File.Move(file.Path, file.Path + ".old"); File.WriteAllText(file.Path, "payload"); File.SetLastWriteTimeUtc(file.Path, new DateTime(file.Stamp.ModifiedTicks, DateTimeKind.Utc));
                var recycler = new FakeRecycler(); new CleanupService(inspector, recycler, new MemoryJournal()).Execute(plan, null, default); Assert(recycler.Paths.Count == 0);
            });
            Test("锁定文件不能扫描或清理", () =>
            {
                var (result, group) = Pair(); using var locked = new FileStream(group.Files[1].Path, FileMode.Open, FileAccess.Read, FileShare.None);
                var scanned = Scan(Path.GetDirectoryName(group.Files[0].Path)!); Assert(scanned.Issues.Count > 0 && scanned.Groups.Count == 0);
                var recycler = new FakeRecycler(); new CleanupService(inspector, recycler, new MemoryJournal()).Execute(PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], []), null, default); Assert(recycler.Paths.Count == 0);
            });
            Test("扫描取消不返回可清理结果", () => { var cts = new CancellationTokenSource(); cts.Cancel(); var dir = Folder(); Reject(() => new FileScanner(inspector).Scan([dir], null, cts.Token)); });
            Test("读取权限被拒绝时跳过并保留文件", () =>
            {
                var dir = Folder(); var path = Write(dir, "denied.txt", "private"); var info = new FileInfo(path);
                var originalAcl = info.GetAccessControl(); var changedAcl = info.GetAccessControl();
                var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
                changedAcl.AddAccessRule(deny); info.SetAccessControl(changedAcl);
                try { var result = Scan(dir); Assert(result.Files.Count == 0 && result.Issues.Count == 1 && File.Exists(path)); }
                finally { info.SetAccessControl(originalAcl); }
            });
            Test("处理取消保留所有待处理项", () =>
            {
                var (result, group) = Pair(); var cts = new CancellationTokenSource(); cts.Cancel(); var recycler = new FakeRecycler();
                var report = new CleanupService(inspector, recycler, new MemoryJournal()).Execute(PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], []), null, cts.Token);
                Assert(report.Entries[0].Status == "已取消" && recycler.Paths.Count == 0);
            });
            Test("硬链接不视为独立重复副本", () =>
            {
                var dir = Folder(); var a = Write(dir, "a", "hello"); var b = Path.Combine(dir, "hardlink");
                Assert(CreateHardLink(b, a, IntPtr.Zero)); var result = Scan(dir); Assert(result.Groups.Count == 0 && result.Issues.Count == 2);
            });
            Test("附加数据流文件被排除", () => { var dir = Folder(); var a = Write(dir, "a", "hello"); Write(dir, "b", "hello"); File.WriteAllText(a + ":extra", "private"); var result = Scan(dir); Assert(result.Groups.Count == 0 && result.Issues.Count == 1); });
            Test("云占位标记文件被排除", () =>
            {
                var dir = Folder(); var a = Write(dir, "offline", "hello"); File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.Offline);
                try { Assert(Scan(dir).Issues.Count == 1); } finally { File.SetAttributes(a, FileAttributes.Normal); }
            });
            Test("系统目录和整盘根被拒绝", () => { Reject(() => inspector.ValidateRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))); Reject(() => inspector.ValidateRoot(Path.GetPathRoot(root)!)); });
            Test("重复身份与互相删除清单被拒绝", () =>
            {
                var (result, group) = Pair(); var forged = new CleanupPlan([new(group.Files[0], group.Files[1]), new(group.Files[1], group.Files[0])], []);
                Reject(() => new CleanupService(inspector, new FakeRecycler(), new MemoryJournal()).Execute(forged, null, default));
            });
            Test("部分失败逐项记录，无永久删除后备", () =>
            {
                var dir = Folder(); Write(dir, "a", "hello"); Write(dir, "b", "hello"); Write(dir, "c", "hello"); var result = Scan(dir); var group = result.Groups.Single();
                var plan = PlanBuilder.Build(result, Keepers(group), group.Files.Skip(1).Select(x => x.Path), []); var recycler = new FakeRecycler { FailFirst = true }; var journal = new MemoryJournal();
                var report = new CleanupService(inspector, recycler, journal).Execute(plan, null, default);
                Assert(report.Entries[0].Status == "已跳过" && report.Entries[1].Status == "已移入回收站" && journal.Entries.Count == 4);
                Assert(group.Files.All(x => File.Exists(x.Path)));
            });
            Test("日志写入失败时不触碰文件", () =>
            {
                var (result, group) = Pair(); var recycler = new FakeRecycler();
                Reject(() => new CleanupService(inspector, recycler, new FailingJournal()).Execute(PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], []), null, default));
                Assert(recycler.Paths.Count == 0);
            });
            Test("设置和日志往返，损坏日志保留警示", () =>
            {
                var dir = Folder(); var store = new LocalStore(dir); store.SaveProtection([root]); Assert(store.LoadProtection().Single() == root);
                store.Append(new(DateTimeOffset.Now, "x", "k", 1, "已跳过", "test")); File.AppendAllText(Path.Combine(dir, "operations.jsonl"), "{bad\n");
                Assert(store.ReadHistory().Count == 2 && store.ReadHistory().First().Status == "记录不完整");
            });
            Test("符号链接目录不被遍历", () =>
            {
                var target = Folder(); Write(target, "a", "hello"); var dir = Folder(); var link = Path.Combine(dir, "link");
                try { Directory.CreateSymbolicLink(link, target); }
                catch (UnauthorizedAccessException) { skipped++; Console.WriteLine("  SKIP: 当前环境未允许创建符号链接"); return; }
                try { var result = Scan(dir); Assert(result.Files.Count == 0 && result.Issues.Count == 1); } finally { Directory.Delete(link); }
            });
            if (args.Contains("--recycle-integration")) Test("真实回收与精确恢复，仅测试目录", RecycleIntegration);
            Console.WriteLine($"RESULT: {passed} checks passed, {skipped} conditional checks skipped. Test files: {root}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
        finally
        {
            var expected = Path.GetFullPath(".scratch") + Path.DirectorySeparatorChar;
            if (root.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).Length == 32)
                try { Directory.Delete(root, recursive: true); } catch (IOException) { Console.WriteLine("测试目录保留，需检查：" + root); }
        }
    }
    private static void RecycleIntegration()
    {
        var (result, group) = Pair(); var plan = PlanBuilder.Build(result, Keepers(group), [group.Files[1].Path], []);
        var journal = new MemoryJournal(); var report = new CleanupService(inspector, new WindowsRecycler(), journal).Execute(plan, null, default);
        var target = group.Files[1].Path; var existedAfter = File.Exists(target);
        if (!existedAfter) RestoreTestFile(target);
        Assert(report.Entries[0].Status == "已移入回收站", string.Join(";", report.Entries.Select(x => x.Detail)));
        Assert(!existedAfter && File.Exists(group.Files[0].Path));
        Assert(File.Exists(target) && File.ReadAllText(target) == "payload", "恢复失败");
    }
    private static void RestoreTestFile(string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            dynamic recycle = shell.NameSpace(10); dynamic items = recycle.Items(); var restored = false;
            for (int i = 0; i < items.Count; i++)
            {
                dynamic item = items.Item(i);
                string deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom");
                string originalName = item.ExtendedProperty("System.FileName");
                if (string.Equals(deletedFrom, Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase) && originalName == Path.GetFileName(target))
                { item.InvokeVerb("undelete"); restored = true; break; }
            }
            Assert(restored, "未找到本测试的回收站项，不操作其他项目");
            for (int i = 0; i < 50 && !File.Exists(target); i++) Thread.Sleep(100);
            Assert(File.Exists(target), "恢复失败");
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS: " + name); }
    private static void Assert(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException) { return; } throw new Exception("Expected rejection"); }
    private static string Folder() { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static string Write(string dir, string relative, string content) { var path = Path.Combine(dir, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
    private static ScanResult Scan(string dir) => new FileScanner(inspector).Scan([dir], null, default);
    private static (ScanResult, DuplicateGroup) Pair() { var dir = Folder(); Write(dir, "keep.txt", "payload"); Write(dir, "copy.txt", "payload"); var result = Scan(dir); return (result, result.Groups.Single()); }
    private static Dictionary<string, string> Keepers(DuplicateGroup group) => new() { [group.Id] = group.Files[0].Path };
    private sealed class MemoryJournal : IOperationJournal { public List<OperationEntry> Entries { get; } = []; public void Append(OperationEntry entry) => Entries.Add(entry); }
    private sealed class FailingJournal : IOperationJournal { public void Append(OperationEntry entry) => throw new IOException("journal unavailable"); }
    private sealed class FakeRecycler : IRecycler
    {
        public bool FailFirst { get; init; } public List<string> Paths { get; } = [];
        public void Recycle(string path, Action validateBeforeRecycle) { validateBeforeRecycle(); Paths.Add(path); if (FailFirst && Paths.Count == 1) throw new IOException("recycle unavailable"); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr attributes);
}
