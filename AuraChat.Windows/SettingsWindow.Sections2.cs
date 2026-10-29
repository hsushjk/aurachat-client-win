using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AuraChat.Windows;

public partial class SettingsWindow
{
    private static readonly (string Value, string Label)[] EdgeVoices =
    {
        ("zh-CN-XiaoxiaoNeural", "晓晓 · 温暖女声"),
        ("zh-CN-XiaoyiNeural", "晓伊 · 活泼女声"),
        ("zh-CN-XiaochenNeural", "晓辰 · 清亮女声"),
        ("zh-CN-XiaohanNeural", "晓涵 · 知性女声"),
        ("zh-CN-XiaomengNeural", "晓梦 · 少女"),
        ("zh-CN-XiaomoNeural", "晓墨 · 低柔女声"),
        ("zh-CN-YunxiNeural", "云希 · 少年"),
        ("zh-CN-YunjianNeural", "云健 · 浑厚男声"),
        ("zh-CN-YunxiaNeural", "云夏 · 少年"),
        ("zh-CN-YunyangNeural", "云扬 · 播报男声"),
        ("zh-CN-liaoning-XiaobeiNeural", "晓北 · 东北女声"),
        ("zh-CN-shaanxi-XiaoniNeural", "晓妮 · 陕西女声"),
        ("zh-HK-HiuMaanNeural", "曉曼 · 粤语女声"),
        ("zh-HK-WanLungNeural", "雲龍 · 粤语男声"),
        ("zh-TW-HsiaoChenNeural", "曉臻 · 台湾女声"),
        ("zh-TW-YunJheNeural", "雲哲 · 台湾男声"),
    };

    private async Task BuildVoiceAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var v = Sub(cfg, "voice");

        var on = Chk("发语音（TTS）", B(v["enabled"], false));
        var provLabels = new[] { "自动", "微软 edge-tts", "本地 CosyVoice", "本地 GPT-SoVITS", "OpenAI 兼容", "其他 HTTP 接口", "Windows 自带" };
        var provValues = new[] { "auto", "edge", "cosyvoice", "gptsovits", "openai", "http", "windows" };
        var prov = Sel(provLabels, provValues, S(v["provider"], "auto"));

        var edgeStatus = Text("正在看 edge-tts 装没装…", dim: true, size: 11);
        var edgeInstall = Btn("装好 edge-tts", async () =>
        {
            var r = await _api.PostSlow("api/voice/edge", new JsonObject());
            if (Ok(r)) Say("装好了");
            else Bad(Reason(r, "没装上"));
            await BuildVoiceAsync();
        });
        var edgeVoiceLabels = EdgeVoices.Select(e => e.Label).ToList();
        var edgeVoiceValues = EdgeVoices.Select(e => e.Value).ToList();
        var edgeVoice = Sel(edgeVoiceLabels, edgeVoiceValues, S(v["voice"], "zh-CN-XiaoxiaoNeural"));
        var edgePanel = new StackPanel();
        edgePanel.Children.Add(Field("edge-tts 状态", edgeStatus));
        edgePanel.Children.Add(Field("音色", edgeVoice));
        edgePanel.Children.Add(Wrap(edgeInstall));

        var windowsVoice = Input(S(v["voice"], "Microsoft Huihui Desktop"), 260);
        var windowsPanel = new StackPanel();
        windowsPanel.Children.Add(Field("音色", windowsVoice, "Windows 语音里的音色名，例如 Microsoft Huihui Desktop"));

        var rate = Sel(new[] { "很慢", "慢", "正常", "快", "很快" },
            new[] { "-30%", "-15%", "+0%", "+15%", "+30%" }, S(v["rate"], "+0%"));
        var ratePanel = new StackPanel();
        ratePanel.Children.Add(Field("语速", rate));

