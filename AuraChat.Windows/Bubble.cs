using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace AuraChat.Windows;

// 乐观气泡：POST api/messages 只回 {ok}，真消息等 SSE 回来
// 发送失败留着它，重试原样重发
public sealed class Outbox
{
    public string Kind { get; init; } = "text";
    public string Text { get; init; } = "";
    public string B64 { get; init; } = "";
    public string Mime { get; init; } = "image/png";
    public string Name { get; init; } = "image";
    public int Dur { get; init; }
    public string StickerMd5 { get; init; } = "";

    public string Label => Kind switch
    {
        "image" => "[图片]",
        "voice" => Dur > 0 ? $"[语音 {Dur}″]" : "[语音]",
        "sticker" => "[表情]",
        _ => Text,
    };
}

// 列表一行：消息气泡，或跨天分隔（同一模板两套外观）
// 只有内容、播放进度、发送状态会变并通知，其余 init 只读
public sealed class Bubble : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "text";
    // 原始文字，右键「复制文字」用；Body 是省略后的显示文案
    public string Text { get; init; } = "";
    public string Body { get; init; } = "";
    public string Clock { get; init; } = "";
    public HorizontalAlignment Align { get; init; } = HorizontalAlignment.Left;

    // 老消息的内联 base64 音频；新消息走 MediaId
    public string Voice { get; init; } = "";
    public int VoiceTotalSec { get; init; }
    public string VoiceLenIdle { get; init; } = "";
    public Visibility VoiceVisibility { get; init; } = Visibility.Collapsed;

    public string MediaId { get; init; } = "";

    public Visibility PlainTextVisibility { get; init; } = Visibility.Visible;

    public bool IsDivider { get; init; }
    public string DividerText { get; init; } = "";
    public Visibility DividerVisibility => IsDivider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => IsDivider ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CopyVisibility =>
        !IsDivider && Kind == "text" && Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SaveImageVisibility =>
        !IsDivider && Kind is "image" or "sticker" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SaveVoiceVisibility =>
        !IsDivider && Kind == "voice" ? Visibility.Visible : Visibility.Collapsed;

    public Outbox? Out { get; init; }

    private Brush _bg = Brushes.Transparent;
    public Brush Bg
    {
        get => _bg;
        set { _bg = value; Raise(nameof(Bg)); }
    }

    private string _voiceLen = "";
    public string VoiceLen
    {
        get => _voiceLen;
        set { _voiceLen = value; Raise(nameof(VoiceLen)); }
    }

    private double _voiceProgress;
    public double VoiceProgress
    {
        get => _voiceProgress;
        set { _voiceProgress = value; Raise(nameof(VoiceProgress)); }
    }

    private bool _voicePlaying;
    public bool VoicePlaying
    {
        get => _voicePlaying;
        set
        {
            _voicePlaying = value;
            Raise(nameof(VoicePlaying));
            Raise(nameof(VoiceBarVisibility));
        }
    }

    public Visibility VoiceBarVisibility => _voicePlaying ? Visibility.Visible : Visibility.Collapsed;

    private ImageSource? _pic;
    public ImageSource? Pic
    {
        get => _pic;
        set
        {
            _pic = value;
            Raise(nameof(Pic));
            Raise(nameof(PicVisibility));
            Raise(nameof(TextVisibility));
        }
    }

    public Visibility PicVisibility => _pic is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => _pic is null ? PlainTextVisibility : Visibility.Collapsed;

    private bool _failed;
    public bool Failed
    {
        get => _failed;
        private set
        {
            _failed = value;
            Raise(nameof(Failed));
            Raise(nameof(RetryVisibility));
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            Raise(nameof(StatusText));
            Raise(nameof(StatusVisibility));
        }
    }

    private string _statusTip = "";
    public string StatusTip
    {
        get => _statusTip;
        private set { _statusTip = value; Raise(nameof(StatusTip)); }
    }

    private Brush _statusBrush = Theme.Brush("Dim");
    public Brush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; Raise(nameof(StatusBrush)); }
    }

    public Visibility StatusVisibility => _statusText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RetryVisibility => _failed ? Visibility.Visible : Visibility.Collapsed;

    // 常态底色另存一份，失败改掉 Bg 后还能还原；主题重排时会重新赋。
    public Brush NormalBg { get; init; } = Brushes.Transparent;

    // 失败态固定灰底红字，不跟主题走（深浅色下都看得清）。
    private static readonly Brush FailBg = Literal(0x3A, 0x3F, 0x46);
    private static readonly Brush FailBrush = Literal(0xD9, 0x3B, 0x3B);

    private static Brush Literal(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public void MarkSending()
    {
        Failed = false;
        Bg = NormalBg;
        StatusBrush = Theme.Brush("Dim");
        StatusTip = "";
        StatusText = "发送中…";
    }

    public void MarkFailed(string? reason = null)
    {
        Failed = true;
        Bg = FailBg;
        StatusBrush = FailBrush;
        StatusText = "发送失败";
        StatusTip = string.IsNullOrWhiteSpace(reason) ? "没发出去，点「重试」再发一次" : reason!;
    }

    public void MarkSent()
    {
        Failed = false;
        Bg = NormalBg;
        StatusText = "";
        StatusTip = "";
    }

    public static Bubble Divider(string text) => new()
    {
        Id = "divider:" + text,
        IsDivider = true,
        DividerText = text,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
