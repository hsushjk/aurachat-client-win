using System.IO;

namespace AuraChat.Windows;

// 媒体落盘到 media\<mediaId>：服务端给了 immutable，取到一次就能一直用。
// Windows 默认不更新最后访问时间，命中时自己写 LastWriteTimeUtc 当 LRU 依据。
// 上限 300MB，超了从最旧的删。
public static class MediaCache
{
    public const long LimitBytes = 300L * 1024 * 1024;

    private static readonly object TrimGate = new();
    private static bool _trimming;

    public static string Dir => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath)!, "media");

    public static string PathOf(string mediaId)
    {
        var sb = new System.Text.StringBuilder(mediaId.Length);
        foreach (var c in mediaId)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var name = sb.ToString();
        if (name.Length == 0) name = "media";
        if (name.Length > 80) name = name[^80..];
        return Path.Combine(Dir, name);
    }

    public static string? Cached(string mediaId)
    {
        var path = PathOf(mediaId);
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length <= 0) return null;
            // 30 秒内不重写时间：翻页重排会反复命中，省磁盘写。
            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(30))
            {
                try { info.LastWriteTimeUtc = DateTime.UtcNow; } catch { }
            }
            return path;
        }
        catch { return null; }
    }

    public static async Task<string?> FetchAsync(Api api, string mediaId, CancellationToken ct = default)
    {
        var hit = Cached(mediaId);
        if (hit is not null) return hit;
        var bytes = await api.MediaBytes(mediaId, ct);
        if (bytes is null || bytes.Length == 0) return null;
        var path = PathOf(mediaId);
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, true);
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
            TrimInBackground();
            return path;
        }
        catch
        {
            return null;
        }
    }

    // 后台 trim，不挡渲染。
    public static void TrimInBackground()
    {
        lock (TrimGate)
        {
            if (_trimming) return;
            _trimming = true;
        }
        _ = Task.Run(() =>
        {
            try { Trim(); }
            finally { lock (TrimGate) _trimming = false; }
        });
    }

    public static void Trim(long limit = LimitBytes)
    {
        try
        {
            var dir = Dir;
            if (!Directory.Exists(dir)) return;
            var files = new DirectoryInfo(dir).GetFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Name.EndsWith(".part") == false)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= limit) return;
            foreach (var f in files)
            {
                if (total <= limit) break;
                var len = f.Length;
                try
                {
                    f.Delete();
                    total -= len;
                }
                catch { /* 被 MediaPlayer 之类占用，下轮再删 */ }
            }
        }
        catch { }
    }
}
