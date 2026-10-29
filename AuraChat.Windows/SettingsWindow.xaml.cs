using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace AuraChat.Windows;

public partial class SettingsWindow : Window
{
    private readonly Api _api;
    private readonly string _base;
    private readonly Dictionary<string, Func<Task>> _sections = new();
    private readonly bool _standalone;
    private string _current = "";
    private bool _owner;
    private string _serverVersion = "";

    private MediaPlayer? _ttsPlayer;
    private byte[]? _ttsBytes;
    private string _ttsMime = "audio/mpeg";
    private TextBox? _ttsText;
    private ImageSource? _wallpaper;
    private static string BaseDir => Path.GetDirectoryName(Environment.ProcessPath)!;
    private static string WallpaperPath => Path.Combine(BaseDir, "wallpaper.jpg");
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    // 人格编辑器：null = 收起；"" = 新建；其它 = 正在编辑的 id
    private string? _personaEdit;

    // 客人「重置密码」的内联编辑
    private string _guestPwSubject = "";

    // 关于钉在最上面，后面是 web 端那 19 组
    private static readonly string[] NavOrder =
    {
        "关于",
        "总开关", "身份", "外观", "模型", "回复节奏", "空间日记", "语音", "听语音",
        "生图", "表情库", "聊天记录", "日志", "闲置搭话", "自驱", "网页入口", "客人", "账号", "本地缓存",
    };

    // 访客也能看到的几项，其余只有主人能进
    private static readonly HashSet<string> GuestSections = new(StringComparer.Ordinal)
    {
        "关于", "账号", "本地缓存",
    };

    public bool LoggedOut { get; private set; }

    public SettingsWindow(Api api, string baseUrl, string? startSection = null, bool standalone = false)
    {
        InitializeComponent();
        _api = api;
        _base = baseUrl;
        _standalone = standalone;
        Register();

        if (standalone)
        {
            Sidebar.Visibility = Visibility.Collapsed;
            Grid.SetColumn(ContentRoot, 0);
            Grid.SetColumnSpan(ContentRoot, 2);
            Title = "AuraChat · " + (startSection ?? "空间");
        }
        else
        {
            Title = "AuraChat · 设置";
        }

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBox.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) =>
        {
            _toastTimer.Stop();
            Theme.Changed -= OnThemeChanged;
            try
            {
                _ttsPlayer?.Stop();
                _ttsPlayer?.Close();
            }
            catch { }
        };
        Theme.Changed += OnThemeChanged;

