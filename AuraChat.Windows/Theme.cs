using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace AuraChat.Windows;

public enum AppTheme
{
    Follow,
    Light,
    Dark,
    Acrylic,
    Frosted,
}

// 换主题 = 换 App.xaml 那批颜色资源（XAML 全用 DynamicResource）+ 重排气泡。
// 亚克力/毛玻璃不动 WindowStyle、AllowsTransparency：窗口显示后再开会重建 HWND，
// 托盘和通知容易跟着坏；改成垫一张背景图 + 半透明面板做磨砂。
public static class Theme
{
    public static string UiPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath)!, "ui.json");

    public static AppTheme Mode { get; private set; } = AppTheme.Dark;

    public static bool IsDark { get; private set; } = true;

    public static bool IsGlass => Mode is AppTheme.Acrylic or AppTheme.Frosted;

    // 颜色资源换了（主窗要重排气泡、重刷背景）。
    public static event Action? Changed;

    // 模式切了：背景图该不该露出来另发一次，和上面的重排分开。
    public static event Action? BackdropChanged;

    public static string ModeLabel(AppTheme m) => m switch
    {
        AppTheme.Follow => "跟随系统",
        AppTheme.Light => "浅色",
        AppTheme.Dark => "深色",
        AppTheme.Acrylic => "亚克力",
        AppTheme.Frosted => "毛玻璃",
        _ => "深色",
    };

    private static readonly string[] Keys =
    {
        "Bg", "Panel", "PanelSolid", "Line", "Fg", "Dim", "Accent", "MineBg", "TaBg", "Hover", "Pressed", "GlassOverlay",
    };

    // 深色配色是老的，换主题别把老用户习惯的颜色改掉。
    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        ["Bg"] = C("#101215"),
        ["Panel"] = C("#171A1F"),
        ["Line"] = C("#262B33"),
        ["Fg"] = C("#E7EAF0"),
        ["Dim"] = C("#8A93A3"),
        ["Accent"] = C("#3B82F6"),
        ["MineBg"] = C("#1E3A8A"),
        ["TaBg"] = C("#1F242B"),
        ["Hover"] = C("#1F2530"),
        ["Pressed"] = C("#2A3240"),
        ["GlassOverlay"] = C("#1A0E1116"),
    };

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        ["Bg"] = C("#F4F6FA"),
        ["Panel"] = C("#FFFFFF"),
        ["Line"] = C("#DCE3ED"),
        ["Fg"] = C("#1B2130"),
        ["Dim"] = C("#6A7488"),
        ["Accent"] = C("#2563EB"),
        ["MineBg"] = C("#C9DCFB"),
        ["TaBg"] = C("#EDF1F7"),
        ["Hover"] = C("#E9EFF8"),
        ["Pressed"] = C("#D9E3F1"),
        ["GlassOverlay"] = C("#33F7FAFF"),
    };

    // 玻璃主题：面板带 alpha，让底下的背景图透上来。
    private static readonly Dictionary<string, Color> DarkGlassPalette = new()
    {
        ["Bg"] = C("#0E1116"),
        ["Panel"] = A("#B316191F"),
        ["Line"] = A("#59606470"),
        ["Fg"] = C("#E7EAF0"),
        ["Dim"] = C("#AFB8C6"),
        ["Accent"] = C("#5B9CF8"),
        ["MineBg"] = A("#C71E3A8A"),
        ["TaBg"] = A("#A81F242B"),
        ["Hover"] = A("#26FFFFFF"),
        ["Pressed"] = A("#40FFFFFF"),
        ["GlassOverlay"] = C("#33000000"),
    };

    private static readonly Dictionary<string, Color> LightGlassPalette = new()
    {
        ["Bg"] = C("#E7ECF5"),
        ["Panel"] = A("#B3FFFFFF"),
        ["Line"] = A("#59607080"),
        ["Fg"] = C("#1B2130"),
        ["Dim"] = C("#57617A"),
        ["Accent"] = C("#2563EB"),
        ["MineBg"] = A("#C7C9DCFB"),
        ["TaBg"] = A("#B3EDF1F7"),
        ["Hover"] = A("#1A1B2130"),
        ["Pressed"] = A("#2E1B2130"),
        ["GlassOverlay"] = C("#4DFFFFFF"),
    };

    private static Dictionary<string, Color> _current = DarkPalette;
    private static readonly Dictionary<string, Brush> Cache = new();

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color A(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // Bubble 静态字段可能早于 App 初始化，别碰 Application.Current
    public static Brush Brush(string key)
    {
        if (Cache.TryGetValue(key, out var hit)) return hit;
        // PanelSolid = Panel 去掉 alpha，给必须盖住底下东西的弹层用（下拉菜单）。
        System.Windows.Media.Color color;
        if (key == "PanelSolid")
        {
            var pc = _current.TryGetValue("Panel", out var p) ? p : Colors.Transparent;
            color = System.Windows.Media.Color.FromArgb(0xFF, pc.R, pc.G, pc.B);
        }
        else
        {
            color = _current.TryGetValue(key, out var c) ? c : Colors.Transparent;
        }
        var b = new SolidColorBrush(color);
        b.Freeze();
        Cache[key] = b;
        return b;
    }

    public static Color Color(string key) => _current.TryGetValue(key, out var c) ? c : Colors.Transparent;

    public static void Load()
    {
        try
        {
            if (File.Exists(UiPath))
            {
                var j = JsonNode.Parse(File.ReadAllText(UiPath)) as JsonObject;
                var s = j?["mode"]?.GetValue<string>() ?? "";
                Mode = s switch
                {
                    "follow" => AppTheme.Follow,
                    "light" => AppTheme.Light,
                    "dark" => AppTheme.Dark,
                    "acrylic" => AppTheme.Acrylic,
                    "frosted" => AppTheme.Frosted,
                    _ => AppTheme.Dark,
                };
            }
        }
        catch { }
        Apply(save: false);
    }

    public static void Set(AppTheme mode)
    {
        Mode = mode;
        Apply(save: true);
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiPath)!);
            var s = Mode switch
            {
                AppTheme.Follow => "follow",
                AppTheme.Light => "light",
                AppTheme.Dark => "dark",
                AppTheme.Acrylic => "acrylic",
                AppTheme.Frosted => "frosted",
                _ => "dark",
            };
            File.WriteAllText(UiPath, new JsonObject { ["mode"] = s }.ToJsonString());
        }
        catch { }
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { }
        return false;
    }

    public static void Apply(bool save = true)
    {
        if (save) Save();
        var light = Mode switch
        {
            AppTheme.Light => true,
            AppTheme.Dark => false,
            // 玻璃主题固定按深色做底：跟系统浅色会糊成一片白，也跟网页端对不上。
            AppTheme.Acrylic => false,
            AppTheme.Frosted => false,
            AppTheme.Follow => SystemPrefersLight(),
            _ => false,
        };
        IsDark = !light;
        _current = Mode switch
        {
            AppTheme.Acrylic or AppTheme.Frosted => light ? LightGlassPalette : DarkGlassPalette,
            _ => light ? LightPalette : DarkPalette,
        };
        Cache.Clear();
        var app = Application.Current;
        if (app is not null)
        {
            foreach (var key in Keys)
                app.Resources[key] = Brush(key);
        }
        BackdropChanged?.Invoke();
        Changed?.Invoke();
    }
}
