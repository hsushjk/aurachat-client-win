using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AuraChat.Windows;

// MCI 录出的位深/采样率随声卡驱动变，服务端 toMono16k 只吃 PCM16/单声道/16k，
// 所以录完自己重采样，别指望驱动。
public static class Mci
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? ret, int retLen, IntPtr hwnd);

    private const string Alias = "aurachatrec";
    private const int TargetRate = 16000;

    public static string LastError { get; private set; } = "";

    public static string Start()
    {
        LastError = "";
        Close();
        if (Send($"open new type waveaudio alias {Alias}") != 0) return "打不开录音设备";
        Send($"set {Alias} time format milliseconds");
        Send($"set {Alias} samplespersec {TargetRate} bitspersec 16 channels 1 bytespersec 32000 alignment 2");
        return Send($"record {Alias}") == 0 ? "" : "录音启动失败";
    }

    public static (string Base64, int Dur)? StopAndRead(string path)
    {
        LastError = "";
        if (Send($"stop {Alias}") != 0)
        {
            Close();
            LastError = "停止录音失败";
            return null;
        }
        var sb = new StringBuilder(64);
        mciSendString($"status {Alias} length", sb, sb.Capacity, IntPtr.Zero);
        int.TryParse(sb.ToString().Trim(), out var ms);
        var saved = Send($"save {Alias} \"{path}\"") == 0;
        Close();
        if (!saved || !File.Exists(path))
        {
            LastError = "录音存不下来";
            return null;
        }

        var raw = File.ReadAllBytes(path);
        try { File.Delete(path); } catch { }
        if (raw.Length < 1000)
        {
            LastError = "这段太短了";
            return null;
        }

        var wav = Normalize(raw);
        if (wav is null)
        {
            LastError = "录音格式解码不了";
            return null;
        }
        if (wav.Length < 8000)
        {
            LastError = "这段太短了";
            return null;
        }
        return (Convert.ToBase64String(wav), Math.Clamp(ms / 1000, 1, 60));
    }

    private static void Close() => Send($"close {Alias}");

    private static int Send(string cmd) => mciSendString(cmd, null, 0, IntPtr.Zero);

    private static byte[]? Normalize(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        int channels = 0, rate = 0, bits = 0, format = 0;
        byte[]? data = null;
        var off = 12;
        while (off + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, off, 4);
            var size = BitConverter.ToInt32(wav, off + 4);
            if (size < 0) return null;
            var start = off + 8;
            if (id == "fmt " && start + 16 <= wav.Length)
            {
                format = BitConverter.ToInt16(wav, start);
                channels = BitConverter.ToInt16(wav, start + 2);
                rate = BitConverter.ToInt32(wav, start + 4);
                bits = BitConverter.ToInt16(wav, start + 14);
            }
            else if (id == "data")
            {
                data = wav[(start)..(Math.Min(wav.Length, start + size))];
                break;
            }
            off = start + size + (size & 1);
        }
        if (data is null || channels <= 0 || rate <= 0) return null;

        var frames = data.Length / (channels * Math.Max(1, bits / 8));
        if (frames <= 0) return null;
        var mono = new double[frames];

        for (var i = 0; i < frames; i++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var at = (i * channels + c) * Math.Max(1, bits / 8);
                double v;
                if (format == 3 && bits == 32) v = BitConverter.ToSingle(data, at);
                else if (bits == 8) v = (data[at] - 128) / 128.0;
                else if (bits == 16) v = BitConverter.ToInt16(data, at) / 32768.0;
                else if (bits == 24) v = ((data[at] | (data[at + 1] << 8) | (data[at + 2] << 16)) << 8 >> 8) / 8388608.0;
                else if (bits == 32) v = BitConverter.ToInt32(data, at) / 2147483648.0;
                else return null;
                sum += v;
            }
            mono[i] = sum / channels;
        }

        var outLen = Math.Max(1, (int)(frames * (double)TargetRate / rate));
        var pcm = new byte[outLen * 2];
        for (var i = 0; i < outLen; i++)
        {
            var pos = i * (double)rate / TargetRate;
            var a = (int)pos;
            var b = Math.Min(frames - 1, a + 1);
            var t = pos - a;
            var v = mono[Math.Min(a, frames - 1)] * (1 - t) + mono[b] * t;
            var s = (short)Math.Clamp((int)Math.Round(v * 32767), -32768, 32767);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return BuildWav(pcm);
    }

    private static byte[] BuildWav(byte[] pcm)
    {
        var outBuf = new byte[44 + pcm.Length];
        void Le32(int v, int at)
        {
            outBuf[at] = (byte)(v & 0xFF);
            outBuf[at + 1] = (byte)((v >> 8) & 0xFF);
            outBuf[at + 2] = (byte)((v >> 16) & 0xFF);
            outBuf[at + 3] = (byte)((v >> 24) & 0xFF);
        }
        void Le16(int v, int at)
        {
            outBuf[at] = (byte)(v & 0xFF);
            outBuf[at + 1] = (byte)((v >> 8) & 0xFF);
        }
        Encoding.ASCII.GetBytes("RIFF").CopyTo(outBuf, 0);
        Le32(36 + pcm.Length, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(outBuf, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(outBuf, 12);
        Le32(16, 16);
        Le16(1, 20);
        Le16(1, 22);
        Le32(TargetRate, 24);
        Le32(TargetRate * 2, 28);
        Le16(2, 32);
        Le16(16, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(outBuf, 36);
        Le32(pcm.Length, 40);
        pcm.CopyTo(outBuf, 44);
        return outBuf;
    }
}