        var cv = Obj(v["cosyvoice"]) ?? new JsonObject();
        var cvUrl = Input(S(cv["url"], "http://127.0.0.1:50000"), 320);
        var cvMode = Sel(new[] { "预置音色", "自然语言控制", "零样本克隆", "跨语种" },
            new[] { "sft", "instruct", "zero_shot", "cross_lingual" }, S(cv["mode"], "sft"));
        var cvSpk = Input(S(cv["spkId"], "中文女"), 200);
        var cvRate = Sel(new[] { "22050 Hz", "24000 Hz", "16000 Hz" }, new[] { "22050", "24000", "16000" },
            L(cv["sampleRate"], 22050).ToString());
        var cvInstruct = Input(S(cv["instruct"]), 360);
        var cvPanel = new StackPanel();
        cvPanel.Children.Add(Field("服务地址", cvUrl, "CosyVoice 自带 runtime/python/fastapi/server.py"));
        cvPanel.Children.Add(Field("模式", cvMode));
        cvPanel.Children.Add(Field("音色 ID", cvSpk, "例如 中文女、中文男"));
        cvPanel.Children.Add(Field("采样率", cvRate));
        cvPanel.Children.Add(Field("控制指令", cvInstruct, "instruct 模式下用，例如「用四川话说」"));

        var gs = Obj(v["gptsovits"]) ?? new JsonObject();
        var gsUrl = Input(S(gs["url"], "http://127.0.0.1:9880"), 320);
        var gsRef = Input(S(gs["refAudio"]), 420);
        var gsPrompt = Input(S(gs["promptText"]), 420);
        var gsPanel = new StackPanel();
        gsPanel.Children.Add(Field("服务地址", gsUrl, "GPT-SoVITS 的 api_v2.py"));
        gsPanel.Children.Add(Field("参考音频路径", gsRef, "服务那台电脑上的路径"));
        gsPanel.Children.Add(Field("参考音频的文字", gsPrompt));

        var oaUrl = Input(S(v["ttsBaseUrl"], "http://127.0.0.1:9880/v1"), 320);
        var oaModel = Input(S(v["ttsModel"], "tts-1"), 220);
        var oaVoice = Input(S(v["ttsVoice"], "alloy"), 200);
        var oaKey = Pw("不需要就留空；填过留空表示不改");
        oaKey.Width = 320;
        var oaPanel = new StackPanel();
        oaPanel.Children.Add(Field("服务地址", oaUrl, "例如 http://127.0.0.1:9880/v1"));
        oaPanel.Children.Add(Field("模型", oaModel));
        oaPanel.Children.Add(Field("音色", oaVoice, "看服务支持哪些"));
        oaPanel.Children.Add(Field("密钥", oaKey));

        var httpUrl = Input(S(v["http"] is JsonObject h ? h["urlTemplate"] : null, ""), 460);
        var httpPanel = new StackPanel();
        httpPanel.Children.Add(Field("地址模板", httpUrl, "{text} 会换成要念的话，返回 wav/mp3"));

        var autoChance = SliderRow("短回复变语音的概率", 0, 1, 0.05, D(v["autoChance"], 0.3), "%",
            out var autoChanceSlider, out var autoChanceText, "每次回完，按这个概率把短的转成语音条");
        autoChanceText.Text = Pct(D(v["autoChance"], 0.3)) + "%";
        autoChanceSlider.Value = Math.Round(D(v["autoChance"], 0.3) * 100);
        autoChanceSlider.Minimum = 0;
        autoChanceSlider.Maximum = 100;
        autoChanceSlider.TickFrequency = 5;
        autoChanceSlider.ValueChanged += (_, _) => autoChanceText.Text = (int)autoChanceSlider.Value + "%";
        autoChanceText.Text = (int)autoChanceSlider.Value + "%";

        var autoMax = SliderRow("多短算短回复", 20, 300, 10, L(v["autoMaxChars"], 60), " 字",
            out var autoMaxSlider, out var autoMaxText, "超过这个字数的回复不改语音");
        autoMaxSlider.IsSnapToTickEnabled = true;

