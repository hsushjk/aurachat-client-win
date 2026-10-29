using System.Linq;
using System.Windows;

namespace AuraChat.Windows;

public partial class App : Application
{
    // --silent / -silent：不弹主窗，直接缩到托盘。
    public static bool StartSilent { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 主题先于主窗：晚一帧会先画默认深色再闪成浅色。
        Theme.Load();
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("出了点问题：" + args.Exception.Message, "AuraChat", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);

        // 不用 StartupUri：它建窗即 Show，silent 模式要手动控制显示。
        StartSilent = e.Args.Any(a => a is "--silent" or "-silent" or "/silent");
        var win = new MainWindow();
        if (!StartSilent) win.Show();
    }
}
