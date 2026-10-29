using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace AuraChat.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Bubble> _items = new();
    private readonly Dictionary<string, Msg> _msgs = new();
    private Api? _api;
    private CancellationTokenSource? _streamCts;
    private bool _typingSent;
    private bool _hasMore;
    private bool _loadingOlder;
    private bool _stickToBottom = true;
    // 钉住的消息：id + 内容里的 y，前插或图片撑高后按增量补回视口。
    private string? _pinId;
    private double _pinContentY;
    private DateTime _pinUntil;
    private WinForms.NotifyIcon? _tray;
    // 上次弹气泡的时间：Windows 把气泡排队逐条放，弹太密会堵住点击。
    private DateTime _lastTipAt = DateTime.MinValue;
    private static readonly TimeSpan TipGap = TimeSpan.FromSeconds(6);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;
    private readonly DispatcherTimer _voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private MediaPlayer? _voicePlayer;
    private Bubble? _voiceBubble;
    private bool _recording;
    private static readonly SolidColorBrush RecBg = new(Color.FromRgb(0x4A, 0x20, 0x26));

    // 未发出的气泡（重试靠它），永远垫在列表最后。
    private readonly List<Bubble> _pendingRows = new();

    private string _lastDayKey = "";
    private int _unread;
    private string _search = "";
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private ImageSource? _wallpaper;
    private bool _closed;

    private static string BaseDir => Path.GetDirectoryName(Environment.ProcessPath)!;
    private static string StorePath => Path.Combine(BaseDir, "client.json");
    private static string DraftPath => Path.Combine(BaseDir, "draft.txt");

    public MainWindow()
    {
        InitializeComponent();
        MsgList.ItemsSource = _items;
        _voiceTimer.Tick += OnVoiceTick;
        _draftTimer.Tick += OnDraftTick;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBox.Visibility = Visibility.Collapsed;
        };
        Theme.Changed += OnThemeChanged;
        LoadSaved();
        InitTray();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _voiceTimer.Stop();
            _draftTimer.Stop();
            _toastTimer.Stop();
            try { _streamCts?.Cancel(); } catch { }
            _tray?.Dispose();
            _tray = null;
            Theme.Changed -= OnThemeChanged;
        };
    }

    private void LoadSaved()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var j = JsonNode.Parse(File.ReadAllText(StorePath)) as JsonObject;
            var b = j?["base"]?.GetValue<string>() ?? "";
            var user = j?["user"]?.GetValue<string>() ?? "";
            var token = j?["token"]?.GetValue<string>() ?? "";
            if (b.Length > 0) AddrBox.Text = b;
            if (user.Length > 0) UserBox.Text = user;
            if (token.Length > 0 && b.Length > 0)
            {
                _api = new Api(b) { Token = token };
                ShowChat();
                _ = StartAsync();
            }
        }
        catch { }
    }

    private void Save(string? token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var j = new JsonObject
            {
                ["base"] = Normalize(AddrBox.Text),
                ["user"] = UserBox.Text.Trim(),
                ["token"] = token ?? _api?.Token ?? "",
            };
            File.WriteAllText(StorePath, j.ToJsonString());
        }
        catch { }
    }

    private static string Normalize(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (!s.StartsWith("http://") && !s.StartsWith("https://"))
        {
            var hostPort = s.Split('/')[0];
            var hasPort = hostPort.Contains(':') && hostPort[(hostPort.LastIndexOf(':') + 1)..].All(char.IsDigit);
            s = "http://" + (hasPort ? hostPort : hostPort + ":2332") + s[hostPort.Length..];
        }
        return s.TrimEnd('/');
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        ConnectBtn.IsEnabled = false;
        ConnectMsg.Text = "";
        var baseUrl = Normalize(AddrBox.Text);
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;
        if (baseUrl.Length == 0 || user.Length == 0 || pass.Length == 0)
        {
            ConnectMsg.Text = "地址、用户名、密码都要填。";
            ConnectBtn.IsEnabled = true;
            return;
        }
        try
        {
            var failReason = await DoLogin(baseUrl, user, pass, insecure: false);
            if (failReason is null) return;

            // https 登录失败多半是自签证书：抓指纹让用户核对后再决定信不信
            if (baseUrl.StartsWith("https://"))
            {
                var fp = await Api.CaptureFingerprintAsync(baseUrl + "/api/state");
                if (!string.IsNullOrEmpty(fp))
                {
                    var key = baseUrl["https://".Length..].TrimEnd('/');
                    var result = MessageBox.Show(
                        this,
                        "服务器用的是自签证书，系统不认。\n\n指纹（SHA-256）：\n" + fp + "\n\n如果和你在别处看到的指纹一致，就点「是」信任并连接。",
                        "信任这个证书吗？",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                    {
                        Api.AcceptCert(key, fp);
                        await DoLogin(baseUrl, user, pass, insecure: true);
                        return;
                    }
                }
            }
            ConnectMsg.Text = failReason;
        }
        catch (Exception ex)
        {
            ConnectMsg.Text = "连不上：" + ex.Message;
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
        }
    }

    private async Task<string?> DoLogin(string baseUrl, string user, string pass, bool insecure)
    {
        var api = new Api(baseUrl, insecure);
        var r = await api.Login(user, pass);
        if (!OkOf(r))
        {
            api.Dispose();
            return ReasonOf(r, "登录没成功，检查用户名密码。");
        }
        if (r["token"] is JsonValue tv && tv.TryGetValue<string>(out var token)) api.Token = token;
        try { _streamCts?.Cancel(); } catch { }
        _api?.Dispose();
        _api = api;
        Save(api.Token);
        ShowChat();
        await StartAsync();
        return null;
    }

    private void ShowChat()
    {
        ConnectPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        InputBox.Focus();
    }

    private async Task StartAsync()
    {
        var api = _api;
        if (api is null) return;
        try
        {
            var page = await api.MessagesPage("last");
            _msgs.Clear();
            foreach (var m in page.Messages)
                if (m.Id.Length > 0) _msgs[m.Id] = m;
            _hasMore = page.HasMore;
            _stickToBottom = true;
            RebuildRows(keepOffset: false);
            UpdateOlder();
            Scroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            StateText.Text = "聊天记录拉不下来：" + ex.Message;
            return;
        }
        _ = LoadStateAsync();
        _ = LoadAvatarAsync();
        _ = LoadWallpaperAsync();
        RestoreDraft();
        ConnectStream();
    }

    private async Task LoadStateAsync()
    {
        var api = _api;
        if (api is null) return;
        try
        {
            var s = await api.State();
            if (s["botName"] is JsonValue nv && nv.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                TitleText.Text = name;

            // 角色先看 me.role，老服务端没这字段就退回 api/config 的 owner 标记
            var role = "";
            if (s["me"] is JsonObject me && me["role"] is JsonValue rv && rv.TryGetValue<string>(out var rs)) role = rs;
            var owner = role == "owner";
            if (role.Length == 0)
            {
                try
                {
                    var cfg = await api.Get("api/config");
                    owner = cfg["owner"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob;
                }
                catch { }
            }
            SetOwnerNav(owner);
        }
        catch { }
    }

    private async Task LoadAvatarAsync()
    {
        var api = _api;
        if (api is null) return;
        try
        {
            var bytes = await api.FetchBytes("api/avatar");
            var img = bytes is null ? null : await Task.Run(() => ImageUtil.ToImage(bytes));
            if (img is null)
            {
                AvatarBox.Visibility = Visibility.Collapsed;
                AvatarImage.Source = null;
                return;
            }
            AvatarImage.Source = img;
            AvatarBox.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private async Task LoadWallpaperAsync()
    {
        try
        {
            var path = Path.Combine(BaseDir, "wallpaper.jpg");
            _wallpaper = File.Exists(path)
                ? await Task.Run(() => ImageUtil.ToImage(File.ReadAllBytes(path)))
                : null;
        }
        catch { _wallpaper = null; }
        ApplyBackdrop();
    }

    private void ConnectStream()
    {
        var api = _api;
        if (api is null) return;
        _streamCts?.Cancel();
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await api.StreamAsync(
                        onMessage: m => Dispatcher.Invoke(() => AddMessage(m)),
                        onOpen: () => Dispatcher.Invoke(() =>
                        {
                            StateText.Text = "已连接";
                            _ = ResyncAsync();
                        }),
                        ct: ct,
                        onTyping: on => Dispatcher.Invoke(() => StateText.Text = on ? "对方正在输入…" : "已连接"),
                        onHistory: (oldest, more) => Dispatcher.Invoke(() => TrimBefore(oldest, more)));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => StateText.Text = "断了，3 秒后重连：" + ex.Message);
                }
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }, ct);
    }

    private async Task ResyncAsync()
    {
        var api = _api;
        if (api is null) return;
        try
        {
            var lastT = _msgs.Values.Select(m => m.T).DefaultIfEmpty(0).Max();
            var page = await api.MessagesPage("last");
            var added = new List<Msg>();
            foreach (var m in page.Messages)
            {
                if (m.Id.Length == 0 || _msgs.ContainsKey(m.Id)) continue;
                _msgs[m.Id] = m;
                added.Add(m);
            }
            _hasMore = page.HasMore || _hasMore;
            if (added.Count == 0) return;
            if (added.All(m => m.T >= lastT))
            {
                // 全是新消息就追加到末尾，不整表重排，少闪一下
                foreach (var m in added.OrderBy(x => x.T))
                    AppendRealRow(m, loadPic: true);
                if (_stickToBottom) Scroll.ScrollToEnd();
            }
            else
            {
                RebuildRows(keepOffset: true);
            }
            UpdateOlder();
            UpdateUnread();
        }
        catch { }
    }

    private void RebuildRows(bool keepOffset)
    {
        var offset = Scroll.VerticalOffset;
        var extent = Scroll.ExtentHeight;
        var stick = _stickToBottom;
        _items.Clear();
        _lastDayKey = "";
        foreach (var m in _msgs.Values.OrderBy(x => x.T).ThenBy(x => x.Id))
        {
            if (!MatchesSearch(m)) continue;
            AppendRealRow(m, loadPic: true);
        }
        // 待发/失败的气泡永远垫在最后
        foreach (var b in _pendingRows)
            if (!_items.Contains(b)) _items.Add(b);
        _stickToBottom = stick;
        if (keepOffset)
        {
            Scroll.UpdateLayout();
            // UpdateLayout 后 ExtentHeight 未必算上新页，直接补偿常等于 0、视口跳到新顶；
            // 先补一次，等布局真正落地再补一次
            Scroll.ScrollToVerticalOffset(offset + (Scroll.ExtentHeight - extent));
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Scroll.ScrollToVerticalOffset(offset + (Scroll.ExtentHeight - extent));
            }), DispatcherPriority.Loaded);
        }
        else if (stick)
        {
            Scroll.ScrollToEnd();
        }
        UpdateUnread();
    }

    // 视口顶第一条真实消息：id + 内容里的 y。日期条和待发气泡不算。
    private (string id, double contentY)? TopAnchor()
    {
        var content = (FrameworkElement)Scroll.Content;
        foreach (var b in _items)
        {
            if (b.Id.Length == 0) continue;
            if (b.IsDivider) continue; // 日期条的 Id 也非空，但钉在顶上，不能当锚点
            if (MsgList.ItemContainerGenerator.ContainerFromItem(b) is not FrameworkElement fe) continue;
            var viewY = fe.TranslatePoint(new Point(0, 0), Scroll).Y;
            if (viewY < -1) continue;
            var contentY = fe.TranslatePoint(new Point(0, 0), content).Y;
            return (b.Id, contentY);
        }
        return null;
    }

    // 按锚点 y 的增量滚同样的量；用户滚动不改 contentY，所以不会跟用户抢滚动。
    private void PinToAnchor()
    {
        if (_pinId is null) return;
        if (DateTime.UtcNow > _pinUntil) { _pinId = null; return; }
        var content = (FrameworkElement)Scroll.Content;
        foreach (var b in _items)
        {
            if (b.Id != _pinId) continue;
            if (MsgList.ItemContainerGenerator.ContainerFromItem(b) is not FrameworkElement fe) return;
            var contentY = fe.TranslatePoint(new Point(0, 0), content).Y;
            var delta = contentY - _pinContentY;
            if (Math.Abs(delta) > 0.5)
            {
                Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + delta);
                _pinContentY = contentY;
            }
            return;
        }
    }

    private DispatcherTimer? _pinTimer;
    private int _pinTicks;

    // 布局落地先补一次；新页图片异步撑高，之后每 80ms 再补，图片加载完续窗口。
    private void RepinDeferred()
    {
        if (_pinId is null) return;
        Dispatcher.BeginInvoke(new Action(PinToAnchor), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(PinToAnchor), DispatcherPriority.ApplicationIdle);
        _pinTicks = 0;
        if (_pinTimer is null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) => PinTick(timer);
            _pinTimer = timer;
        }
        // 图片加载完就续窗口：重置拍数、重启计时器，别半路早退
        _pinTimer.Stop();
        _pinTimer.Start();
    }

    private void PinTick(DispatcherTimer timer)
    {
        PinToAnchor();
        if (_pinId is null || ++_pinTicks > 60) // 60 × 80ms = 4.8s
        {
            timer.Stop();
            _pinTimer = null;
        }
    }

    private void AppendRealRow(Msg m, bool loadPic)
    {
        var day = DayKey(m.T);
        if (day != _lastDayKey)
        {
            _lastDayKey = day;
            InsertBeforeFailed(Bubble.Divider(DayLabel(m.T)));
        }
        var b = MakeBubble(m);
        InsertBeforeFailed(b);
        if (loadPic) _ = LoadPicAsync(b, m);
    }

    private void InsertBeforeFailed(Bubble b)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Out is not null)
            {
                _items.Insert(i, b);
                return;
            }
        }
        _items.Add(b);
    }

    private void AddMessage(Msg m)
    {
        if (m.Id.Length > 0)
        {
            if (_msgs.ContainsKey(m.Id)) return;
            _msgs[m.Id] = m;
        }
        AppendRealRow(m, loadPic: true);
        if (_stickToBottom) Scroll.ScrollToEnd();
        if (m.Who != "user" && (!IsActive || !_stickToBottom))
        {
            _unread++;
            if (!IsActive) Notify(m);
        }
        UpdateUnread();
    }

    private void TrimBefore(long oldest, bool hasMore)
    {
        _hasMore = hasMore;
        var gone = _msgs.Values.Where(m => m.T < oldest).ToList();
        if (gone.Count > 0)
        {
            foreach (var m in gone) _msgs.Remove(m.Id);
            RebuildRows(keepOffset: true);
        }
        UpdateOlder();
    }

    private async Task LoadOlderAsync()
    {
        var api = _api;
        if (api is null || _loadingOlder || !_hasMore) return;
        _loadingOlder = true;
        UpdateOlder();
        // 先记锚点再拉页：新页插在前面，按这条摆回去
        var anchor = TopAnchor();
        try
        {
            var before = _msgs.Values.Select(m => m.T).DefaultIfEmpty(0).Min();
            var page = await api.MessagesPage("prev", before);
            var added = 0;
            foreach (var m in page.Messages)
            {
                if (m.Id.Length == 0 || _msgs.ContainsKey(m.Id)) continue;
                _msgs[m.Id] = m;
                added++;
            }
            _hasMore = page.HasMore;
            if (added > 0)
            {
                // 交给 PinToAnchor 按 y 增量补偿；RebuildRows 别自己滚，否则双倍
                RebuildRows(keepOffset: false);
                if (anchor is { } a)
                {
                    _pinId = a.id;
                    _pinContentY = a.contentY;
                    _pinUntil = DateTime.UtcNow.AddSeconds(5);
                    // 先同步钉一次最准；异步图片撑高交给 RepinDeferred
                    Scroll.UpdateLayout();
                    PinToAnchor();
                    RepinDeferred();
                }
            }
        }
        catch (Exception ex)
        {
            StateText.Text = "更早的记录没拉到：" + ex.Message;
        }
        finally
        {
            _loadingOlder = false;
            UpdateOlder();
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 列表自己在长高（图片加载、前插补偿）时别重算粘底：偏移没变、可滚高度变了，
        // 会把「明明在底部」误判成「滚上去了」，之后就不再自动跟新消息
        if (e.ExtentHeightChange == 0)
            _stickToBottom = Scroll.VerticalOffset + Scroll.ViewportHeight >= Scroll.ScrollableHeight - 30;
        // 只有人真的在看（窗口在前台 + 停在底部）才清未读：追加消息顺带把滚动条拉到最底
        // （ExtentHeightChange≠0），只看 _stickToBottom 会把后台收到的消息立刻清掉，红点永远不亮
        if (_stickToBottom && IsActive && _unread > 0) ClearUnread();
        else UpdateUnread();
        if (e.ExtentHeightChange != 0) return; // 列表自己在长高，不触发翻页
    }

    private void OnLoadOlder(object sender, RoutedEventArgs e) => _ = LoadOlderAsync();

    private void UpdateOlder()
    {
        OlderBtn.Content = _loadingOlder ? "正在加载…" : "查看更早的记录";
        OlderBtn.IsEnabled = !_loadingOlder;
        OlderBtn.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
        // 没更早的了：按钮收起换灰字；列表还空着就都不露
        OlderHint.Visibility = !_hasMore && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUnread()
    {
        Title = _unread > 0 ? $"AuraChat ({_unread})" : "AuraChat";
        JumpBtn.Visibility = _unread > 0 && !_stickToBottom ? Visibility.Visible : Visibility.Collapsed;
        if (_tray is null) return;
        try
        {
            var t = _unread > 0 ? $"AuraChat ({_unread})" : "AuraChat";
            _tray.Text = t.Length > 63 ? t[..63] : t;
        }
        catch { }
    }

    private void ClearUnread()
    {
        if (_unread == 0)
        {
            UpdateUnread();
            return;
        }
        _unread = 0;
        UpdateUnread();
    }

    private void OnActivated(object sender, EventArgs e) => ClearUnread();

    private void OnJumpBottom(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        Scroll.ScrollToEnd();
        ClearUnread();
    }

    private void OpenSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        SearchBox.Clear();
        _search = "";
        SearchHint.Text = "在已加载的消息里搜索（文字 / 名字 / 说明）…";
        SearchHint.Visibility = Visibility.Visible;
        RebuildRows(keepOffset: false);
        InputBox.Focus();
    }

    private void OnSearchClose(object sender, RoutedEventArgs e) => CloseSearch();

    private void OnToggleSearch(object sender, RoutedEventArgs e)
    {
        if (SearchBar.Visibility == Visibility.Visible) CloseSearch();
        else OpenSearch();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q == _search) return;
        _search = q;
        RebuildRows(keepOffset: false);
        if (_search.Length == 0)
        {
            StateText.Text = "已连接";
            return;
        }
        var n = _items.Count(b => b.Out is null && !b.IsDivider);
        StateText.Text = n == 0 ? "没有找到匹配的消息" : $"找到 {n} 条匹配";
        Scroll.ScrollToTop();
    }

    private bool MatchesSearch(Msg m)
    {
        if (_search.Length == 0) return true;
        return Has(m.Text) || Has(m.Name) || Has(m.Caption) || Has(m.Who);
    }

    private bool Has(string s) => s.Length > 0 && s.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private Bubble MakeBubble(Msg m)
    {
        var mine = m.Who == "user";
        var expired = m.MediaExpired;
        var media = !expired && m.Kind is "image" or "sticker";
        var voice = !expired && m.Kind == "voice";

        var body = media || voice
            ? ""
            : m.Kind switch
            {
                "sticker" => string.IsNullOrWhiteSpace(m.Caption)
                    ? (string.IsNullOrWhiteSpace(m.Name) ? "[表情]" : m.Name)
                    : m.Caption,
                "image" => mine ? "[图片]" : "[Ta 发来一张图]",
                _ => m.Text,
            };
        if (expired) body = m.Kind == "voice" ? "语音已过期" : "[图片已过期]";

        var t = m.T > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.T).ToLocalTime() : DateTimeOffset.Now;
        var voiceLen = voice ? (m.Dur > 0 ? $"{m.Dur}″" : "语音") : "";
        var bg = media ? Brushes.Transparent : Theme.Brush(mine ? "MineBg" : "TaBg");

        return new Bubble
        {
            Id = m.Id,
            Kind = m.Kind,
            Text = m.Text,
            Body = body,
            Clock = t.ToString("HH:mm"),
            Align = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Bg = bg,
            NormalBg = bg,
            Voice = voice ? m.Voice : "",
            VoiceTotalSec = voice ? m.Dur : 0,
            VoiceLen = voiceLen,
            VoiceLenIdle = voiceLen,
            VoiceVisibility = voice ? Visibility.Visible : Visibility.Collapsed,
            PlainTextVisibility = media || voice ? Visibility.Collapsed : Visibility.Visible,
            MediaId = m.MediaId,
        };
    }

    private async Task LoadPicAsync(Bubble bubble, Msg m)
    {
        try
        {
            if (m.Kind == "image" && m.MediaId.Length > 0 && _api is not null)
            {
                var path = await MediaCache.FetchAsync(_api, m.MediaId);
                if (path is null) return;
                var bytes = await File.ReadAllBytesAsync(path);
                var img = await Task.Run(() => ImageUtil.ToImage(bytes));
                if (img is not null) bubble.Pic = img;
                return;
            }
            if (m.Kind == "image" && m.Image.Length > 0)
            {
                var img = ImageUtil.FromBase64(m.Image);
                if (img is not null) bubble.Pic = img;
                return;
            }
            if (m.Kind == "sticker" && m.Sticker.Length > 0 && _api is not null)
            {
                var bytes = await _api.FetchBytes(_api.StickerUrl(m.Sticker));
                var img = bytes is null ? null : await Task.Run(() => ImageUtil.ToImage(bytes));
                if (img is not null) bubble.Pic = img;
            }
        }
        catch { }
        finally
        {
            // 图片撑高会挪内容，翻页期间要重新钉
            if (_pinId is not null) RepinDeferred();
        }
    }

    private static string DayKey(long ms) =>
        ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd") : "";

    private static string DayLabel(long ms)
    {
        var when = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().Date : DateTime.Today;
        var today = DateTime.Today;
        if (when == today) return "今天";
        if (when == today.AddDays(-1)) return "昨天";
        return when.Year == today.Year ? when.ToString("M月d日") : when.ToString("yyyy年M月d日");
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendInputAsync();

    private async Task SendInputAsync()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || _api is null) return;
        await SendOutAsync(new Outbox { Kind = "text", Text = text }, clearInput: text);
    }

    private async Task SendOutAsync(Outbox o, string? clearInput = null)
    {
        var api = _api;
        if (api is null) return;
        var row = AddPendingRow(o);
        var (ok, reason) = await TrySendAsync(api, o);
        if (ok)
        {
            RemoveRow(row);
            if (clearInput is not null && InputBox.Text.Trim() == clearInput) InputBox.Clear();
            if (o.Kind == "text") ClearDraft();
        }
        else
        {
            row.MarkFailed(reason);
        }
    }

    private Bubble AddPendingRow(Outbox o)
    {
        var bg = Theme.Brush("MineBg");
        var b = new Bubble
        {
            Id = "",
            Kind = o.Kind,
            Text = o.Text,
            Body = o.Label,
            Align = HorizontalAlignment.Right,
            Bg = bg,
            NormalBg = bg,
            Out = o,
            PlainTextVisibility = Visibility.Visible,
        };
        b.MarkSending();
        _pendingRows.Add(b);
        _items.Add(b);
        if (_stickToBottom) Scroll.ScrollToEnd();
        return b;
    }

    private void RemoveRow(Bubble b)
    {
        _pendingRows.Remove(b);
        _items.Remove(b);
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Bubble b || b.Out is null) return;
        var api = _api;
        if (api is null) return;
        var o = b.Out;
        b.MarkSending();
        var (ok, reason) = await TrySendAsync(api, o);
        if (ok)
        {
            RemoveRow(b);
            if (o.Kind == "text" && InputBox.Text.Trim() == o.Text) InputBox.Clear();
            if (o.Kind == "text") ClearDraft();
        }
        else
        {
            b.MarkFailed(reason);
        }
    }

    private static async Task<(bool Ok, string Reason)> TrySendAsync(Api api, Outbox o)
    {
        JsonObject r;
        try
        {
            r = o.Kind switch
            {
                "image" => await api.SendImage(o.B64, o.Mime, o.Name),
                "voice" => await api.SendVoice(o.B64, o.Mime, o.Dur),
                "sticker" => await api.SendSticker(o.StickerMd5),
                _ => await api.SendText(o.Text),
            };
        }
        catch (Exception ex)
        {
            return (false, "网络不通：" + ex.Message);
        }
        if (OkOf(r)) return (true, "");
        return (false, ReasonOf(r, "服务端没接收"));
    }

    private static bool OkOf(JsonObject? r) =>
        r?["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && ok;

    private static string ReasonOf(JsonObject? r, string fallback)
    {
        if (r?["reason"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
        return fallback;
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SendInputAsync();
    }

    private void OnInputFocus(object sender, RoutedEventArgs e)
    {
        if (StickerBar.Visibility == Visibility.Visible) StickerBar.Visibility = Visibility.Collapsed;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_api is null) return;
        var on = InputBox.Text.Length > 0;
        if (on != _typingSent)
        {
            _typingSent = on;
            _ = _api.Typing(on);
        }
        // 停手 500ms 再落盘，别每按一个键写一次文件
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private void OnDraftTick(object? sender, EventArgs e)
    {
        _draftTimer.Stop();
        SaveDraft();
    }

    private void SaveDraft()
    {
        try
        {
            var text = InputBox.Text;
            if (text.Length == 0)
            {
                if (File.Exists(DraftPath)) File.Delete(DraftPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
            File.WriteAllText(DraftPath, text);
        }
        catch { }
    }

    private void RestoreDraft()
    {
        try
        {
            if (InputBox.Text.Length > 0 || !File.Exists(DraftPath)) return;
            var t = File.ReadAllText(DraftPath);
            if (t.Length == 0) return;
            InputBox.Text = t;
            InputBox.CaretIndex = t.Length;
        }
        catch { }
    }

    private void ClearDraft()
    {
        _draftTimer.Stop();
        try { if (File.Exists(DraftPath)) File.Delete(DraftPath); } catch { }
    }

    private async void OnPickImage(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "挑一张图",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            StateText.Text = "正在压图…";
            var name = Path.GetFileName(dlg.FileName);
            var (b64, mime) = await Task.Run(() => ImageUtil.Prepare(dlg.FileName));
            StateText.Text = "已连接";
            await SendOutAsync(new Outbox { Kind = "image", B64 = b64, Mime = mime, Name = name });
        }
        catch (Exception ex)
        {
            StateText.Text = "已连接";
            Toast("图片发不出去：" + ex.Message);
        }
    }

    private void OnVoice(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        if (!_recording)
        {
            var err = Mci.Start();
            if (err.Length > 0)
            {
                StateText.Text = "录音起不来：" + err;
                return;
            }
            _recording = true;
            VoiceGlyph.Text = "\uE71A";
            VoiceBtn.SetCurrentValue(BackgroundProperty, RecBg);
            StateText.Text = "正在录音…再点一下发出去";
            return;
        }

        _recording = false;
        VoiceGlyph.Text = "\uE720";
        VoiceBtn.ClearValue(BackgroundProperty);
        var path = Path.Combine(Path.GetTempPath(), $"aurachat-rec-{Guid.NewGuid():N}.wav");
        var got = Mci.StopAndRead(path);
        if (got is null)
        {
            StateText.Text = "这段没录上：" + (Mci.LastError.Length > 0 ? Mci.LastError : "未知原因");
            return;
        }
        StateText.Text = "已连接";
        _ = SendOutAsync(new Outbox { Kind = "voice", B64 = got.Value.Base64, Mime = "audio/wav", Dur = got.Value.Dur });
    }

    private async void OnSticker(object sender, RoutedEventArgs e)
    {
        if (StickerBar.Visibility == Visibility.Visible)
        {
            StickerBar.Visibility = Visibility.Collapsed;
            return;
        }
        StickerBar.Visibility = Visibility.Visible;
        await FillStickersAsync();
    }

    private async Task FillStickersAsync()
    {
        var api = _api;
        if (api is null) return;
        try
        {
            var list = await api.Stickers();
            StickerList.Children.Clear();
            if (list.Count == 0)
            {
                StickerList.Children.Add(new TextBlock
                {
                    Text = "表情库还是空的",
                    Foreground = Theme.Brush("Dim"),
                    Margin = new Thickness(8),
                });
                return;
            }
            foreach (var (id, name) in list)
                StickerList.Children.Add(MakeStickerButton(api, id, name));
        }
        catch (Exception ex)
        {
            StateText.Text = "表情没拉到：" + ex.Message;
        }
    }

    private UIElement MakeStickerButton(Api api, string id, string name)
    {
        var img = new Image { Width = 84, Height = 84, Stretch = Stretch.Uniform };
        var btn = new Button
        {
            Margin = new Thickness(4),
            Padding = new Thickness(2),
            ToolTip = name,
            Content = img,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        btn.Click += (_, _) => _ = SendOutAsync(new Outbox { Kind = "sticker", StickerMd5 = id });
        _ = Task.Run(async () =>
        {
            var bytes = await api.FetchBytes(api.StickerUrl(id));
            if (bytes is null) return;
            var bmp = ImageUtil.ToImage(bytes);
            if (bmp is null) return;
            Dispatcher.Invoke(() => img.Source = bmp);
        });
        return btn;
    }

    private void OnBubbleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Bubble b || b.IsDivider) return;
        if (b.VoicePlaying)
        {
            StopVoice();
            return;
        }
        if (b.Voice.Length > 0)
        {
            PlayVoice(b, b.Voice);
            return;
        }
        if (b.VoiceVisibility == Visibility.Visible && b.MediaId.Length > 0)
        {
            _ = PlayMediaVoiceAsync(b, b.MediaId);
            return;
        }
        if (b.Pic is not null)
        {
            ViewerImage.Source = b.Pic;
            ImageViewer.Visibility = Visibility.Visible;
        }
    }

    private void OnBubbleRightDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Bubble b && b.IsDivider) e.Handled = true;
    }

    private async Task PlayMediaVoiceAsync(Bubble b, string mediaId)
    {
        var api = _api;
        if (api is null) return;
        var idle = b.VoiceLen;
        if (MediaCache.Cached(mediaId) is null) b.VoiceLen = "下载中…";
        try
        {
            var path = await MediaCache.FetchAsync(api, mediaId);
            if (path is null)
            {
                b.VoiceLen = idle;
                StateText.Text = "这条语音已经过期了";
                Toast("这条语音已经过期了");
                return;
            }
            PlayVoiceFile(b, path, deleteOnEnd: false);
        }
        catch
        {
            b.VoiceLen = idle;
            StateText.Text = "这条语音已经过期了";
            Toast("这条语音已经过期了");
        }
    }

    private void PlayVoice(Bubble bubble, string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64);
            var file = Path.Combine(Path.GetTempPath(), $"aurachat-voice-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(file, bytes);
            PlayVoiceFile(bubble, file, deleteOnEnd: true);
        }
        catch
        {
            StateText.Text = "这条语音放不出来";
        }
    }

    private void PlayVoiceFile(Bubble bubble, string path, bool deleteOnEnd)
    {
        try
        {
            StopVoice(deleteOnEnd ? path : null);
            var p = new MediaPlayer();
            p.Open(new Uri(path));
            p.MediaEnded += (_, _) => Dispatcher.Invoke(() => StopVoice(deleteOnEnd ? path : null));
            p.Play();
            _voicePlayer = p;
            _voiceBubble = bubble;
            bubble.VoicePlaying = true;
            bubble.VoiceProgress = 0;
            _voiceTimer.Start();
        }
        catch
        {
            StopVoice();
            StateText.Text = "这条语音放不出来";
        }
    }

    private void OnVoiceTick(object? sender, EventArgs e)
    {
        if (_voicePlayer is null || _voiceBubble is null)
        {
            StopVoice();
            return;
        }
        var b = _voiceBubble;
        var pos = _voicePlayer.Position.TotalSeconds;
        var total = _voicePlayer.NaturalDuration.HasTimeSpan ? _voicePlayer.NaturalDuration.TimeSpan.TotalSeconds : 0;
        var shown = total > 0 ? total : b.VoiceTotalSec;
        b.VoiceProgress = total > 0 ? Math.Clamp(pos / total, 0, 1) : 0;
        b.VoiceLen = shown > 0
            ? $"{(int)pos}″ / {(int)Math.Round(shown)}″"
            : $"{(int)pos}″";
    }

    private void StopVoice(string? deletePath = null)
    {
        _voiceTimer.Stop();
        var p = _voicePlayer;
        _voicePlayer = null;
        if (p is not null)
        {
            try
            {
                p.Stop();
                p.Close();
            }
            catch { }
        }
        if (deletePath is not null)
        {
            try { if (File.Exists(deletePath)) File.Delete(deletePath); } catch { }
        }
        var b = _voiceBubble;
        _voiceBubble = null;
        if (b is null) return;
        b.VoicePlaying = false;
        b.VoiceProgress = 0;
        if (b.VoiceLenIdle.Length > 0) b.VoiceLen = b.VoiceLenIdle;
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Bubble b) return;
        if (b.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(b.Text);
            Toast("已复制");
        }
        catch
        {
            Toast("复制失败：剪贴板被别的程序占着");
        }
    }

    private async void OnSaveImage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Bubble b) return;
        var api = _api;
        byte[]? bytes = null;
        var ext = ".png";
        try
        {
            _msgs.TryGetValue(b.Id, out var m);
            if (b.MediaId.Length > 0 && api is not null)
            {
                bytes = await api.MediaBytes(b.MediaId);
                if (m is not null && m.MediaMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    ext = ExtOfMime(m.MediaMime);
            }
            else if (m is not null && m.Kind == "sticker" && m.Sticker.Length > 0 && api is not null)
            {
                bytes = await api.FetchBytes(api.StickerUrl(m.Sticker));
            }
            else if (m is not null && m.Image.Length > 0)
            {
                bytes = Convert.FromBase64String(m.Image);
                if (m.ImageMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    ext = ExtOfMime(m.ImageMime);
            }

            if (bytes is null || bytes.Length == 0)
            {
                Toast("这张图已经取不到了（可能过期了）");
                return;
            }
            var file = Path.Combine(PicturesDir(), $"aurachat-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            await File.WriteAllBytesAsync(file, bytes);
            Toast("图片已存到 图片\\AuraChat");
            MessageBox.Show(this, "图片已经存到：\n" + file, "保存图片", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Toast("保存失败：" + ex.Message);
        }
    }

    private async void OnSaveVoice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Bubble b) return;
        var api = _api;
        byte[]? bytes = null;
        var ext = ".wav";
        try
        {
            _msgs.TryGetValue(b.Id, out var m);
            if (b.MediaId.Length > 0 && api is not null)
            {
                bytes = await api.MediaBytes(b.MediaId);
                if (m is not null && m.MediaMime.Length > 0) ext = ExtOfAudioMime(m.MediaMime);
            }
            else if (m is not null && m.Voice.Length > 0)
            {
                bytes = Convert.FromBase64String(m.Voice);
            }
            if (bytes is null || bytes.Length == 0)
            {
                Toast("这条语音已经过期了");
                return;
            }
            var file = Path.Combine(PicturesDir(), $"aurachat-voice-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            await File.WriteAllBytesAsync(file, bytes);
            Toast("语音已存到 图片\\AuraChat");
            MessageBox.Show(this, "语音已经存到：\n" + file, "保存语音", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Toast("保存失败：" + ex.Message);
        }
    }

    private static string PicturesDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AuraChat");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ExtOfMime(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".png",
    };

    private static string ExtOfAudioMime(string mime) => mime.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/webm" => ".webm",
        "audio/mp4" => ".m4a",
        _ => ".wav",
    };

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ImageViewer.Visibility == Visibility.Visible)
            {
                ImageViewer.Visibility = Visibility.Collapsed;
                ViewerImage.Source = null;
                e.Handled = true;
                return;
            }
            if (SearchBar.Visibility == Visibility.Visible)
            {
                CloseSearch();
                e.Handled = true;
                return;
            }
            if (StickerBar.Visibility == Visibility.Visible)
            {
                StickerBar.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnOpenSettings(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnCloseViewer(object sender, MouseButtonEventArgs e)
    {
        ImageViewer.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
    }

    private void Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    // 标题栏跟深/浅色走，否则深色主题上顶一条白边
    private void ApplyTitleBar()
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            var dark = Theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(h, 19, ref dark, sizeof(int)); // 老版本 Windows 的同一个参数
        }
        catch { }
    }

    private void OnThemeChanged()
    {
        ApplyBackdrop();
        RebuildRows(keepOffset: true);
    }

    private void ApplyBackdrop()
    {
        ApplyTitleBar();
        var glass = Theme.IsGlass;
        BackdropLayer.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        BackdropImage.Source = _wallpaper;
        // 亚克力糊得更狠一点，毛玻璃留点轮廓
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
            // 没背景图时用竖向渐变兜底（和安卓/网页同观感），纯色蒙层会把亚克力糊成白板
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
            // 无背景图又非玻璃主题：靠这层纯色撑住半透明观感
            BackdropTint.Opacity = _wallpaper is null ? 0.96 : (Theme.Mode == AppTheme.Frosted ? 0.62 : 0.8);
        }
        ChatPanel.Background = glass ? Brushes.Transparent : Theme.Brush("Bg");
    }

    private void InitTray()
    {
        try
        {
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            _tray = new WinForms.NotifyIcon
            {
                Icon = ico,
                Visible = true,
                Text = "AuraChat",
            };
            _tray.DoubleClick += (_, _) => ShowWindow();
            _tray.BalloonTipClicked += (_, _) => ShowWindow();
            _tray.ContextMenuStrip = new WinForms.ContextMenuStrip();
            _tray.ContextMenuStrip.Items.Add("显示 AuraChat", null, (_, _) => ShowWindow());
            _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => ExitApp());
        }
        catch
        {
            _tray = null;
        }
    }

    private void ShowWindow()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // 从通知/托盘点回来时进程可能不在前台，光 Activate() 会被 Windows 前台锁挡掉，
        // 窗口只在任务栏闪一下；走 Win32 还原 + 置前
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ShowWindowAsync(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        Activate();
    }

    private void ExitApp()
    {
        _closed = true;
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        Hide();
        try { _tray?.ShowBalloonTip(2000, "AuraChat", "窗口收进托盘了，右键托盘图标可以退出。", WinForms.ToolTipIcon.Info); } catch { }
    }

    private void Notify(Msg m)
    {
        // Windows 把气泡通知排队逐条放，每条消息都弹会积压，点哪条都得等队列清空；
        // 改成隔一段时间只报一次，其余交给托盘提示和标题未读数
        var now = DateTime.UtcNow;
        if (now - _lastTipAt < TipGap) return;
        _lastTipAt = now;
        var body = m.Kind switch
        {
            "image" => "[图片]",
            "sticker" => "[表情]",
            "voice" => m.Dur > 0 ? $"[语音 {m.Dur}s]" : "[语音]",
            _ => m.Text,
        };
        if (body.Length > 120) body = body[..120] + "…";
        try
        {
            _tray ??= CreateTray();
            _tray?.ShowBalloonTip(4000, _unread > 0 ? $"AuraChat ({_unread})" : "AuraChat", body, WinForms.ToolTipIcon.None);
        }
        catch { }
    }

    private WinForms.NotifyIcon? CreateTray()
    {
        try
        {
            var tray = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                Visible = true,
                Text = "AuraChat",
            };
            tray.DoubleClick += (_, _) => ShowWindow();
            tray.BalloonTipClicked += (_, _) => ShowWindow();
            return tray;
        }
        catch { return null; }
    }

    private void OnNavChat(object sender, RoutedEventArgs e) { }

    private void OnNavSpace(object sender, RoutedEventArgs e) => OpenSection("空间");

    private void OnNavMood(object sender, RoutedEventArgs e) => OpenSection("心情");

    private void OnNavOverview(object sender, RoutedEventArgs e) => OpenSection("概览");

    private void OnNavMemory(object sender, RoutedEventArgs e) => OpenSection("记忆");

    private void OnNavLogs(object sender, RoutedEventArgs e) => OpenSection("日志");

    private void OnNavSettings(object sender, RoutedEventArgs e) => OnOpenSettings(sender, e);

    // 概览/记忆/日志只有主人能看到（对齐 web 端 role 判断）
    private void SetOwnerNav(bool owner)
    {
        var v = owner ? Visibility.Visible : Visibility.Collapsed;
        NavOverviewBtn.Visibility = v;
        NavMemoryBtn.Visibility = v;
        NavLogsBtn.Visibility = v;
    }

    private void OpenSection(string section)
    {
        if (_closed) return;
        if (_api is null)
        {
            Toast("先连上服务器");
            return;
        }
        var w = new SettingsWindow(_api, _api.Base, section, standalone: true) { Owner = this };
        w.ShowDialog();
        if (w.LoggedOut)
        {
            DoLogout();
            return;
        }
        applyAfterSettings();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        if (_api is null)
        {
            Toast("先连上服务器再改设置");
            return;
        }
        var w = new SettingsWindow(_api, _api.Base) { Owner = this };
        w.ShowDialog();
        if (w.LoggedOut)
        {
            DoLogout();
            return;
        }
        applyAfterSettings();
    }

    private void applyAfterSettings()
    {
        _ = LoadAvatarAsync();
        _ = LoadWallpaperAsync();
        ApplyBackdrop();
    }

    private void OnLogout(object sender, RoutedEventArgs e) => DoLogout();

    private void DoLogout()
    {
        try { _streamCts?.Cancel(); } catch { }
        try { _api?.Dispose(); } catch { }
        _api = null;
        _streamCts = null;
        _msgs.Clear();
        _pendingRows.Clear();
        _items.Clear();
        _lastDayKey = "";
        _hasMore = false;
        _loadingOlder = false;
        _unread = 0;
        _wallpaper = null;
        _search = "";
        SearchBar.Visibility = Visibility.Collapsed;
        AvatarImage.Source = null;
        AvatarBox.Visibility = Visibility.Collapsed;
        ApplyBackdrop();
        UpdateUnread();
        UpdateOlder();
        Save("");
        ChatPanel.Visibility = Visibility.Collapsed;
        ConnectPanel.Visibility = Visibility.Visible;
        PassBox.Clear();
        ConnectMsg.Text = "";
        StateText.Text = "连接中…";
        TitleText.Text = "AuraChat";
        try { if (File.Exists(DraftPath)) File.Delete(DraftPath); } catch { }
    }
}
