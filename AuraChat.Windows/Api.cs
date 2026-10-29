using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace AuraChat.Windows;

public sealed record Msg(
    string Id, string Who, string Text, string Kind, long T,
    string Sticker, string Name, string Caption, string Voice, int Dur, string Heard, string Image, string ImageMime,
    string MediaId = "", string MediaMime = "", bool MediaExpired = false);

public sealed record Page(List<Msg> Messages, long OldestAt, bool HasMore, int FileIndex, int FileTotal);

public sealed class Api : IDisposable
{
    public string Base { get; }
    public string Token { get; set; } = "";

    // 普通请求：20 秒够用。
    private readonly HttpClient _http;

    // 慢接口：AI 写日记/整理记忆/合成语音，20 秒根本不够。
    private readonly HttpClient _slow;

    // SSE 长连接不能有整体超时。
    private readonly HttpClient _stream;

    // 默认 handler 走系统 CA 校验；只有用户明确信任过自签证书时才传 insecure 忽略错误。
    public Api(string baseUrl, bool insecure = false)
    {
        Base = baseUrl.TrimEnd('/');
        _http = Make(insecure, TimeSpan.FromSeconds(20));
        _slow = Make(insecure, TimeSpan.FromSeconds(180));
        _stream = Make(insecure, Timeout.InfiniteTimeSpan);
    }

    private static HttpClient Make(bool insecure, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (insecure) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> AcceptedCerts = new();

    public static bool IsCertAccepted(string key) => AcceptedCerts.ContainsKey(key);

    public static void AcceptCert(string key, string fingerprint) => AcceptedCerts[key] = fingerprint;

    public static string? FingerprintOf(X509Certificate2 cert)
    {
        try
        {
            var sha = SHA256.HashData(cert.GetRawCertData());
            return string.Join(":", sha.Select(b => b.ToString("X2")));
        }
        catch { return null; }
    }

    // 用忽略证书错误的一次性 client 发请求，从校验回调里拿证书；不是 https 或连不上返回 null。
    public static async Task<string?> CaptureFingerprintAsync(string url)
    {
        try
        {
            X509Certificate2? got = null;
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => { got = cert; return true; };
            using var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var res = await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
            await res.Content.ReadAsByteArrayAsync();
            return got is null ? null : FingerprintOf(got);
        }
        catch { return null; }
    }

    private HttpRequestMessage Req(HttpMethod m, string path, JsonNode? body = null)
    {
        var r = new HttpRequestMessage(m, Base + "/" + path.TrimStart('/'));
        if (Token.Length > 0) r.Headers.Add("Authorization", "Bearer " + Token);
        if (body is not null) r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return r;
    }

    private async Task<JsonObject> Send(HttpClient client, HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        try
        {
            using var res = await client.SendAsync(Req(m, path, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject { ["ok"] = res.IsSuccessStatusCode };
            try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["ok"] = false }; }
            catch { return new JsonObject { ["ok"] = false, ["reason"] = "服务端回的不是 JSON" }; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = "服务端太久了，没等到回应" };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = ex.Message };
        }
    }

    private Task<JsonObject> Call(HttpMethod m, string path, JsonNode? body = null, CancellationToken ct = default) =>
        Send(_http, m, path, body, ct);

    private Task<JsonObject> CallSlow(HttpMethod m, string path, JsonNode? body = null) =>
        Send(_slow, m, path, body, CancellationToken.None);

    public Task<JsonObject> Get(string path, CancellationToken ct = default) => Call(HttpMethod.Get, path, null, ct);
    public Task<JsonObject> Post(string path, JsonNode? body = null, CancellationToken ct = default) => Call(HttpMethod.Post, path, body, ct);
    public Task<JsonObject> Patch(string path, JsonNode? body = null) => Call(HttpMethod.Patch, path, body);
    public Task<JsonObject> Delete(string path) => Call(HttpMethod.Delete, path);

    // 会让 AI 想一会儿的接口（日记、记忆整理、语音合成、测试按钮），走 180 秒的 client。
    public Task<JsonObject> PostSlow(string path, JsonNode? body = null) => CallSlow(HttpMethod.Post, path, body);
    public Task<JsonObject> GetSlow(string path) => CallSlow(HttpMethod.Get, path);

    public Task<JsonObject> Login(string user, string pass) =>
        Call(HttpMethod.Post, "api/login", new JsonObject { ["username"] = user, ["password"] = pass });

    public Task<JsonObject> State() => Call(HttpMethod.Get, "api/state");

    public Task<JsonObject> Logout() => Call(HttpMethod.Post, "api/logout");

    // file=last 取最新一整片（首屏），file=prev 取 before 之前一整片；
    // mode=page 按条数分页（before 游标 + limit），老接口。
    public async Task<Page> MessagesPage(string mode = "last", long before = 0, int limit = 100, CancellationToken ct = default)
    {
        var q = new StringBuilder("api/messages?");
        if (mode is "last" or "prev")
        {
            q.Append("file=").Append(mode);
            if (mode == "prev" && before > 0) q.Append("&before=").Append(before);
        }
        else
        {
            q.Append("limit=").Append(Math.Clamp(limit, 1, 100));
            if (before > 0) q.Append("&before=").Append(before);
        }
        var r = await Call(HttpMethod.Get, q.ToString(), null, ct);
        var list = new List<Msg>();
        if (r["messages"] is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject o) list.Add(ParseMsg(o));
        var f = r["file"] as JsonObject;
        return new Page(
            list,
            r["oldestAt"]?.GetValue<long>() ?? 0,
            r["hasMore"] is JsonValue hv && hv.TryGetValue<bool>(out var hm) && hm,
            f?["index"]?.GetValue<int>() ?? -1,
            f?["total"]?.GetValue<int>() ?? 0);
    }

