using System.Windows;

namespace SpaceKeeper.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成：" + args.Exception.Message + "\n请检查原文件、回收站和操作记录。", "空间守护", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
