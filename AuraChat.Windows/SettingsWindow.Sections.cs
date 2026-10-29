using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AuraChat.Windows;

public partial class SettingsWindow
{
    private async Task BuildMasterAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var enabled = Chk("启用", B(cfg["enabled"], true));

        AddCard("总开关",
            Switched(enabled, "停用所有功能：不回消息、不主动搭话、不写动态、不自驱。"),
            Btn("保存", async () =>
            {
                var patch = new JsonObject { ["enabled"] = enabled.IsChecked == true };
                if (await PatchConfigAsync(patch)) await BuildMasterAsync();
            }, "primary"));
    }

    private async Task BuildIdentityAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;

        var botName = Input(S(cfg["botName"]), 240);
        var botTag = Input(S(cfg["botTag"]), 320);
        var ownerName = Input(S(cfg["ownerName"]), 240);

        // 人设用下拉 + 内联编辑，不另开窗口
        var personas = new List<(string Id, string Name)>();
        try
        {
            var pr = await _api.Get("api/personas");
            foreach (var it in Arr(pr["personas"]) ?? Arr(pr["list"]) ?? new JsonArray())
            {
                var o = Obj(it);
                if (o is null) continue;
                var id = S(o["id"]);
                if (id.Length == 0) continue;
                var nm = S(o["name"], id);
                personas.Add((id, nm));
            }
        }
        catch { }

        var currentPersona = S(Sub(cfg, "persona")["current"]);
        var labels = new List<string> { "随主人设（不指定）" };
        var values = new List<string> { "" };
        foreach (var p in personas)
        {
            labels.Add(p.Name);
            values.Add(p.Id);
        }
        labels.Add("＋ 新建一个人设…");
        values.Add("__new__");
        var personaSel = Sel(labels, values, currentPersona);

        var avatarBtn = new Button
        {
            Content = "选张图",
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Margin = new Thickness(0, 0, 8, 8),
        };
        avatarBtn.Click += async (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif",
                Title = "选一张头像",
            };
            if (dlg.ShowDialog() != true) return;
            if (await UploadImage(dlg.FileName, "api/avatar")) Say("头像换好了");
        };
        var avatarDefault = Btn("用默认图标", async () =>
        {
            if (await UploadTransparent("api/avatar", "头像已经恢复成默认图标"))
                Say("");
        });

        AddCard("身份",
            Field("名字", botName, "Ta 的名字，一般跟着人设走"),
            Field("个性签名", botTag, "可留空；显示在聊天页名字后面"),
            Field("怎么称呼我", ownerName, "Ta 对你的称呼"),
            Field("人设", personaSel),
            Wrap(Btn("编辑当前人设", () =>
            {
                var sel = SelValue(personaSel);
                _personaEdit = sel == "__new__" ? "" : sel;
                return BuildIdentityAsync();
            })),
            Field("头像", Row(avatarBtn, avatarDefault), "选一张图当 Ta 的头像"),
            Btn("保存身份", async () =>
            {
                var sel = SelValue(personaSel);
                if (sel == "__new__") { Bad("先在下面把人设建好，再选它。"); return; }
                var patch = new JsonObject
                {
                    ["botName"] = botName.Text.Trim(),
                    ["botTag"] = botTag.Text.Trim(),
                    ["ownerName"] = ownerName.Text.Trim(),
                    ["persona"] = new JsonObject { ["current"] = sel },
                };
                if (await PatchConfigAsync(patch, "身份存好了", "身份没存上")) await BuildIdentityAsync();
            }, "primary"));

        if (_personaEdit is not null)
            await AddPersonaEditorAsync(_personaEdit);
    }

    private async Task AddPersonaEditorAsync(string id)
    {
        var isNew = id.Length == 0;
        var nameBox = Input("", 240);
        var tagsBox = Input("", 320);
        var bodyBox = Input("", 560, multi: true, height: 150);
        if (!isNew)
        {
            try
            {
                var r = await _api.Get("api/personas/detail?id=" + Uri.EscapeDataString(id));
                var p = Obj(r["persona"]) ?? Obj(r["detail"]);
                if (p is not null)
                {
                    nameBox.Text = S(p["name"]);
                    tagsBox.Text = string.Join("，", (Arr(p["tags"]) ?? new JsonArray()).Select(t => S(t)));
                    bodyBox.Text = S(p["body"]);
                }
            }
            catch { }
        }

        AddCard(isNew ? "新建人设" : "编辑人设",
            Field("名字", nameBox),
            Field("标签", tagsBox, "用逗号或空格分开都行"),
            Field("人设正文", bodyBox, "越具体越好：Ta 的性格、说话方式、在意什么"),
            Wrap(
                Btn("保存人设", async () =>
                {
                    var nm = nameBox.Text.Trim();
                    if (nm.Length == 0) { Bad("人设得起个名字。"); return; }
                    var payload = new JsonObject
                    {
                        ["name"] = nm,
                        ["tags"] = ToArray(SplitTags(tagsBox.Text)),
                        ["body"] = bodyBox.Text,
                    };
                    if (!isNew) payload["id"] = id;
                    var r = await _api.Post("api/personas/save", payload);
                    if (!Ok(r)) { Bad(Reason(r, "人设没存上")); return; }
                    var newId = S(r["id"], isNew ? S(r["persona"]?["id"]) : id);
                    if (newId.Length == 0) newId = id;
                    if (newId.Length > 0)
                    {
                        var pr = await _api.Patch("api/config", new JsonObject
                        {
                            ["persona"] = new JsonObject { ["current"] = newId },
                        });
                        if (!Ok(pr)) { Bad(Reason(pr, "人设存了，但没切换过去")); }
                    }
                    _personaEdit = null;
                    Say("人设存好了");
                    await BuildIdentityAsync();
                }, "primary"),
                Btn("收起", () =>
                {
                    _personaEdit = null;
                    return BuildIdentityAsync();
                }),
                isNew ? Btn("删掉这个人设", () => Task.CompletedTask) : Btn("删掉这个人设", async () =>
                {
                    if (MessageBox.Show(this, "这个人设会被删掉，确定吗？", "删掉人设",
                            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                    var r = await _api.Post("api/personas/delete", new JsonObject { ["id"] = id });
                    if (!Ok(r)) { Bad(Reason(r, "没删掉")); return; }
                    _personaEdit = null;
                    Say("人设删掉了");
                    await BuildIdentityAsync();
                }, "danger")));
    }

    private async Task BuildAppearanceAsync()
    {
        ResetBody();
        var labels = new List<string> { "跟随系统", "浅色", "深色", "亚克力", "毛玻璃" };
        var values = new List<string> { "follow", "light", "dark", "acrylic", "frosted" };
        var cur = Theme.Mode switch
        {
            AppTheme.Light => "light",
            AppTheme.Dark => "dark",
            AppTheme.Acrylic => "acrylic",
            AppTheme.Frosted => "frosted",
            _ => "follow",
        };
        var themeSel = Sel(labels, values, cur);

        var pick = Btn("选张背景图", async () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
                Title = "选一张背景图",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                await Task.Run(() => File.Copy(dlg.FileName, WallpaperPath, overwrite: true));
            }
            catch (Exception ex)
            {
                Bad("背景图存不下来：" + ex.Message);
                return;
            }
            Say("背景图换好了");
            await LoadWallpaperAsync();
        });
        var clear = Btn("清除背景图", async () =>
        {
            try { if (File.Exists(WallpaperPath)) File.Delete(WallpaperPath); } catch { }
            await LoadWallpaperAsync();
        }, "danger");

        // 网页端也是点一下立刻换，没有「应用」按钮
        themeSel.SelectionChanged += (_, _) =>
        {
            var want = SelValue(themeSel) switch
            {
                "light" => AppTheme.Light,
                "dark" => AppTheme.Dark,
                "acrylic" => AppTheme.Acrylic,
                "frosted" => AppTheme.Frosted,
                _ => AppTheme.Follow,
            };
            if (want == Theme.Mode) return;
            Theme.Set(want);
            Say("主题换好了");
        };

        AddCard("外观",
            Field("主题", themeSel, "主题只存本机，不跟服务端同步，选了就生效"),
            Text("仅 亚克力 / 毛玻璃 支持背景图。", dim: true),
            Wrap(pick, clear),
            Text("背景图尺寸大一点会见效慢一些；亚克力会做模糊，毛玻璃更淡。", dim: true, size: 11));
        await Task.CompletedTask;
    }

    private async Task BuildModelAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;

        var labels = new List<string> { "默认（跟随 DSH）" };
        var values = new List<string> { "" };
        try
        {
            var r = await _api.Get("api/models");
            foreach (var it in Arr(r["models"]) ?? new JsonArray())
            {
                var o = Obj(it);
                if (o is null) continue;
                var prov = S(o["provider"]);
                if (o["models"] is JsonArray ms)
                {
                    foreach (var mm in ms)
                    {
                        var name = S(mm);
                        if (name.Length == 0) continue;
                        labels.Add(prov.Length > 0 ? prov + " / " + name : name);
                        values.Add(prov.Length > 0 ? prov + "::" + name : name);
                    }
                }
                else
                {
                    var name = S(o["model"]);
                    if (name.Length == 0) continue;
                    labels.Add(prov.Length > 0 ? prov + " / " + name : name);
                    values.Add(prov.Length > 0 ? prov + "::" + name : name);
                }
            }
        }
        catch { }

        var m = Sub(cfg, "model");
        var modelSel = Sel(labels, values, S(m["model"]));
        var effortSel = Sel(new[] { "关", "低", "中", "高" },
            new[] { "off", "low", "medium", "high" }, S(m["reasoningEffort"], "medium"));

        AddCard("模型",
            Field("模型", modelSel, "不指定就用 DSH 那边的默认模型"),
            Field("思考强度", effortSel, "越高想得越久，也越费 token"),
            Btn("保存", async () =>
            {
                var sel = SelValue(modelSel);
                var patch = new JsonObject
                {
                    ["model"] = new JsonObject
                    {
                        ["model"] = sel,
                        ["reasoningEffort"] = SelValue(effortSel),
                    },
                };
                if (await PatchConfigAsync(patch)) await BuildModelAsync();
            }, "primary"));
    }

    private async Task BuildReplyAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var m = Sub(cfg, "merge");

        var on = Chk("连发合并", B(m["enabled"], true));
        var wait = Input(D(m["waitSec"], 3).ToString("0.#", CultureInfo.InvariantCulture), 120);
        var max = Input(D(m["maxSec"], 12).ToString("0.#", CultureInfo.InvariantCulture), 120);
        var typing = Input(D(m["waitTypingSec"], 0).ToString("0.#", CultureInfo.InvariantCulture), 120);

        AddCard("回复节奏",
            Switched(on, "你连着发几条，Ta 等你说完再一起回，不然会一句一句地接。"),
            Field("停顿几秒再回", wait, "你停下以后先等几秒，看你还发不发"),
            Field("最多等几秒", max, "等太久了就先生成回复"),
            Field("你在打字时先等几秒", typing, "看到你正在输入就再等等，免得两条回复撞在一起"),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["merge"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["waitSec"] = DblOf(wait.Text, 3),
                        ["maxSec"] = DblOf(max.Text, 12),
                        ["waitTypingSec"] = DblOf(typing.Text, 0),
                    },
                };
                if (await PatchConfigAsync(patch)) await BuildReplyAsync();
            }, "primary"));
    }

    private async Task BuildDiaryAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var d = Sub(cfg, "diary");

        var on = Chk("每天写「Ta 自己按计划写动态」", B(d["enabled"], true));
        var perDay = Input(L(d["timesPerDay"], 2).ToString(), 90);
        var start = Input(S(d["windowStart"], "09:00"), 110);
        var end = Input(S(d["windowEnd"], "23:00"), 110);
        var gap = Input(L(d["minGapMinutes"], 90).ToString(), 110);
        var minChars = Input(L(d["minChars"], 40).ToString(), 110);
        var maxChars = Input(L(d["maxChars"], 800).ToString(), 110);
        var vis = Sel(new[] { "任何人", "仅主人" }, new[] { "all", "self" }, S(d["visibility"], "all"));

        AddCard("空间日记",
            Switched(on, "按时间段自己写动态，不用你催。"),
            Field("每天几篇", perDay, "0 到 6 篇"),
            Row(Cell("时间段开始", start), Cell("时间段结束", end)),
            Field("两篇至少隔（分钟）", gap),
            Row(Cell("最少字数", minChars), Cell("最多字数", maxChars)),
            Text("想写长一点就调大「最多字数」，最多 3000。", dim: true, size: 11),
            Field("谁能看", vis),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["diary"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["timesPerDay"] = Math.Clamp(IntOf(perDay.Text, 2), 0, 6),
                        ["windowStart"] = start.Text.Trim(),
                        ["windowEnd"] = end.Text.Trim(),
                        ["minGapMinutes"] = Math.Max(0, IntOf(gap.Text, 90)),
                        ["minChars"] = Math.Max(0, IntOf(minChars.Text, 40)),
                        ["maxChars"] = Math.Clamp(IntOf(maxChars.Text, 800), 0, 3000),
                        ["visibility"] = SelValue(vis),
                    },
                };
                if (await PatchConfigAsync(patch)) await BuildDiaryAsync();
            }, "primary"));
    }

    private async Task BuildHistoryAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var h = Sub(cfg, "history");
        var keep = Input(L(h["keepDays"], 30).ToString(), 110);

        AddCard("聊天记录",
            Field("保留天数", keep, "聊天记录、图片、语音一起按天清理"),
            Text("聊完（空闲 2 分钟）后按每 100 条自动切成一个文件，一个文件就是界面的一页；翻到顶可以点「查看更早的记录」取上一页。图片和语音单独存放，过期后服务器和浏览器缓存一起清。", dim: true, size: 11),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["history"] = new JsonObject { ["keepDays"] = Math.Max(1, IntOf(keep.Text, 30)) },
                };
                if (await PatchConfigAsync(patch)) await BuildHistoryAsync();
            }, "primary"));
    }

    private async Task BuildLogAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var l = Sub(cfg, "logs");
        var mb = Input(L(l["maxMB"], 4).ToString(), 110);

        AddCard("日志",
            Field("占用空间（MB）", mb, "日志只放在内存里，不落盘；超过这个大小就丢掉最旧的"),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["logs"] = new JsonObject { ["maxMB"] = Math.Clamp(IntOf(mb.Text, 4), 1, 64) },
                };
                if (await PatchConfigAsync(patch)) await BuildLogAsync();
            }, "primary"));
    }

    private async Task BuildWebAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var w = Sub(cfg, "web");
        var host = Input(S(w["host"], "127.0.0.1"), 220);
        var port = Input(L(w["port"], 1708).ToString(), 110);

        AddCard("网页入口",
            Field("监听地址", host, "127.0.0.1 只有本机能打开；0.0.0.0 局域网也能连"),
            Field("监听端口", port),
            Btn("保存", async () =>
            {
                var p = IntOf(port.Text, 1708);
                if (p < 1 || p > 65535) { Bad("端口得在 1 到 65535 之间。"); return; }
                var patch = new JsonObject
                {
                    ["web"] = new JsonObject { ["host"] = host.Text.Trim(), ["port"] = p },
                };
                if (await PatchConfigAsync(patch, "存好了（重启服务端才生效）")) await BuildWebAsync();
            }, "primary"));
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var s in items) arr.Add(s);
        return arr;
    }

    private static IEnumerable<string> SplitTags(string text) =>
        text.Split(new[] { '，', ',', '、', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct();

    private async Task<bool> UploadImage(string path, string api, string? name = null)
    {
        try
        {
            var (b64, mime) = await Task.Run(() => ImageUtil.Prepare(path));
            var body = new JsonObject { ["data"] = b64, ["mime"] = mime };
            if (name is not null) body["name"] = name;
            var r = await _api.Post(api, body);
            if (Ok(r)) return true;
            Bad(Reason(r, "没传上去"));
            return false;
        }
        catch (Exception ex)
        {
            Bad("图片读不出来：" + ex.Message);
            return false;
        }
    }

    // 空 data 就是让服务端清掉头像，回到默认图标
    private async Task<bool> UploadTransparent(string api, string okText)
    {
        try
        {
            var r = await _api.Post(api, new JsonObject { ["data"] = "", ["mime"] = "" });
            if (Ok(r)) { Say(okText); return true; }
            Bad(Reason(r, "没清掉"));
            return false;
        }
        catch (Exception ex)
        {
            Bad("出错了：" + ex.Message);
            return false;
        }
    }

    private async Task LoadPreview(Image img, string? path)
    {
        try
        {
            var bytes = path is null ? null : await Task.Run(() => File.ReadAllBytes(path));
            Dispatcher.Invoke(() => img.Source = bytes is null ? null : ImageUtil.ToImage(bytes));
        }
        catch { }
    }

    private static bool IsFail(JsonObject? r) => r is not null && !Ok(r);
}
