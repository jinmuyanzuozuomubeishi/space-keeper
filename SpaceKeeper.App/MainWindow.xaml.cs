using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SpaceKeeper.Core;

namespace SpaceKeeper.App;

public partial class MainWindow : Window
{
    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<string> ProtectedFolders { get; } = [];
    private readonly WindowsFileInspector inspector = new();
    private readonly LocalStore store;
    private readonly Dictionary<string, string> keepers = new();
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private ScanResult? scan;
    private CleanupPlan? preview;
    private CancellationTokenSource? cancellation;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent(); DataContext = this;
        store = new LocalStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceKeeper"));
        try { foreach (var path in store.LoadProtection()) ProtectedFolders.Add(Paths.Normalize(path)); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or ArgumentException)
        { MessageBox.Show("保护设置未能读取，原设置未改动。请重新添加需要保护的目录。\n" + e.Message, "设置提示"); }
        RefreshHistory();
#if DEBUG
        Loaded += async (_, _) => await RunUiSmokeIfRequested();
#endif
    }

    private string? ChooseFolder(string title)
    {
        var picker = new OpenFolderDialog { Title = title, Multiselect = false };
        return picker.ShowDialog(this) == true ? Paths.Normalize(picker.FolderName) : null;
    }
    private void AddRoot(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("选择需要扫描的个人文件目录"); if (path == null) return;
        try { inspector.ValidateRoot(path); if (!Roots.Contains(path, StringComparer.OrdinalIgnoreCase)) Roots.Add(path); InvalidateResults(); }
        catch (Exception error) { MessageBox.Show(error.Message, "无法扫描此目录"); }
    }
    private void RemoveRoot(object sender, RoutedEventArgs e)
    { if (RootsList.SelectedItem is string path) { Roots.Remove(path); InvalidateResults(); } }
    private void AddProtection(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("选择只扫描、不清理的受保护目录"); if (path == null) return;
        if (!ProtectedFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) ProtectedFolders.Add(path);
        foreach (var file in selected.ToArray()) if (IsProtected(file)) selected.Remove(file);
        SaveProtection(); InvalidatePreview(); RenderCards(); UpdateSelection();
    }
    private void RemoveProtection(object sender, RoutedEventArgs e)
    { if (ProtectionList.SelectedItem is string path) { ProtectedFolders.Remove(path); SaveProtection(); InvalidatePreview(); RenderCards(); } }
    private void SaveProtection()
    {
        try { store.SaveProtection(ProtectedFolders); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show("当前保护规则仍生效，但未保存，下次启动需重新添加。\n" + e.Message); }
    }
    private bool IsProtected(string path) => ProtectedFolders.Any(x => Paths.IsWithin(path, x));
    private void InvalidatePreview() { preview = null; ExecuteButton.IsEnabled = false; PreviewGrid.ItemsSource = null; PreviewText.Text = "选择已变化，请重新生成清理预览。"; }
    private void InvalidateResults()
    {
        scan = null; selected.Clear(); keepers.Clear(); GroupsList.ItemsSource = null; FileCards.Children.Clear();
        FoldersGrid.ItemsSource = null; LargeFilesGrid.ItemsSource = null; IssuesGrid.ItemsSource = null;
        OverviewText.Text = "目录已变化，请重新扫描。"; InvalidatePreview(); UpdateSelection();
    }
    private void SetBusy(bool value)
    {
        busy = value; ScanButton.IsEnabled = !value; AddRootButton.IsEnabled = !value; RemoveRootButton.IsEnabled = !value;
        AddProtectionButton.IsEnabled = !value; RemoveProtectionButton.IsEnabled = !value; PreviewButton.IsEnabled = !value;
        FileCards.IsEnabled = !value; GroupsList.IsEnabled = !value; ExecuteButton.IsEnabled = !value && preview?.Items.Count > 0;
        CancelButton.IsEnabled = value; Progress.IsIndeterminate = value;
    }
    private async void StartScan(object sender, RoutedEventArgs e) => await ScanAsync();
    private async Task ScanAsync()
    {
        if (busy) return;
        if (Roots.Count == 0) { MessageBox.Show("请先选择扫描目录。", "空间守护"); return; }
        InvalidateResults(); cancellation = new(); SetBusy(true); var roots = Roots.ToArray();
        try
        {
            var progress = new Progress<ScanProgress>(x => StatusText.Text = $"{x.Stage} · {x.Files:N0} 个文件 · {Size(x.Bytes)} · {x.CurrentPath}");
            scan = await Task.Run(() => new FileScanner(inspector).Scan(roots, progress, cancellation.Token));
            foreach (var group in scan.Groups)
                keepers[group.Id] = group.Files.OrderByDescending(x => IsProtected(x.Path)).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).First().Path;
            GroupsList.ItemsSource = scan.Groups.Select(x => new GroupView(x, $"{x.Id}  ·  {x.Files.Count} 份\n副本总大小 {Size((x.Files.Count - 1) * x.Files[0].Length)}")).ToList();
            if (scan.Groups.Count > 0) GroupsList.SelectedIndex = 0;
            FoldersGrid.ItemsSource = scan.Folders.Take(100).Select(x => new { x.Path, Size = Size(x.Bytes) }).ToList();
            LargeFilesGrid.ItemsSource = scan.Files.OrderByDescending(x => x.Length).Take(100).Select(x => new LargeFileView(x.Path, Path.GetFileName(x.Path), Size(x.Length))).ToList();
            IssuesGrid.ItemsSource = scan.Issues;
            OverviewText.Text = $"已扫描 {scan.Files.Count:N0} 个文件 · {Size(scan.Files.Sum(x => x.Length))} · {scan.Groups.Count:N0} 组完全重复 · 跳过 {scan.Issues.Count:N0} 项";
            StatusText.Text = "扫描完成。占用统计仅包含支持的文件，跳过项不计入；结果不是清理授权。";
            UpdateSelection();
        }
        catch (OperationCanceledException) { InvalidateResults(); StatusText.Text = "扫描已取消，没有处理任何文件。"; }
        catch (Exception e) { InvalidateResults(); StatusText.Text = "扫描未完成：" + e.Message; }
        finally { SetBusy(false); cancellation.Dispose(); cancellation = null; }
    }
    private void CancelTask(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StatusText.Text = "正在取消，已完成的回收操作不会自动恢复。"; }
    private void GroupChanged(object sender, SelectionChangedEventArgs e) => RenderCards();
    private void RenderCards()
    {
        FileCards.Children.Clear();
        if (GroupsList.SelectedItem is not GroupView view) return;
        var group = view.Group;
        foreach (var file in group.Files)
        {
            var isKeeper = keepers[group.Id].Equals(file.Path, StringComparison.OrdinalIgnoreCase);
            var isProtected = IsProtected(file.Path);
            var card = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 229, 220)), BorderThickness = new(1), CornerRadius = new(7), Padding = new(14), Margin = new(0, 0, 0, 10) };
            var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = new(92) }); content.ColumnDefinitions.Add(new());
            var image = Thumbnail(file.Path);
            if (image != null) content.Children.Add(new Image { Source = image, Width = 78, Height = 78, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top });
            else content.Children.Add(new TextBlock { Text = Path.GetExtension(file.Path).ToUpperInvariant(), FontSize = 18, Foreground = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center });
            var stack = new StackPanel(); Grid.SetColumn(stack, 1); content.Children.Add(stack);
            stack.Children.Add(new TextBlock { Text = Path.GetFileName(file.Path), FontWeight = FontWeights.Bold, FontSize = 16 });
            stack.Children.Add(new TextBlock { Text = file.Path, Foreground = Brushes.SlateGray, Margin = new(0, 5, 0, 5) });
            stack.Children.Add(new TextBlock { Text = $"{Size(file.Length)}  ·  修改于 {new DateTime(file.Stamp.ModifiedTicks, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  SHA-256 + 逐字节确认一致" });
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) };
            var keep = new RadioButton { Content = "保留这份", GroupName = group.Id, IsChecked = isKeeper, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 18, 0) };
            System.Windows.Automation.AutomationProperties.SetName(keep, "保留文件 " + file.Path);
            keep.Checked += (_, _) => { keepers[group.Id] = file.Path; selected.Remove(file.Path); InvalidatePreview(); RenderCards(); UpdateSelection(); };
            controls.Children.Add(keep);
            var check = new CheckBox { Content = isProtected ? "受保护，不清理" : "移入回收站", IsEnabled = !isKeeper && !isProtected, IsChecked = selected.Contains(file.Path), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 18, 0) };
            System.Windows.Automation.AutomationProperties.SetName(check, (isProtected ? "受保护文件 " : "选择回收副本 ") + file.Path);
            check.Click += (_, _) => { if (check.IsChecked == true) selected.Add(file.Path); else selected.Remove(file.Path); InvalidatePreview(); UpdateSelection(); };
            controls.Children.Add(check);
            var locate = new Button { Content = "打开所在目录", Padding = new(10, 5, 10, 5), MinHeight = 30 };
            locate.Click += (_, _) => Locate(file.Path); controls.Children.Add(locate);
            stack.Children.Add(controls); card.Child = content; FileCards.Children.Add(card);
        }
    }
    private static BitmapImage? Thumbnail(string path)
    {
        if (!new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" }.Contains(Path.GetExtension(path).ToLowerInvariant())) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 160; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or FileFormatException) { return null; }
    }
    private void UpdateSelection()
    {
        var bytes = scan?.Groups.SelectMany(x => x.Files).Where(x => selected.Contains(x.Path)).Sum(x => x.Length) ?? 0;
        SelectionText.Text = $"已选择 {selected.Count} 份副本 · 文件总大小 {Size(bytes)} · 尚未处理";
    }
    private void ShowPreview(object sender, RoutedEventArgs e)
    {
        if (scan == null) { MessageBox.Show("请先完成扫描。"); return; }
        try
        {
            preview = PlanBuilder.Build(scan, keepers, selected, ProtectedFolders);
            PreviewGrid.ItemsSource = preview.Items.Select(x => new { Target = x.Target.Path, Keeper = x.Keeper.Path, Size = Size(x.Target.Length) }).ToList();
            PreviewText.Text = $"将回收 {preview.Items.Count} 份副本 · 文件总大小 {Size(preview.LogicalBytes)} · 每组至少保留一份";
            ExecuteButton.IsEnabled = preview.Items.Count > 0; Tabs.SelectedIndex = 2;
        }
        catch (Exception error) { InvalidatePreview(); MessageBox.Show(error.Message, "无法生成清理清单"); }
    }
    private async void ExecuteCleanup(object sender, RoutedEventArgs e)
    {
        if (busy || preview == null || preview.Items.Count == 0) return;
        if (MessageBox.Show($"将把 {preview.Items.Count} 份副本（{Size(preview.LogicalBytes)}）移入回收站。\n\n执行前会重新校验。保留副本不处理；文件变化或无法安全回收时跳过。\n回收站清空前通常不会释放空间。\n\n确认继续？", "确认回收副本", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var plan = preview; cancellation = new(); SetBusy(true);
        try
        {
            var progress = new Progress<OperationEntry>(x => StatusText.Text = $"{x.Status} · {x.Target}");
            var report = await OnStaThread(() => new CleanupService(inspector, new WindowsRecycler(), store).Execute(plan, progress, cancellation.Token));
            var moved = report.Entries.Where(x => x.Status == "已移入回收站").ToList();
            HistoryText.Text = $"本次回收 {moved.Count} 份 · {Size(moved.Sum(x => x.Bytes))}。磁盘可用空间变化：" +
                string.Join("；", report.ObservedFreeSpaceChanges.Select(x => $"{x.Key} {(x.Value < 0 ? "−" : "+")}{Size(Math.Abs(x.Value))}")) + "（含其他程序的影响）";
            StatusText.Text = "处理完成，请检查记录。回收站未清空；再次清理前需重新扫描。";
        }
        catch (Exception error) { StatusText.Text = "任务未完成，请检查操作记录、原文件与回收站：" + error.Message; }
        finally
        {
            InvalidateResults(); RefreshHistory(); Tabs.SelectedIndex = 4; SetBusy(false); cancellation.Dispose(); cancellation = null;
        }
    }
    private static Task<T> OnStaThread<T>(Func<T> action)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { source.SetResult(action()); } catch (Exception e) { source.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start(); return source.Task;
    }
    private void RefreshHistory()
    {
        try { HistoryGrid.ItemsSource = store.ReadHistory(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { HistoryText.Text = "无法读取操作记录：" + e.Message; }
    }
    private static void Locate(string path)
    {
        try
        {
            var process = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; process.ArgumentList.Add("/select,"); process.ArgumentList.Add(path); Process.Start(process);
        }
        catch (Exception e) { MessageBox.Show("无法定位文件：" + e.Message); }
    }
    private void OpenLargeFileFolder(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (LargeFilesGrid.SelectedItem is LargeFileView file) Locate(file.Path); }
    private void OpenRecycleBin(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
    private void WindowClosing(object? sender, CancelEventArgs e)
    { if (busy) { e.Cancel = true; MessageBox.Show("任务正在进行，请先取消任务并等待结束，再关闭窗口。"); } }
    private static string Size(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"]; var number = (double)value; var index = 0;
        while (number >= 1024 && index < units.Length - 1) { number /= 1024; index++; }
        return $"{number:0.##} {units[index]}";
    }
    private sealed record GroupView(DuplicateGroup Group, string Label);
    private sealed record LargeFileView(string Path, string Name, string Size);

#if DEBUG
    private async Task RunUiSmokeIfRequested()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length != 4 || args[1] != "--ui-smoke") return;
        try
        {
            Roots.Add(Paths.Normalize(args[2])); await ScanAsync();
            if (scan == null || scan.Groups.Count == 0 || selected.Count != 0) throw new Exception("扫描或默认选择检查失败");
            var group = scan.Groups.FirstOrDefault(x => x.Files.Any(f => Path.GetExtension(f.Path) == ".png")) ?? scan.Groups[0];
            GroupsList.SelectedIndex = scan.Groups.ToList().IndexOf(group);
            var keeper = keepers[group.Id];
            selected.Add(group.Files.First(x => x.Path != keeper).Path); RenderCards(); UpdateSelection(); ShowPreview(this, new RoutedEventArgs());
            if (preview?.Items.Count != 1 || !ExecuteButton.IsEnabled) throw new Exception("预览检查失败");
            Directory.CreateDirectory(args[3]);
            foreach (var tab in new[] { 0, 1, 2, 3, 4 })
            {
                Tabs.SelectedIndex = tab; UpdateLayout(); await Task.Delay(150);
                var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(args[3], $"ui-{tab}.png")); encoder.Save(file);
            }
            File.WriteAllText(Path.Combine(args[3], "ui-smoke.txt"), "PASS: scan, no automatic selection, groups, cleanup preview, five tabs rendered. No files recycled.");
        }
        catch (Exception error) { Directory.CreateDirectory(args[3]); File.WriteAllText(Path.Combine(args[3], "ui-smoke.txt"), "FAIL: " + error); }
        finally { Close(); }
    }
#endif
}