        _ = InitAsync(startSection);
    }

    // 先问角色再决定导航给几项；顺手记下服务端版本给「关于」用
    private async Task InitAsync(string? startSection)
    {
        try
        {
            var st = await _api.State();
            _serverVersion = S(st["version"]);
            var role = S(Obj(st["me"])?["role"]);
            _owner = role == "owner";
            if (role.Length == 0)
            {
                // 老服务端没有 me.role，退回 api/config 的 owner 标记
                try
                {
                    var cfg = await _api.Get("api/config");
                    _owner = B(cfg["owner"]);
                }
                catch { }
            }
        }
        catch { }

        // 独立页只会从主人的导航进来；状态读不到时别把主人当访客
        if (_standalone && !_owner)
            _owner = startSection is "概览" or "记忆" or "日志";

        // 主窗的「日志」是独立页，设置里的「日志」是日志大小那组，别串
        if (_standalone && startSection == "日志") startSection = "日志页";

        BuildNav();
        Navigate(startSection ?? (_owner ? "总开关" : "账号"));
        await LoadWallpaperAsync();
    }

    // 换主题后重刷：卡片底色是建的时候取的快照
    private void OnThemeChanged()
    {
        if (!IsLoaded) return;
        ApplyBackdrop();
        Navigate(_current.Length > 0 ? _current : "账号");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private void ApplyTitleBar()
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            var dark = Theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(h, 19, ref dark, sizeof(int));
        }
        catch { }
    }

    private async Task LoadWallpaperAsync()
    {
        try
        {
            _wallpaper = File.Exists(WallpaperPath)
                ? await Task.Run(() => ImageUtil.ToImage(File.ReadAllBytes(WallpaperPath)))
                : null;
        }
        catch { _wallpaper = null; }
        ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        ApplyTitleBar();
        var glass = Theme.IsGlass;
        BackdropLayer.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        BackdropImage.Source = _wallpaper;
        var radius = Theme.Mode switch
        {
            AppTheme.Acrylic => 42d,
            AppTheme.Frosted => 26d,
            _ => 0d,
        };
        BackdropImage.Effect = radius > 0 ? new BlurEffect { Radius = radius } : null;
        BackdropTint.Background = glass ? Theme.Brush("GlassOverlay") : Brushes.Transparent;
        if (glass && _wallpaper is null)
        {
            BackdropLayer.Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#1B2436"),
                (Color)ColorConverter.ConvertFromString("#101318"),
                new Point(0, 0),
                new Point(0.35, 1));
            BackdropTint.Opacity = Theme.Mode == AppTheme.Frosted ? 0.34 : 0.48;
        }
        else
        {
            BackdropLayer.Background = Brushes.Transparent;
            BackdropTint.Opacity = _wallpaper is null ? 0.96 : (Theme.Mode == AppTheme.Frosted ? 0.62 : 0.8);
        }
    }

    private void Register()
    {
        _sections["关于"] = BuildAboutAsync;
        _sections["总开关"] = BuildMasterAsync;
        _sections["身份"] = BuildIdentityAsync;
        _sections["外观"] = BuildAppearanceAsync;
        _sections["模型"] = BuildModelAsync;
        _sections["回复节奏"] = BuildReplyAsync;
        _sections["空间日记"] = BuildDiaryAsync;
        _sections["语音"] = BuildVoiceAsync;
        _sections["听语音"] = BuildListenAsync;
        _sections["生图"] = BuildImageGenAsync;
        _sections["表情库"] = BuildStickerAsync;
        _sections["聊天记录"] = BuildHistoryAsync;
        _sections["日志"] = BuildLogAsync;
        _sections["闲置搭话"] = BuildSpeakUpAsync;
        _sections["自驱"] = BuildSelfDriveAsync;
        _sections["网页入口"] = BuildWebAsync;
        _sections["客人"] = BuildGuestAsync;
        _sections["账号"] = BuildAccountAsync;
        _sections["本地缓存"] = BuildCacheAsync;

        // 独立页（空间 / 心情 / 概览 / 记忆 / 日志）也走同一个窗口
        _sections["空间"] = BuildSpaceAsync;
        _sections["心情"] = BuildMoodAsync;
        _sections["概览"] = BuildOverviewAsync;
        _sections["记忆"] = BuildMemoryAsync;
        _sections["日志页"] = BuildLogsPageAsync;
    }

    private void BuildNav()
    {
        if (_standalone)
        {
            Sidebar.Visibility = Visibility.Collapsed;
            return;
        }
        NavPanel.Children.Clear();
        foreach (var name in NavOrder)
        {
            if (!_owner && !GuestSections.Contains(name)) continue;
            var btn = new Button
            {
                Content = name,
                Style = (Style)Application.Current.FindResource("NavButton"),
                Tag = name,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            btn.Click += (_, _) => Navigate(name);
            NavPanel.Children.Add(btn);
        }
    }

    private async void Navigate(string name)
    {
        _current = name;
        HeadText.Text = name switch
        {
            "日志页" => "日志",
            _ => name,
        };
        HeadRight.Children.Clear();
        foreach (Button b in NavPanel.Children)
        {
            var on = (string)b.Tag == name;
            b.Background = on ? Theme.Brush("Hover") : Brushes.Transparent;
            b.Foreground = on ? Theme.Brush("Fg") : Theme.Brush("Dim");
        }
        ResetBody();
        Say("");
        if (!_sections.TryGetValue(name, out var build)) return;
        try
        {
            await build();
        }
        catch (Exception ex)
        {
            Bad("这一页没读出来：" + ex.Message);
        }
    }

    private void ResetBody()
    {
        Body.Children.Clear();
    }

    private void Say(string s)
    {
        StatusText.Text = s;
        StatusText.Foreground = Theme.Brush("Dim");
        if (s.Length > 0) ShowToast(s);
    }

    private void Bad(string s)
    {
        StatusText.Text = s;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        if (s.Length > 0) ShowToast(s, danger: true);
    }

    private void ShowToast(string text, bool danger = false)
    {
        ToastText.Text = text;
        ToastText.Foreground = Brushes.White;
        ToastBox.Background = danger
            ? new SolidColorBrush(Color.FromRgb(0xB9, 0x3B, 0x3B))
            : Theme.Brush("Accent");
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void AddCard(string? title, params UIElement[] kids)
    {
        var sp = new StackPanel();
        if (!string.IsNullOrEmpty(title))
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8),
            });
        foreach (var k in kids) sp.Children.Add(k);
        Body.Children.Add(new Border
        {
            Background = Theme.Brush("Panel"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = sp,
        });
    }

    private StackPanel CardWithHead(string title, UIElement? headRight, params UIElement[] kids)
    {
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (headRight is not null)
        {
            DockPanel.SetDock(headRight, Dock.Right);
            head.Children.Add(headRight);
        }
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(head);
        foreach (var k in kids) sp.Children.Add(k);
        Body.Children.Add(new Border
        {
            Background = Theme.Brush("Panel"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = sp,
        });
        return sp;
    }

    private TextBlock Text(string text, bool dim = false, double size = 12.5, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Theme.Brush(dim ? "Dim" : "Fg"),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = Theme.Brush("Dim"),
        Margin = new Thickness(0, 2, 0, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private StackPanel Cell(string caption, FrameworkElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        sp.Children.Add(Label(caption));
        sp.Children.Add(control);
        return sp;
    }

    private StackPanel Field(string caption, FrameworkElement control, string hint = "")
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(Label(caption));
        sp.Children.Add(control);
        if (hint.Length > 0) sp.Children.Add(Text(hint, dim: true, size: 11));
        return sp;
    }

    private StackPanel Switched(CheckBox box, string hint = "")
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(box);
        if (hint.Length > 0) sp.Children.Add(Text(hint, dim: true, size: 11));
        return sp;
    }

    private Grid KV(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Text(label, dim: true);
        var v = Text(value, wrap: false);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        v.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        return g;
    }

    private TextBox Input(string value = "", double width = 320, bool multi = false, double height = 90)
    {
        var tb = new TextBox
        {
            Text = value,
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            AcceptsReturn = multi,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        return tb;
    }

    private PasswordBox Pw(string hint)
    {
        return new PasswordBox
        {
            Margin = new Thickness(0, 0, 8, 8),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Theme.Brush("Bg"),
            Foreground = Theme.Brush("Fg"),
            BorderBrush = Theme.Brush("Line"),
            FontSize = 13,
            ToolTip = hint,
        };
    }

    private Button Btn(string text, Func<Task> action, string kind = "ghost", double width = double.NaN)
    {
        var styleKey = kind switch
        {
            "primary" => null,
            "danger" => "DangerButton",
            _ => "GhostButton",
        };
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 64,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (styleKey is not null) b.Style = (Style)Application.Current.FindResource(styleKey);
        if (!double.IsNaN(width)) b.Width = width;
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Bad("出错了：" + ex.Message);
            }
            finally
            {
                b.IsEnabled = true;
            }
        };
        return b;
    }

    private Button IconBtn(string glyph, string tooltip, bool danger = false, Func<Task>? action = null)
    {
        // 图标字形是 Segoe MDL2 Assets 的私用区字符，必须用 TextBlock 显式设字体：
        // 否则 ContentPresenter 生成的 TextBlock 会被全局隐式样式（Microsoft YaHei UI）覆盖，显示成方块
        var b = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
            },
            ToolTip = tooltip,
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        b.Style = (Style)Application.Current.FindResource(danger ? "DangerButton" : "GhostButton");
        if (action is not null)
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                try { await action(); }
                catch (Exception ex) { Bad("出错了：" + ex.Message); }
                finally { b.IsEnabled = true; }
            };
        return b;
    }

    private StackPanel Row(params UIElement[] kids)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var k in kids) sp.Children.Add(k);
        return sp;
    }

    private WrapPanel Wrap(params UIElement[] kids)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var k in kids) wp.Children.Add(k);
        return wp;
    }

    private CheckBox Chk(string text, bool value)
    {
        return new CheckBox
        {
            Content = text,
            IsChecked = value,
            Margin = new Thickness(0, 0, 18, 8),
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    // labels 是显示文字，values 是写回 config 的值
    private ComboBox Sel(IReadOnlyList<string> labels, IReadOnlyList<string> values, string current)
    {
        var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < labels.Count; i++)
            cb.Items.Add(new ComboBoxItem { Content = labels[i], Tag = i < values.Count ? values[i] : labels[i] });
        var idx = 0;
        for (var i = 0; i < values.Count; i++)
            if (values[i] == current) { idx = i; break; }
        if (cb.Items.Count > 0) cb.SelectedIndex = Math.Min(idx, cb.Items.Count - 1);
        return cb;
    }

    private static string SelValue(ComboBox cb) =>
        (cb.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private StackPanel SliderRow(string caption, double min, double max, double step, double value,
        string unit, out Slider slider, out TextBlock outText, string hint = "")
    {
        slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 260,
            Value = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var txt = new TextBlock
        {
            FontSize = 12,
            Foreground = Theme.Brush("Fg"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Text = value.ToString("0.##", CultureInfo.InvariantCulture) + unit,
        };
        outText = txt;
        var sl = slider;
        sl.ValueChanged += (_, _) =>
            txt.Text = sl.Value.ToString("0.##", CultureInfo.InvariantCulture) + unit;
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(Label(caption));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(sl);
        row.Children.Add(txt);
        sp.Children.Add(row);
        if (hint.Length > 0) sp.Children.Add(Text(hint, dim: true, size: 11));
        return sp;
    }

    private void HeadStat(string text)
    {
        var tb = Text(text, dim: true);
        tb.Margin = new Thickness(8, 0, 0, 0);
        tb.VerticalAlignment = VerticalAlignment.Center;
        HeadRight.Children.Add(tb);
    }

    private async Task<JsonObject?> LoadConfigAsync()
    {
        var r = await _api.Get("api/config");
        if (!Ok(r))
        {
            Bad(Reason(r, "读不到设置（要先以主人身份登录）"));
            return null;
        }
        return Obj(r["config"]) ?? new JsonObject();
    }

    private async Task<bool> PatchConfigAsync(JsonObject patch, string okText = "存好了", string badText = "没存上")
    {
        var r = await _api.Patch("api/config", patch);
        if (Ok(r))
        {
            Say(okText);
            return true;
        }
        Bad(Reason(r, badText));
        return false;
    }

    private static JsonObject Sub(JsonObject cfg, string key) => Obj(cfg[key]) ?? new JsonObject();

    private static int IntOf(string s, int def = 0) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static double DblOf(string s, double def = 0) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static int Pct(double ratio) => (int)Math.Round(ratio * 100);

    private void AddAboutCard()
    {
        var asm = Assembly.GetExecutingAssembly();
        var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString()
                  ?? "0.0.0";
        AddCard("关于",
            KV("客户端版本", ver),
            KV("平台", "Windows 桌面端 · WPF / .NET 9"),
            KV("服务端版本", _serverVersion.Length > 0 ? _serverVersion : "—"));
    }

    private Task BuildAboutAsync()
    {
        ResetBody();
        AddAboutCard();
        return Task.CompletedTask;
    }

    private async Task BuildAccountAsync()
    {
        ResetBody();
        var st = await _api.State();
        var me = Obj(st["me"]);
        var name = S(me?["name"]);
        var role = S(me?["role"]);

        AddCard("账号",
            KV("用户名", name.Length > 0 ? name : "（未知）"),
            KV("角色", Role(role)));

        var oldPw = Pw("当前密码");
        var newPw = Pw("新密码（至少 6 位）");
        AddCard("修改密码",
            Text("改完下次登录要用新密码。", dim: true),
            Row(Cell("当前密码", oldPw), Cell("新密码", newPw)),
            Btn("改密码", async () =>
            {
                if (newPw.Password.Length < 6) { Bad("新密码至少 6 位。"); return; }
                var r = await _api.Post("api/account/password", new JsonObject
                {
                    ["oldPassword"] = oldPw.Password,
                    ["newPassword"] = newPw.Password,
                });
                if (Ok(r))
                {
                    Say("密码已经改好了");
                    oldPw.Clear();
                    newPw.Clear();
                }
                else Bad(Reason(r, "密码没改成"));
            }, "primary"));

        AddCard("退出登录",
            Text("退出后回到登录页，本地 token 会被清掉。", dim: true),
            Btn("退出登录", DoLogout, "danger"));
    }

    private async Task DoLogout()
    {
        try { await _api.Logout(); } catch { }
        LoggedOut = true;
        Close();
    }

    private async Task BuildCacheAsync()
    {
        ResetBody();
        var (bytes, count) = await Task.Run(() =>
        {
            try
            {
                var dir = MediaCache.Dir;
                if (!Directory.Exists(dir)) return (0L, 0);
                var files = new DirectoryInfo(dir).GetFiles();
                return (files.Sum(f => f.Length), files.Length);
            }
            catch { return (0L, 0); }
        });

        AddCard("本地缓存",
            Text("聊天里的图片、语音取下来一次就存在 exe 同目录的 media 里；总共 300MB 封顶，太旧的会自动清。", dim: true),
            KV("占用", $"{bytes / 1024.0 / 1024.0:0.0} MB · {count} 个文件"),
            KV("位置", MediaCache.Dir),
            Btn("清空缓存", async () =>
            {
                var (okCount, freed) = await Task.Run(() =>
                {
                    var n = 0;
                    long freed2 = 0;
                    try
                    {
                        var dir = MediaCache.Dir;
                        if (!Directory.Exists(dir)) return (0, 0L);
                        foreach (var f in new DirectoryInfo(dir).GetFiles())
                        {
                            try
                            {
                                var len = f.Length;
                                f.Delete();
                                n++;
                                freed2 += len;
                            }
                            catch { }
                        }
                    }
                    catch { }
                    return (n, freed2);
                });
                Say($"清掉了 {okCount} 个文件，释放 {freed / 1024.0 / 1024.0:0.0} MB");
                await BuildCacheAsync();
            }, "danger"));
    }

    private static bool Ok(JsonObject? r) =>
        r?["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && ok;

    private static string Reason(JsonObject? r, string fallback)
    {
        if (r?["reason"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
        return fallback;
    }

    private static string S(JsonNode? n, string def = "")
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return def;
    }

    private static long L(JsonNode? n, long def = 0)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        }
        return def;
    }

    private static double D(JsonNode? n, double def = 0)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
        }
        return def;
    }

    private static bool B(JsonNode? n, bool def = false)
    {
        if (n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return def;
    }

    private static JsonObject? Obj(JsonNode? n) => n as JsonObject;

    private static JsonArray? Arr(JsonNode? n) => n as JsonArray;

    private static string Fmt(long ms) =>
        ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    private static string Js(JsonNode? n, int max = 500)
    {
        try
        {
            var s = n?.ToJsonString() ?? "";
            return s.Length > max ? s[..max] + "…" : s;
        }
        catch
        {
            return "";
        }
    }

    private static string Role(string role) => role switch
    {
        "owner" => "主人（owner）",
        "social" => "访客（social）",
        "" => "—",
        _ => role,
    };
}
