using System.IO;
using System.Windows;
using System.Windows.Media;

namespace BvdsForWindows;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(AppContext.BaseDirectory, "crash.log");

    /// <summary>主题色定义（参考 青.jpg / 琥珀.jpg 主色）：teal（薄荷青绿） / amber（橙红陶土）</summary>
    private static readonly IReadOnlyDictionary<string, (Color Accent, Color AccentDark, Color AccentSoft)> Themes =
        new Dictionary<string, (Color, Color, Color)>
        {
            ["teal"] = (Color.FromRgb(0x6F, 0xAF, 0xAF), Color.FromRgb(0xA8, 0xD8, 0xD8), Color.FromRgb(0xD8, 0xF0, 0xF0)),
            ["amber"] = (Color.FromRgb(0xB8, 0x5C, 0x48), Color.FromRgb(0xD8, 0x78, 0x60), Color.FromRgb(0xF3, 0xD9, 0xCE)),
        };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            ShowErrorOnce(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomainUnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LogCrash("UnobservedTaskException", args.Exception);

        // 应用已保存的主题
        try
        {
            ApplyTheme(new Core.ConfigStore().Theme);
        }
        catch { }
    }

    /// <summary>全局应用主题（替换资源字典中的强调色，DynamicResource 引用自动更新）</summary>
    public static void ApplyTheme(string theme)
    {
        if (!Themes.TryGetValue(theme, out var colors))
            colors = Themes["teal"];
        var res = Current.Resources;
        res["AccentBrush"] = new SolidColorBrush(colors.Accent);
        res["AccentDarkBrush"] = new SolidColorBrush(colors.AccentDark);
        res["AccentSoftBrush"] = new SolidColorBrush(colors.AccentSoft);   // 浅色按钮底
    }

    /// <summary>同一秒内只弹一次错误框（防止布局循环等异常轰炸弹窗）</summary>
    private static long _lastErrorBoxTick;
    private static void ShowErrorOnce(Exception ex)
    {
        var now = Environment.TickCount64;
        if (now - _lastErrorBoxTick < 1000) return;
        _lastErrorBoxTick = now;
        try
        {
            MessageBox.Show($"发生未处理异常:\n{ex.Message}\n\n详情已写入 crash.log", "BVDS 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] =====\n{ex}\n");
        }
        catch { }
    }
}