        var previewBox = Input("晚安啦，今天也辛苦了，明天见～", 460);
        _ttsText = previewBox;

        var panelHost = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        panelHost.Children.Add(edgePanel);
        panelHost.Children.Add(windowsPanel);
        panelHost.Children.Add(ratePanel);
        panelHost.Children.Add(cvPanel);
        panelHost.Children.Add(gsPanel);
        panelHost.Children.Add(oaPanel);
        panelHost.Children.Add(httpPanel);

        void ApplyProvider()
        {
            var p = SelValue(prov);
            edgePanel.Visibility = p is "auto" or "edge" ? Visibility.Visible : Visibility.Collapsed;
            windowsPanel.Visibility = p == "windows" ? Visibility.Visible : Visibility.Collapsed;
            ratePanel.Visibility = p is "auto" or "edge" or "windows" ? Visibility.Visible : Visibility.Collapsed;
            cvPanel.Visibility = p == "cosyvoice" ? Visibility.Visible : Visibility.Collapsed;
            gsPanel.Visibility = p == "gptsovits" ? Visibility.Visible : Visibility.Collapsed;
            oaPanel.Visibility = p == "openai" ? Visibility.Visible : Visibility.Collapsed;
            httpPanel.Visibility = p == "http" ? Visibility.Visible : Visibility.Collapsed;
        }
        prov.SelectionChanged += (_, _) => ApplyProvider();
        ApplyProvider();

        try
        {
            var er = await _api.Get("api/voice/edge");
            var installed = B(er["installed"]);
            var dir = S(er["dir"]);
            edgeStatus.Text = installed
                ? "已经装好了" + (dir.Length > 0 ? "（" + dir + "）" : "")
                : "还没装；点了下面的按钮会自动下载，装一次就行";
            edgeInstall.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        }
        catch
        {
            edgeStatus.Text = "查不到 edge-tts 的状态（要主人登录）";
        }