    public async Task<List<Msg>> Messages(CancellationToken ct = default) =>
        (await MessagesPage("last", 0, 100, ct)).Messages;

    private static Msg ParseMsg(JsonObject o) => new(
        S(o, "id"), S(o, "who", "bot"), S(o, "text"), S(o, "kind", "text"),
        o["t"]?.GetValue<long>() ?? 0, S(o, "sticker"), S(o, "name"), S(o, "caption"),
        S(o, "voice"), o["dur"]?.GetValue<int>() ?? 0, S(o, "heard"),
        S(o, "image"), S(o, "imageMime"),
        S(o, "mediaId"), S(o, "mediaMime"),
        o["mediaExpired"] is JsonValue ev && ev.TryGetValue<bool>(out var expired) && expired);

    private static string S(JsonObject o, string key, string def = "") =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;

    public Task<JsonObject> SendText(string text) =>
        Call(HttpMethod.Post, "api/messages", new JsonObject { ["text"] = text });

    public Task<JsonObject> SendSticker(string md5) =>
        Call(HttpMethod.Post, "api/messages", new JsonObject { ["sticker"] = md5 });

    // 语音发 WAV 的 base64，服务端会顺手做一次转写。
    public Task<JsonObject> SendVoice(string base64, string mime, int dur) =>
        Call(HttpMethod.Post, "api/messages", new JsonObject
        {
            ["voice"] = new JsonObject
            {
                ["data"] = base64,
                ["mime"] = mime,
                ["dur"] = dur,
            },
        });

    // 图片同样走 base64，服务端落盘后回 mediaId。
    public Task<JsonObject> SendImage(string base64, string mediaType, string name = "image") =>
        Call(HttpMethod.Post, "api/messages", new JsonObject
        {
            ["images"] = new JsonArray(new JsonObject
            {
                ["data"] = base64,
                ["mediaType"] = mediaType,
                ["name"] = name,
            }),
        });

    public async Task<List<(string Md5, string Name)>> Stickers()
    {
        var r = await Call(HttpMethod.Get, "api/stickers");
        var list = new List<(string, string)>();
        if (r["items"] is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var md5 = S(o, "md5");
            if (md5.Length > 0) list.Add((md5, S(o, "name")));
        }
        return list;
    }

    public async Task<byte[]?> FetchBytes(string url, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url.StartsWith("http") ? url : Base + "/" + url.TrimStart('/'));
            if (Token.Length > 0) req.Headers.Add("Authorization", "Bearer " + Token);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            // 服务端出错时回的是 200 + {"ok":false,...}，别把它当图片
            if (bytes.Length > 1 && bytes[0] == (byte)'{') return null;
            return bytes.Length > 0 ? bytes : null;
        }
        catch { return null; }
    }

    public Task Typing(bool on) =>
        Call(HttpMethod.Post, "api/typing", new JsonObject { ["typing"] = on }).ContinueWith(_ => { });

    public string StickerUrl(string md5) => $"{Base}/api/sticker/{md5}?token={Uri.EscapeDataString(Token)}";

    // 媒体直链（mediaId 自带扩展名）。只在必须给 URL 的地方用，拉字节走 MediaBytes。
    public string MediaUrl(string mediaId) => $"{Base}/api/media/{mediaId}?token={Uri.EscapeDataString(Token)}";

    // 服务端对 /api/media 给了 immutable 强缓存，上层会落盘，只会在首次或缓存丢了时调到。
    // 404 = 媒体已被清理，返回 null。
    public Task<byte[]?> MediaBytes(string mediaId, CancellationToken ct = default) =>
        FetchBytes("api/media/" + mediaId, ct);

    // SSE 长连接；断线由调用方重连。
    // 服务端只发 data:（不写 event 行），空 event 要当成 message，具体类型看 JSON 里的 type。
    public async Task StreamAsync(Action<Msg> onMessage, Action onOpen, CancellationToken ct, Action<bool>? onTyping = null,
        Action<long, bool>? onHistory = null)
    {
        using var req = Req(HttpMethod.Get, "api/stream?token=" + Uri.EscapeDataString(Token));
        using var res = await _stream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException("SSE " + (int)res.StatusCode + " " + res.ReasonPhrase);
        onOpen();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string evt = "";
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                Dispatch(evt, data.ToString(), onMessage, onTyping, onHistory);
                evt = "";
                data.Clear();
            }
            else if (line[0] == ':')
            {
                // 心跳 / connected 注释行
            }
            else if (line.StartsWith("event:")) evt = line[6..].Trim();
            else if (line.StartsWith("data:")) data.Append(line[5..].TrimStart());
        }
    }

    private static void Dispatch(string evt, string payload, Action<Msg> onMessage, Action<bool>? onTyping,
        Action<long, bool>? onHistory)
    {
        if (payload.Length == 0) return;
        JsonObject? root;
        try { root = JsonNode.Parse(payload) as JsonObject; }
        catch { return; }
        if (root is null) return;
        var type = evt.Length > 0 ? evt : S(root, "type");
        if (type == "message")
        {
            if (root["message"] is not JsonObject obj) obj = root;
            onMessage(ParseMsg(obj));
        }
        else if (type == "typing" && onTyping is not null)
        {
            onTyping(root["on"] is JsonValue v && v.TryGetValue<bool>(out var on) && on);
        }
        else if (type == "history" && onHistory is not null)
        {
            // 服务端按 keepDays 清过片：比 oldestAt 更早的消息和媒体都没了
            var oldest = root["oldestAt"]?.GetValue<long>() ?? 0;
            var more = root["hasMore"] is JsonValue hv && hv.TryGetValue<bool>(out var hm) && hm;
            onHistory(oldest, more);
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _http.Dispose();
        _slow.Dispose();
    }
}