        AddCard("语音",
            Switched(on, "Ta 能把话念出来发语音条。"),
            Field("用哪个引擎", prov, "自动：谁在就用谁；本地服务挂掉会退回 edge-tts"),
            panelHost,
            autoChance,
            autoMax,
            Text("先保存设置再试听；试听不影响真正发消息的开关。", dim: true, size: 11),
            Field("试听内容", previewBox),
            Wrap(Btn("生成", async () =>
            {
                await PreviewTtsAsync();
                await Task.CompletedTask;
            }, "primary")),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["voice"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["provider"] = SelValue(prov),
                        ["voice"] = SelValue(prov) == "windows" ? windowsVoice.Text.Trim() : SelValue(edgeVoice),
                        ["rate"] = SelValue(rate),
                        ["autoChance"] = Math.Round(autoChanceSlider.Value / 100.0, 3),
                        ["autoMaxChars"] = (int)autoMaxSlider.Value,
                        ["ttsBaseUrl"] = oaUrl.Text.Trim(),
                        ["ttsModel"] = oaModel.Text.Trim(),
                        ["ttsVoice"] = oaVoice.Text.Trim(),
                        ["cosyvoice"] = new JsonObject
                        {
                            ["url"] = cvUrl.Text.Trim(),
                            ["mode"] = SelValue(cvMode),
                            ["spkId"] = cvSpk.Text.Trim(),
                            ["sampleRate"] = IntOf(SelValue(cvRate), 22050),
                            ["instruct"] = cvInstruct.Text.Trim(),
                        },
                        ["gptsovits"] = new JsonObject
                        {
                            ["url"] = gsUrl.Text.Trim(),
                            ["refAudio"] = gsRef.Text.Trim(),
                            ["promptText"] = gsPrompt.Text.Trim(),
                        },
                        ["http"] = new JsonObject { ["urlTemplate"] = httpUrl.Text.Trim() },
                    },
                };
                if (oaKey.Password.Length > 0)
                    patch["voice"]!["ttsApiKey"] = oaKey.Password;
                if (await PatchConfigAsync(patch)) await BuildVoiceAsync();
            }, "primary"));
    }

    private async Task PreviewTtsAsync()
    {
        var text = _ttsText?.Text.Trim() ?? "";
        if (text.Length == 0) { Bad("先写一句要念的话。"); return; }
        try
        {
            var r = await _api.PostSlow("api/voice/test", new JsonObject { ["text"] = text });
            if (!Ok(r)) { Bad(Reason(r, "没生成出来")); return; }
            var b64 = S(r["data"]);
            if (b64.Length == 0) { Bad("没生成出来"); return; }
            var bytes = Convert.FromBase64String(b64);
            var mime = S(r["mime"], "audio/mpeg");
            _ttsBytes = bytes;
            _ttsMime = mime;
            var path = WriteTts(bytes, mime);
            _ttsPlayer ??= new MediaPlayer();
            _ttsPlayer.Open(new Uri(path));
            _ttsPlayer.Play();
            Say("引擎：" + S(r["engine"], "未知"));
        }
        catch (Exception ex)
        {
            Bad("试听失败了：" + ex.Message);
        }
    }

    // MediaPlayer 只认文件，试听结果先落临时文件。
    private static string WriteTts(byte[] bytes, string mime)
    {
        var ext = mime.Contains("wav") ? ".wav" : mime.Contains("ogg") ? ".ogg" : ".mp3";
        var path = Path.Combine(Path.GetTempPath(), "aurachat-tts" + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task BuildListenAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var v = Sub(cfg, "voice");
        var on = Chk("听语音（STT）", B(v["asr"], false));
        var lang = Input(S(v["asrLanguage"], "zh"), 140);

        var status = Text("正在看识别服务…", dim: true, size: 11);
        try
        {
            var st = await _api.State();
            var asr = Obj(st["asr"]);
            if (asr is null) status.Text = "查不到识别服务的状态。";
            else
            {
                var host = S(asr["host"]);
                var providers = Arr(asr["providers"]);
                if (host.Length == 0)
                    status.Text = "宿主没有提供语音识别（网页版没有；桌面版要启用语音输入）。";
                else if (providers is not null && providers.Count > 0)
                    status.Text = "可用：" + string.Join("、", providers.Select(p => S(p)));
                else
                    status.Text = "宿主有服务，但识别器还没注册（模型可能没下载完）。";
            }
        }
        catch { status.Text = "查不到（要主人登录）。"; }

        AddCard("听语音",
            Switched(on, "把 Ta 收到的语音转成文字再回。"),
            Text("识别走 SenseVoice（dsh 自带），本地推理，不用填地址，也不用联网；只在 DSH 桌面版里可用。", dim: true, size: 11),
            Field("识别服务", status),
            Field("识别语言", lang, "zh / en，留空自动"),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["voice"] = new JsonObject
                    {
                        ["asr"] = on.IsChecked == true,
                        ["asrLanguage"] = lang.Text.Trim(),
                    },
                };
                if (await PatchConfigAsync(patch)) await BuildListenAsync();
            }, "primary"));
    }

    private async Task BuildImageGenAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var g = Sub(cfg, "imageGen");

        var on = Chk("允许画图", B(g["enabled"], false));
        var prov = Sel(new[] { "OpenAI 兼容", "SD WebUI", "ComfyUI", "让 Ta 写代码画" },
            new[] { "openai", "sdwebui", "comfyui", "code" }, S(g["provider"], "openai"));

        var baseUrl = Input(S(g["baseUrl"], "http://127.0.0.1:7860"), 340);
        var apiKey = Pw("本地服务一般不用填");
        apiKey.Width = 300;
        var model = Input(S(g["model"]), 260);
        var size = Input(S(g["size"], "1024x1024"), 160);
        var style = Input(S(g["stylePrompt"]), 460);
        var steps = Input(L(g["steps"], 28).ToString(), 110);
        var cfgScale = Input(D(g["cfgScale"], 7).ToString("0.#", CultureInfo.InvariantCulture), 110);
        var sampler = Input(S(g["sampler"], "DPM++ 2M Karras"), 240);
        var negative = Input(S(g["negativePrompt"]), 460);

        var basePanel = new StackPanel();
        basePanel.Children.Add(Field("地址", baseUrl, "例如 http://127.0.0.1:7860"));
        basePanel.Children.Add(Field("密钥", apiKey));
        var modelField = Field("模型 / 检查点", model, "SD WebUI 可留空，ComfyUI 填 ckpt 名");
        var sizeField = Field("画面尺寸", size, "例如 1024x1024");
        var styleField = Field("风格补充", style, "每次画图都会拼在提示词前面");
        var stepsField = Field("步数", steps);
        var samplerField = Field("采样器", sampler, "例如 DPM++ 2M Karras");
        var cfgField = Field("CFG Scale", cfgScale);
        var negField = Field("负面提示词", negative, "不想让 Ta 画出来的东西");
        var codeHint = Text("代码画：Ta 会直接写一段绘图代码把图画出来，不用连接任何服务。", dim: true, size: 11);

        var host = new StackPanel();
        host.Children.Add(basePanel);
        host.Children.Add(modelField);
        host.Children.Add(sizeField);
        host.Children.Add(styleField);
        host.Children.Add(stepsField);
        host.Children.Add(samplerField);
        host.Children.Add(cfgField);
        host.Children.Add(negField);
        host.Children.Add(codeHint);

        void ApplyProvider()
        {
            var p = SelValue(prov);
            var remote = p != "code";
            basePanel.Visibility = remote && p is "openai" or "sdwebui" or "comfyui" ? Visibility.Visible : Visibility.Collapsed;
            baseUrl.Visibility = p is "openai" or "sdwebui" or "comfyui" ? Visibility.Visible : Visibility.Collapsed;
            apiKey.Visibility = p == "openai" ? Visibility.Visible : Visibility.Collapsed;
            modelField.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            sizeField.Visibility = p is "openai" or "sdwebui" ? Visibility.Visible : Visibility.Collapsed;
            styleField.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            stepsField.Visibility = p is "sdwebui" or "comfyui" ? Visibility.Visible : Visibility.Collapsed;
            samplerField.Visibility = p == "sdwebui" ? Visibility.Visible : Visibility.Collapsed;
            cfgField.Visibility = p == "sdwebui" ? Visibility.Visible : Visibility.Collapsed;
            negField.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            codeHint.Visibility = p == "code" ? Visibility.Visible : Visibility.Collapsed;
        }
        prov.SelectionChanged += (_, _) => ApplyProvider();
        ApplyProvider();

        AddCard("生图",
            Switched(on, "Ta 能在聊天里直接画图发给你。"),
            Field("用哪个服务", prov),
            host,
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["imageGen"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["provider"] = SelValue(prov),
                        ["baseUrl"] = baseUrl.Text.Trim(),
                        ["model"] = model.Text.Trim(),
                        ["size"] = size.Text.Trim(),
                        ["stylePrompt"] = style.Text.Trim(),
                        ["steps"] = IntOf(steps.Text, 28),
                        ["cfgScale"] = DblOf(cfgScale.Text, 7),
                        ["sampler"] = sampler.Text.Trim(),
                        ["negativePrompt"] = negative.Text.Trim(),
                    },
                };
                if (apiKey.Password.Length > 0) patch["imageGen"]!["apiKey"] = apiKey.Password;
                if (await PatchConfigAsync(patch)) await BuildImageGenAsync();
            }, "primary"));
    }

    private async Task BuildStickerAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var s = Sub(cfg, "stickers");

        var on = Chk("自动配表情", B(s["enabled"], true));
        var chance = SliderRow("自动配表情的概率", 10, 100, 5, Pct(D(s["autoChance"], 0.5)), "%",
            out var chanceSlider, out var chanceText, "每次回完，按这个概率补一张表情");
        chanceSlider.Value = Pct(D(s["autoChance"], 0.5));
        chanceSlider.ValueChanged += (_, _) => chanceText.Text = (int)chanceSlider.Value + "%";
        chanceText.Text = (int)chanceSlider.Value + "%";
        var steal = Chk("偷表情", B(s["steal"], true));

        var upload = Btn("上传表情", async () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp",
                Multiselect = true,
                Title = "选几张表情",
            };
            if (dlg.ShowDialog() != true) return;
            var okCount = 0;
            foreach (var f in dlg.FileNames)
            {
                try
                {
                    var (b64, mime) = await Task.Run(() => ImageUtil.Prepare(f, 600));
                    var r = await _api.Post("api/stickers/upload", new JsonObject
                    {
                        ["data"] = b64,
                        ["mime"] = mime,
                        ["from"] = "panel",
                    });
                    if (Ok(r)) okCount++;
                }
                catch { }
            }
            Say(okCount > 0 ? $"传上去 {okCount} 张" : "都没传上去");
            await BuildStickerAsync();
        }, "primary");

        AddCard("表情库",
            Switched(on),
            chance,
            Switched(steal, "Ta 收到你发的图 / 表情，会自己挑着收进库。"),
            Wrap(upload),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["stickers"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["autoChance"] = Math.Round(chanceSlider.Value / 100.0, 3),
                        ["steal"] = steal.IsChecked == true,
                    },
                };
                if (await PatchConfigAsync(patch)) await BuildStickerAsync();
            }, "primary"));

        var stickers = new List<(string Md5, string Name)>();
        try
        {
            var r = await _api.Get("api/stickers");
            foreach (var it in Arr(r["items"]) ?? Arr(r["stickers"]) ?? new JsonArray())
            {
                if (Obj(it) is not { } o) continue;
                var md5 = S(o["md5"], S(o["hash"]));
                if (md5.Length == 0) continue;
                stickers.Add((md5, S(o["name"])));
            }
        }
        catch { }

        var grid = new WrapPanel();
        foreach (var (md5, stickerName) in stickers)
        {
            var img = new Image
            {
                Width = 84,
                Height = 84,
                Stretch = Stretch.UniformToFill,
            };
            try
            {
                var bytes = await _api.FetchBytes(_api.StickerUrl(md5));
                img.Source = bytes is null ? null : ImageUtil.ToImage(bytes);
            }
            catch { }
            var name = Input(stickerName, 96);
            name.Margin = new Thickness(0, 4, 0, 4);
            var saved = new TextBlock { FontSize = 10, Foreground = Theme.Brush("Dim") };
            var tile = new StackPanel
            {
                Width = 128,
                Margin = new Thickness(0, 0, 10, 10),
            };
            tile.Children.Add(new Border
            {
                Child = img,
                CornerRadius = new CornerRadius(8),
                Background = Theme.Brush("Bg"),
                ClipToBounds = true,
                Height = 84,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 84,
            });
            tile.Children.Add(name);
            var renameBtn = IconBtn("\uE70F", "改名", action: async () =>
            {
                var r = await _api.Patch("api/stickers/" + md5, new JsonObject { ["name"] = name.Text.Trim() });
                if (Ok(r)) saved.Text = "存了";
                else saved.Text = Reason(r, "没存上");
            });
            var delBtn = IconBtn("\uE74D", "删除", danger: true, action: async () =>
            {
                var r = await _api.Delete("api/stickers/" + md5);
                if (Ok(r)) { Say("删掉了"); await BuildStickerAsync(); }
                else Bad(Reason(r, "没删掉"));
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(renameBtn);
            row.Children.Add(delBtn);
            tile.Children.Add(row);
            tile.Children.Add(saved);
            grid.Children.Add(tile);
        }

        AddCard("全部表情",
            Text(stickers.Count > 0 ? $"共 {stickers.Count} 张" : "还没有表情。", dim: stickers.Count == 0),
            grid);
    }

    private async Task BuildSpeakUpAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var s = Sub(cfg, "speakUp");

        var on = Chk("开启", B(s["enabled"], true));
        var idle = Input(L(s["idleMinutes"], 90).ToString(), 110);
        var gap = Input(L(s["minGapMinutes"], 60).ToString(), 110);
        var max = Input(L(s["maxPerDay"], 4).ToString(), 110);
        var from = Input(S(s["quietFrom"], "23:00"), 110);
        var to = Input(S(s["quietTo"], "08:00"), 110);

        AddCard("闲置搭话",
            Switched(on, "你一阵子没说话，Ta 会自己开口。"),
            Field("闲置多久开口（分钟）", idle, "从你最后一条消息算起"),
            Field("两次至少隔（分钟）", gap),
            Field("每天最多几次", max),
            Row(Cell("时间段开始", from), Cell("时间段结束", to)),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["speakUp"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["idleMinutes"] = Math.Max(1, IntOf(idle.Text, 90)),
                        ["minGapMinutes"] = Math.Max(0, IntOf(gap.Text, 60)),
                        ["maxPerDay"] = Math.Max(1, IntOf(max.Text, 4)),
                        ["quietFrom"] = from.Text.Trim(),
                        ["quietTo"] = to.Text.Trim(),
                    },
                };
                if (await PatchConfigAsync(patch)) await BuildSpeakUpAsync();
            }, "primary"));
    }

    private async Task BuildSelfDriveAsync()
    {
        ResetBody();
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var s = Sub(cfg, "selfDrive");

        var on = Chk("开启", B(s["enabled"], false));
        var interval = Input(L(s["intervalMinutes"], 60).ToString(), 110);
        var max = Input(L(s["maxPerDay"], 3).ToString(), 110);
        var from = Input(S(s["quietFrom"], "23:00"), 110);
        var to = Input(S(s["quietTo"], "08:00"), 110);

        var note = Input("", 560, multi: true, height: 110);
        try
        {
            var r = await _api.Get("api/selfdrive");
            note.Text = S(r["text"], S(r["note"]));
        }
        catch { }

        AddCard("自驱",
            Switched(on, "Ta 会照着这张便签自己找点事做。"),
            Field("多久看一次（分钟）", interval),
            Field("每天最多几次", max),
            Row(Cell("时间段开始", from), Cell("时间段结束", to)),
            Btn("保存", async () =>
            {
                var patch = new JsonObject
                {
                    ["selfDrive"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["intervalMinutes"] = Math.Max(5, IntOf(interval.Text, 60)),
                        ["maxPerDay"] = Math.Max(1, IntOf(max.Text, 3)),
                        ["quietFrom"] = from.Text.Trim(),
                        ["quietTo"] = to.Text.Trim(),
                    },
                };
                if (await PatchConfigAsync(patch)) await BuildSelfDriveAsync();
            }, "primary"));

        AddCard("共享便签",
            Text("随手写点什么，想到哪写到哪。就是一张便签，不用列待办，你和 Ta 都能改。", dim: true),
            note,
            Btn("保存便签", async () =>
            {
                var r = await _api.Post("api/selfdrive", new JsonObject { ["text"] = note.Text });
                if (Ok(r)) Say("便签存好了");
                else Bad(Reason(r, "便签没存上"));
            }, "primary"));
    }

    private async Task BuildGuestAsync()
    {
        ResetBody();
        var user = Input("", 220);
        var nick = Input("", 220);
        var level = Sel(new[] { "只聊天、发日记（social）", "完全权限（full）" },
            new[] { "social", "full" }, "social");

        AddCard("创建客人",
            Text("给朋友开个号；密码只显示这一次。", dim: true),
            Row(Cell("用户名", user), Cell("昵称", nick)),
            Field("权限", level),
            Btn("创建", async () =>
            {
                var u = user.Text.Trim();
                if (u.Length == 0) { Bad("用户名不能空着。"); return; }
                var r = await _api.Post("api/guests", new JsonObject
                {
                    ["username"] = u,
                    ["displayName"] = nick.Text.Trim().Length > 0 ? nick.Text.Trim() : u,
                    ["level"] = SelValue(level),
                });
                if (!Ok(r)) { Bad(Reason(r, "没建成")); return; }
                var pw = S(r["password"]);
                MessageBox.Show(this,
                    $"账号建好了。\n\n用户名：{u}\n密码：{(pw.Length > 0 ? pw : "（服务端没返回）")}\n\n密码只显示这一次，请复制给朋友。",
                    "创建成功", MessageBoxButton.OK, MessageBoxImage.Information);
                user.Clear();
                nick.Clear();
                await BuildGuestAsync();
            }, "primary"));

        var listCard = new List<(string Subject, string Name, string Level)>();
        try
        {
            var r = await _api.Get("api/guests");
            foreach (var it in Arr(r["guests"]) ?? new JsonArray())
            {
                var o = Obj(it);
                if (o is null) continue;
                listCard.Add((S(o["subject"], S(o["username"])), S(o["name"], S(o["displayName"])), S(o["level"], "social")));
            }
        }
        catch { }

        var kids = new List<UIElement>();
        kids.Add(Text(listCard.Count > 0 ? $"共 {listCard.Count} 个客人" : "还没有客人。", dim: listCard.Count == 0));
        foreach (var g in listCard)
        {
            var nameText = Text(g.Name.Length > 0 ? g.Name : g.Subject, size: 13);
            var subText = Text("@" + g.Subject + " · " + g.Level, dim: true, size: 11);
            var lvl = Sel(new[] { "social", "full" }, new[] { "social", "full" }, g.Level);
            var change = Btn("改权限", async () =>
            {
                var r = await _api.Patch("api/guests/" + Uri.EscapeDataString(g.Subject),
                    new JsonObject { ["level"] = SelValue(lvl) });
                if (Ok(r)) Say("改好了");
                else Bad(Reason(r, "没改成"));
            });
            var reset = Btn("重置密码", async () =>
            {
                _guestPwSubject = _guestPwSubject == g.Subject ? "" : g.Subject;
                await BuildGuestAsync();
            });
            var del = IconBtn("\uE74D", "删除", danger: true, action: async () =>
            {
                if (MessageBox.Show(this, $"删掉客人「{g.Subject}」？", "删除客人",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                var r = await _api.Delete("api/guests/" + Uri.EscapeDataString(g.Subject));
                if (Ok(r)) { Say("删掉了"); await BuildGuestAsync(); }
                else Bad(Reason(r, "没删掉"));
            });

            var rowKids = new List<UIElement>
            {
                new StackPanel { Children = { nameText, subText }, MinWidth = 150 },
                lvl, change, reset, del,
            };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4),
            };
            foreach (var k in rowKids) row.Children.Add(k);
            kids.Add(row);

            if (_guestPwSubject == g.Subject)
            {
                var pw = Pw("新密码（至少 6 位）");
                pw.Width = 220;
                kids.Add(Row(pw, Btn("确定改", async () =>
                {
                    if (pw.Password.Length < 6) { Bad("新密码至少 6 位。"); return; }
                    var r = await _api.Patch("api/guests/" + Uri.EscapeDataString(g.Subject),
                        new JsonObject { ["password"] = pw.Password });
                    if (Ok(r))
                    {
                        Say("密码改好了");
                        _guestPwSubject = "";
                        await BuildGuestAsync();
                    }
                    else Bad(Reason(r, "密码没改成"));
                }, "primary"), IconBtn("\uE711", "取消", action: () =>
                {
                    _guestPwSubject = "";
                    return BuildGuestAsync();
                })));
            }
        }
        AddCard("客人名单", kids.ToArray());
    }
}
