using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AuraChat.Windows;

public partial class SettingsWindow
{
    // 编辑目标：p:id / i:key:idx / s:key:idx / m:id；新建用 __new__
    private string _memTab = "档案";
    private string _memEdit = "";

    private const double BarWidth = 420;

    private async Task BuildOverviewAsync()
    {
        ResetBody();
        var st = await _api.Get("api/status");
        if (!Ok(st))
        {
            Bad(Reason(st, "读不到概览"));
            return;
        }
        var cfg = await LoadConfigAsync() ?? new JsonObject();

        var stats = Obj(st["stats"]) ?? new JsonObject();
        var mood = Obj(st["mood"]) ?? new JsonObject();
        var diary = Obj(st["diaryStats"]) ?? new JsonObject();
        var speak = Obj(st["speakUp"]) ?? new JsonObject();
        var drive = Obj(st["selfDrive"]) ?? new JsonObject();
        var memory = Obj(st["memory"]) ?? new JsonObject();
        var sessions = Obj(st["sessions"]);

        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 12) };
        var moodText = $"{L(mood["value"])} {S(mood["label"], "")}".Trim();
        grid.Children.Add(StatCell("心情", moodText));
        grid.Children.Add(StatCell("动态", $"{L(diary["total"])} 篇"));
        grid.Children.Add(StatCell("今天写了", $"{L(diary["today"])} 篇"));
        grid.Children.Add(StatCell("今天主动搭话", $"{L(speak["count"])} 次"));
        grid.Children.Add(StatCell("自驱",
            (B(drive["enabled"]) ? "开启" : "关闭") + $" · 今天 {L(drive["count"])} 次"));
        grid.Children.Add(StatCell("记忆", $"{L(memory["count"])} 条"));
        Body.Children.Add(grid);

        var su = Sub(cfg, "speakUp");
        var masterOn = B(cfg["enabled"], true);
        var suOn = B(su["enabled"], true);
        var quietFrom = S(su["quietFrom"], "23:00");
        var quietTo = S(su["quietTo"], "08:00");

        AddCard("状态",
            KV("总开关", masterOn ? "开" : "关"),
            KV("闲置搭话", suOn ? $"{L(su["idleMinutes"], 90)} 分钟" : "关"),
            KV("时间段", $"{quietFrom} – {quietTo}"),
            KV("聊天轮数", L(stats["turns"]).ToString()),
            KV("消息条数", L(stats["messages"]).ToString()),
            KV("语音 / 表情 / 图",
                $"{L(stats["voice"])} 条 · 表情 {L(stats["stickers"])} · 图 {L(stats["images"])}"),
            KV("会话", $"{sessions?.Count ?? 0} 个"));

        if (!_owner) return;

        AddCard("调试 / 试一下",
            Wrap(
                Btn("让Ta现在来找你", () => RunTestAsync("speakup", "让Ta现在来找你")),
                Btn("让Ta写一篇日记", () => RunTestAsync("diary", "让Ta写一篇日记")),
                Btn("让Ta自驱一次", () => RunTestAsync("selfdrive", "让Ta自驱一次")),
                Btn("让Ta回访评论", () => RunTestAsync("social", "让Ta回访评论"))));
    }

    private Border StatCell(string label, string value)
    {
        var sp = new StackPanel { Margin = new Thickness(2) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Theme.Brush("Dim"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush("Fg"),
            TextWrapping = TextWrapping.Wrap,
        });
        return new Border
        {
            Background = Theme.Brush("Panel"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 8, 8),
            Child = sp,
        };
    }

    private async Task RunTestAsync(string what, string okText)
    {
        var r = await _api.PostSlow("api/test", new JsonObject { ["what"] = what });
        if (Ok(r)) Say(S(r["message"]) is { Length: > 0 } m ? m : okText);
        else Bad(Reason(r, "没做成"));
    }

    private async Task BuildSpaceAsync()
    {
        ResetBody();
        HeadRight.Children.Clear();
        var refresh = IconBtn("\uE72C", "刷新", action: () => BuildSpaceAsync());
        HeadRight.Children.Add(refresh);

        var meName = "";
        var meRole = "";
        try
        {
            var st = await _api.State();
            meName = S(Obj(st["me"])?["name"]);
            meRole = S(Obj(st["me"])?["role"]);
        }
        catch { }

        var r = await _api.Get("api/diary");
        if (!Ok(r))
        {
            Bad(Reason(r, "读不到动态"));
            return;
        }
        var entries = Arr(r["entries"]) ?? new JsonArray();
        var stats = Obj(r["stats"]) ?? new JsonObject();
        var head = $"共 {L(stats["total"], entries.Count)} 篇 · 今天 {L(stats["today"])} 篇";
        var planned = L(stats["planned"]);
        if (planned > 0) head += $" · 计划 {planned} 篇";
        HeadStat(head);

        if (entries.Count == 0)
        {
            AddCard("空间", Text("还没有动态。", dim: true));
            return;
        }

        foreach (var it in entries)
        {
            if (Obj(it) is not { } entry) continue;
            var id = S(entry["id"]);
            var card = new StackPanel();

            var headRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            if (_owner)
            {
                var del = IconBtn("\uE74D", "删除", danger: true, action: async () =>
                {
                    if (MessageBox.Show(this, "这篇动态会被删掉，确定吗？", "删动态",
                            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                    var dr = await _api.Post("api/diary/delete", new JsonObject { ["entryId"] = id });
                    if (Ok(dr)) { Say("删掉了"); await BuildSpaceAsync(); }
                    else Bad(Reason(dr, "没删掉"));
                });
                DockPanel.SetDock(del, Dock.Right);
                headRow.Children.Add(del);
            }

            var hsp = new StackPanel { Orientation = Orientation.Horizontal };
            hsp.Children.Add(new TextBlock
            {
                Text = DiaryTime(L(entry["at"])),
                FontSize = 11.5,
                Foreground = Theme.Brush("Dim"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (S(entry["visibility"], "all") == "self")
            {
                hsp.Children.Add(new TextBlock
                {
                    Text = "仅主人可见",
                    FontSize = 11,
                    Foreground = Theme.Brush("Dim"),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            headRow.Children.Add(hsp);
            card.Children.Add(headRow);
            card.Children.Add(Text(S(entry["text"]), size: 13.5));

            var comments = Arr(entry["comments"]) ?? new JsonArray();
            if (comments.Count == 0) card.Children.Add(Text("还没有评论", dim: true, size: 11));
            foreach (var c in comments)
            {
                if (Obj(c) is not { } cm) continue;
                card.Children.Add(CommentBlock(cm, id, null, meName, meRole));
                foreach (var rp in Arr(cm["replies"]) ?? new JsonArray())
                {
                    if (Obj(rp) is not { } rv) continue;
                    card.Children.Add(CommentBlock(rv, id, S(cm["id"]), meName, meRole));
                }
            }

            var input = new TextBox
            {
                Margin = new Thickness(0, 6, 8, 0),
                MinWidth = 200,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var send = new Button
            {
                Content = "评论",
                MinWidth = 64,
                Height = 30,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var sendRow = new DockPanel();
            DockPanel.SetDock(send, Dock.Right);
            sendRow.Children.Add(send);
            sendRow.Children.Add(input);
            card.Children.Add(sendRow);

            async Task PostComment()
            {
                var text = input.Text.Trim();
                if (text.Length == 0) return;
                var pr = await _api.Post("api/diary/comment", new JsonObject
                {
                    ["entryId"] = id,
                    ["text"] = text,
                });
                if (Ok(pr))
                {
                    Say("");
                    await BuildSpaceAsync();
                }
                else Bad(Reason(pr, "没发出去"));
            }

            send.Click += async (_, _) => await PostComment();
            input.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    await PostComment();
                }
            };

            AddCard(null, card);
        }
    }

    private FrameworkElement CommentBlock(JsonObject cm, string entryId, string? replyTo, string meName, string meRole)
    {
        var wrap = new StackPanel { Margin = new Thickness(replyTo is null ? 0 : 22, 4, 0, 4) };
        var line = new DockPanel();
        var from = S(cm["from"]);
        var commentId = S(cm["id"]);
        var mine = meRole.Length > 0 && from == meRole;
        var canDelete = _owner || mine || (meName.Length > 0 && S(cm["name"]) == meName) || B(cm["mine"]);

        // 点「回复」才展开
        var replyBox = new DockPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var replyInput = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        var replySend = new Button { Content = "回复", MinWidth = 60, Height = 28, Margin = new Thickness(0, 0, 0, 0) };
        DockPanel.SetDock(replySend, Dock.Right);
        replyBox.Children.Add(replySend);
        replyBox.Children.Add(replyInput);

        async Task PostReply()
        {
            var text = replyInput.Text.Trim();
            if (text.Length == 0) return;
            var pr = await _api.Post("api/diary/reply", new JsonObject
            {
                ["entryId"] = entryId,
                ["commentId"] = commentId,
                ["text"] = text,
            });
            if (Ok(pr)) { Say(""); await BuildSpaceAsync(); }
            else Bad(Reason(pr, "没发出去"));
        }
        replySend.Click += async (_, _) => await PostReply();
        replyInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                await PostReply();
            }
        };

        if (canDelete)
        {
            var del = IconBtn("\uE74D", "删除", danger: true, action: async () =>
            {
                var body = new JsonObject { ["entryId"] = entryId, ["commentId"] = commentId };
                if (replyTo is not null) body["replyId"] = commentId;
                var r = await _api.Post("api/diary/comment/delete", body);
                if (Ok(r)) { Say("删掉了"); await BuildSpaceAsync(); }
                else Bad(Reason(r, "没删掉"));
            });
            DockPanel.SetDock(del, Dock.Right);
            line.Children.Add(del);
        }

        var reply = IconBtn("\uE8F1", "回复");
        reply.Click += (_, _) =>
        {
            replyBox.Visibility = replyBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (replyBox.Visibility == Visibility.Visible) replyInput.Focus();
        };
        DockPanel.SetDock(reply, Dock.Right);
        line.Children.Add(reply);

        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new System.Windows.Documents.Run(S(cm["name"], from))
        {
            FontSize = 12,
            Foreground = Theme.Brush("Fg"),
            FontWeight = FontWeights.SemiBold,
        });
        text.Inlines.Add(new System.Windows.Documents.Run("  " + S(cm["text"]))
        {
            FontSize = 12.5,
            Foreground = Theme.Brush("Fg"),
        });
        text.Inlines.Add(new System.Windows.Documents.Run("  " + Fmt(L(cm["at"])))
        {
            FontSize = 10.5,
            Foreground = Theme.Brush("Dim"),
        });
        line.Children.Add(text);
        wrap.Children.Add(line);
        wrap.Children.Add(replyBox);
        return wrap;
    }

    private static string DiaryTime(long ms)
    {
        if (ms <= 0) return "—";
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var today = DateTimeOffset.Now.ToLocalTime().Date;
        var d = t.Date;
        var head = d == today ? "今天" : d == today.AddDays(-1) ? "昨天" : $"{t.Month}月{t.Day}日";
        return $"{head} {t:HH:mm}";
    }

    private async Task BuildMoodAsync()
    {
        ResetBody();
        HeadRight.Children.Clear();
        var refresh = IconBtn("\uE72C", "刷新", action: () => BuildMoodAsync());
        HeadRight.Children.Add(refresh);

        var r = await _api.Get("api/mood");
        var state = Obj(r["state"]);
        if (state is null)
        {
            AddCard("心情", Text(Reason(r, "读不到心情"), dim: true));
            return;
        }

        var value = Math.Clamp(D(state["value"]), 0, 100);
        var label = S(state["label"]);
        var band = Obj(state["band"]) ?? new JsonObject();
        var lo = Math.Clamp(D(band["low"], 40), 0, 100);
        var hi = Math.Clamp(D(band["high"], 60), 0, 100);
        var reason = S(state["reason"]);

        var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        big.Children.Add(new TextBlock
        {
            Text = value.ToString("0.#", CultureInfo.InvariantCulture),
            FontSize = 44,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush("Fg"),
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        big.Children.Add(new TextBlock
        {
            Text = $"  / 100 · {label}",
            FontSize = 13,
            Foreground = Theme.Brush("Dim"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var canvas = new Canvas { Width = BarWidth, Height = 12, Margin = new Thickness(0, 0, 0, 8) };
        canvas.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = BarWidth,
            Height = 6,
            RadiusX = 3,
            RadiusY = 3,
            Fill = Theme.Brush("Line"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        canvas.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(2, BarWidth * value / 100.0),
            Height = 6,
            RadiusX = 3,
            RadiusY = 3,
            Fill = Theme.Brush("Accent"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        foreach (var mark in new[] { lo, hi })
        {
            var line = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 12,
                Fill = Theme.Brush("Fg"),
                Opacity = 0.5,
            };
            Canvas.SetLeft(line, Math.Clamp(BarWidth * mark / 100.0, 0, BarWidth - 2));
            canvas.Children.Add(line);
        }

        var cfg = await LoadConfigAsync() ?? new JsonObject();
        var m = Sub(cfg, "mood");
        var half = D(m["halfLifeHours"], 6);
        var hint = $"平常区间 {lo:0.#}–{hi:0.#}：高于区间慢慢往回落、低于区间慢慢往回升。半衰期 {half:0.#} 小时——离平常差多少，过这么久就减半。";

        var kids = new List<UIElement> { big, canvas, Text(hint, dim: true) };
        if (reason.Length > 0) kids.Add(Text("最近一次变化：" + reason, dim: true));
        AddCard("心情", kids.ToArray());

        if (_owner)
        {
            var halfBox = Input(half.ToString("0.#", CultureInfo.InvariantCulture), 110);
            var loBox = Input(lo.ToString("0.#", CultureInfo.InvariantCulture), 90);
            var hiBox = Input(hi.ToString("0.#", CultureInfo.InvariantCulture), 90);
            var judge = Chk("判断情绪", B(m["judge"], true));
            var dash = new TextBlock
            {
                Text = "到",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 8),
                Foreground = Theme.Brush("Dim"),
            };

            AddCard("心情怎么算",
                Field("半衰期（小时）", halfBox, "心情偏离平常后，几小时落回去一半"),
                Cell("平常区间", Row(loBox, dash, hiBox)),
                Text("低了往上升回下限，高了往下降回上限。", dim: true, size: 11),
                Switched(judge, "让模型判断情绪"),
                Wrap(
                    Btn("保存", async () =>
                    {
                        var l = Math.Clamp(DblOf(loBox.Text, 40), 0, 100);
                        var h = Math.Clamp(DblOf(hiBox.Text, 60), 0, 100);
                        if (h < l) (l, h) = (h, l);
                        var patch = new JsonObject
                        {
                            ["mood"] = new JsonObject
                            {
                                ["halfLifeHours"] = Math.Clamp(DblOf(halfBox.Text, 6), 0.5, 72),
                                ["neutralLow"] = l,
                                ["neutralHigh"] = h,
                                ["judge"] = judge.IsChecked == true,
                            },
                        };
                        if (await PatchConfigAsync(patch)) await BuildMoodAsync();
                    }, "primary"),
                    Btn("重置到平常", async () =>
                    {
                        var rr = await _api.Post("api/mood", new JsonObject { ["reset"] = true });
                        if (Ok(rr)) { Say("重置好了"); await BuildMoodAsync(); }
                        else Bad(Reason(rr, "没重置成"));
                    }, "danger")));
        }

        var history = Arr(r["history"]) ?? new JsonArray();
        var list = new StackPanel();
        if (history.Count == 0) list.Children.Add(Text("还没有记录。", dim: true));
        foreach (var it in history)
        {
            if (Obj(it) is not { } h) continue;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(Text(S(h["reason"]) is { Length: > 0 } t ? t : "（没写原因）", size: 12.5));
            sp.Children.Add(Text($"{Fmt(L(h["at"]))} · {D(h["from"]):0.#} → {D(h["to"]):0.#}", dim: true, size: 11));
            list.Children.Add(sp);
        }
        AddCard("心情变化记录", list);
    }

    private async Task BuildMemoryAsync()
    {
        ResetBody();
        HeadRight.Children.Clear();
        var refresh = IconBtn("\uE72C", "刷新", action: () => BuildMemoryAsync());
        HeadRight.Children.Add(refresh);

        var r = await _api.Get("api/memory");
        if (!Ok(r))
        {
            Bad(Reason(r, "读不到记忆"));
            return;
        }

        var tabs = new[] { "档案", "相处经验", "说话方式", "全部记忆", "设置" };
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var t in tabs)
        {
            var on = _memTab == t;
            var b = new Button
            {
                Content = t,
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 76,
                Height = 32,
            };
            if (on) b.Background = Theme.Brush("Accent");
            else b.Style = (Style)Application.Current.FindResource("GhostButton");
            var name = t;
            b.Click += (_, _) =>
            {
                _memTab = name;
                _memEdit = "";
                _ = BuildMemoryAsync();
            };
            bar.Children.Add(b);
        }
        Body.Children.Add(bar);

        switch (_memTab)
        {
            case "相处经验":
                RenderInsights(r);
                break;
            case "说话方式":
                RenderStyles(r);
                break;
            case "全部记忆":
                RenderItems(r);
                break;
            case "设置":
                await RenderMemSettingsAsync();
                break;
            default:
                RenderProfiles(r);
                break;
        }
    }

    private void RenderProfiles(JsonObject r)
    {
        var profiles = Arr(r["profiles"]) ?? new JsonArray();
        var add = new Button
        {
            Content = "＋ 记一个人",
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Height = 28,
        };
        add.Click += async (_, _) =>
        {
            _memEdit = "p:__new__";
            await BuildMemoryAsync();
        };
        var holder = CardWithHead($"档案 · 共 {profiles.Count} 个", add);

        foreach (var it in profiles)
        {
            if (Obj(it) is not { } p) continue;
            var id = S(p["id"]);
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = S(p["name"], id),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = Theme.Brush("Fg"),
            });
            head.Children.Add(new TextBlock
            {
                Text = "  " + id,
                FontSize = 10.5,
                Foreground = Theme.Brush("Dim"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(head);

            if (_memEdit == "p:" + id)
            {
                sp.Children.Add(BuildProfileEditor(id, p));
            }
            else
            {
                var text = S(p["text"]);
                sp.Children.Add(Text(text.Length > 0 ? text : "（还没有印象）", dim: text.Length == 0));
                sp.Children.Add(Wrap(IconBtn("\uE70F", "修改", action: async () =>
                {
                    _memEdit = "p:" + id;
                    await BuildMemoryAsync();
                })));
            }
            holder.Children.Add(sp);
        }

        if (_memEdit == "p:__new__")
        {
            var box = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            box.Children.Add(Text("给谁记：主人写 owner，客人写用户名。", dim: true, size: 11));
            box.Children.Add(BuildProfileEditor("", null));
            holder.Children.Add(box);
        }
        if (profiles.Count == 0 && _memEdit != "p:__new__")
            holder.Children.Add(Text("还没有档案。", dim: true));
    }

    private UIElement BuildProfileEditor(string id, JsonObject? current)
    {
        var isNew = id.Length == 0;
        var about = Input(isNew ? "" : id, 160);
        var name = Input(S(current?["name"]), 200);
        var text = Input(S(current?["text"]), 520, multi: true, height: 90);
        var sp = new StackPanel();
        if (isNew) sp.Children.Add(Field("给谁（owner / 用户名）", about));
        sp.Children.Add(Field("称呼", name));
        sp.Children.Add(Field("印象", text));
        sp.Children.Add(Wrap(
            Btn("保存", async () =>
            {
                var aboutId = isNew ? about.Text.Trim() : id;
                if (aboutId.Length == 0) { Bad("得填「给谁」。"); return; }
                var rr = await _api.Post("api/memory/profile", new JsonObject
                {
                    ["about"] = aboutId,
                    ["name"] = name.Text.Trim(),
                    ["text"] = text.Text.Trim(),
                });
                if (!Ok(rr)) { Bad(Reason(rr, "没存上")); return; }
                _memEdit = "";
                Say("存好了");
                await BuildMemoryAsync();
            }, "primary"),
            Btn("取消", async () =>
            {
                _memEdit = "";
                await BuildMemoryAsync();
            })));
        return sp;
    }

    private void RenderInsights(JsonObject r)
    {
        var groups = Arr(r["insights"]) ?? new JsonArray();
        foreach (var it in groups)
        {
            if (Obj(it) is not { } g) continue;
            var key = S(g["key"]);
            var title = S(g["name"], key);
            var add = new Button
            {
                Content = "＋ 加一条",
                Style = (Style)Application.Current.FindResource("GhostButton"),
                Height = 26,
                FontSize = 11,
            };
            add.Click += async (_, _) =>
            {
                _memEdit = "i-add:" + key;
                await BuildMemoryAsync();
            };
            var holder = CardWithHead(title, add);

            var list = Arr(g["list"]) ?? new JsonArray();
            for (var i = 0; i < list.Count; i++)
            {
                if (Obj(list[i]) is not { } line) continue;
                var idx = i;
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                if (_memEdit == $"i:{key}:{idx}")
                {
                    var box = Input(S(line["text"]), 480, multi: true, height: 60);
                    sp.Children.Add(box);
                    sp.Children.Add(Wrap(
                        Btn("保存", async () =>
                        {
                            if (await SaveInsightLinesAsync(key, title, list, idx, box.Text.Trim()))
                            {
                                _memEdit = "";
                                Say("存好了");
                            }
                            await BuildMemoryAsync();
                        }, "primary"),
                        Btn("取消", async () =>
                        {
                            _memEdit = "";
                            await BuildMemoryAsync();
                        })));
                }
                else
                {
                    sp.Children.Add(Text(S(line["text"])));
                    sp.Children.Add(Text(Fmt(L(line["at"])), dim: true, size: 10.5));
                    sp.Children.Add(Wrap(
                        IconBtn("\uE70F", "修改", action: async () =>
                        {
                            _memEdit = $"i:{key}:{idx}";
                            await BuildMemoryAsync();
                        }),
                        IconBtn("\uE74D", "删除", danger: true, action: async () =>
                        {
                            if (await SaveInsightLinesAsync(key, title, list, idx, null)) Say("删掉了");
                            await BuildMemoryAsync();
                        })));
                }
                holder.Children.Add(sp);
            }
            if (list.Count == 0) holder.Children.Add(Text("还没有。", dim: true));

            if (_memEdit == "i-add:" + key)
            {
                var box = Input("", 480, multi: true, height: 60);
                holder.Children.Add(box);
                holder.Children.Add(Wrap(
                    Btn("保存", async () =>
                    {
                        if (box.Text.Trim().Length == 0) return;
                        if (await SaveInsightLinesAsync(key, title, list, -1, box.Text.Trim()))
                        {
                            _memEdit = "";
                            Say("存好了");
                        }
                        await BuildMemoryAsync();
                    }, "primary"),
                    Btn("取消", async () =>
                    {
                        _memEdit = "";
                        await BuildMemoryAsync();
                    })));
            }
        }
        if (groups.Count == 0) AddCard("相处经验", Text("还没有相处经验。", dim: true));
    }

    private async Task<bool> SaveInsightLinesAsync(string key, string name, JsonArray list, int index, string? text)
    {
        var lines = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            if (i == index)
            {
                if (!string.IsNullOrEmpty(text)) lines.Add(text);
                continue;
            }
            if (Obj(list[i]) is { } o)
            {
                var s = S(o["text"]);
                if (s.Length > 0) lines.Add(s);
            }
        }
        if (index < 0 && text is not null) lines.Add(text);

        var arr = new JsonArray();
        foreach (var l in lines) arr.Add(l);
        var r = await _api.Post("api/memory/insight", new JsonObject
        {
            ["key"] = key,
            ["name"] = name,
            ["lines"] = arr,
        });
        if (!Ok(r)) { Bad(Reason(r, "没存上")); return false; }
        return true;
    }

    private void RenderStyles(JsonObject r)
    {
        var styles = Obj(r["styles"]) ?? new JsonObject();
        foreach (var kv in styles)
        {
            var key = kv.Key;
            var title = key == "self" ? "Ta 自己养成的说法" : "和 " + key + " 说话的方式";
            var lines = Arr(kv.Value) ?? new JsonArray();
            var add = new Button
            {
                Content = "＋ 加一条",
                Style = (Style)Application.Current.FindResource("GhostButton"),
                Height = 26,
                FontSize = 11,
            };
            add.Click += async (_, _) =>
            {
                _memEdit = "s-add:" + key;
                await BuildMemoryAsync();
            };
            var holder = CardWithHead(title, add);

            for (var i = 0; i < lines.Count; i++)
            {
                var idx = i;
                var text = S(lines[i]);
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                if (_memEdit == $"s:{key}:{idx}")
                {
                    var box = Input(text, 480, multi: true, height: 54);
                    sp.Children.Add(box);
                    sp.Children.Add(Wrap(
                        Btn("保存", async () =>
                        {
                            if (await SaveStyleLinesAsync(key, lines, idx, box.Text.Trim()))
                            {
                                _memEdit = "";
                                Say("存好了");
                            }
                            await BuildMemoryAsync();
                        }, "primary"),
                        Btn("取消", async () =>
                        {
                            _memEdit = "";
                            await BuildMemoryAsync();
                        })));
                }
                else
                {
                    sp.Children.Add(Text(text));
                    sp.Children.Add(Wrap(
                        IconBtn("\uE70F", "修改", action: async () =>
                        {
                            _memEdit = $"s:{key}:{idx}";
                            await BuildMemoryAsync();
                        }),
                        IconBtn("\uE74D", "删除", danger: true, action: async () =>
                        {
                            if (await SaveStyleLinesAsync(key, lines, idx, null)) Say("删掉了");
                            await BuildMemoryAsync();
                        })));
                }
                holder.Children.Add(sp);
            }
            if (lines.Count == 0) holder.Children.Add(Text("还没有。", dim: true));

            if (_memEdit == "s-add:" + key)
            {
                var box = Input("", 480, multi: true, height: 54);
                holder.Children.Add(box);
                holder.Children.Add(Wrap(
                    Btn("保存", async () =>
                    {
                        if (box.Text.Trim().Length == 0) return;
                        if (await SaveStyleLinesAsync(key, lines, -1, box.Text.Trim()))
                        {
                            _memEdit = "";
                            Say("存好了");
                        }
                        await BuildMemoryAsync();
                    }, "primary"),
                    Btn("取消", async () =>
                    {
                        _memEdit = "";
                        await BuildMemoryAsync();
                    })));
            }
        }
        if (styles.Count == 0) AddCard("说话方式", Text("还没有。", dim: true));
    }

    private async Task<bool> SaveStyleLinesAsync(string key, JsonArray lines, int index, string? text)
    {
        var list = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == index)
            {
                if (!string.IsNullOrEmpty(text)) list.Add(text);
                continue;
            }
            var s = S(lines[i]);
            if (s.Length > 0) list.Add(s);
        }
        if (index < 0 && text is not null) list.Add(text);

        var arr = new JsonArray();
        foreach (var l in list) arr.Add(l);
        var r = await _api.Post("api/memory/style", new JsonObject { ["who"] = key, ["lines"] = arr });
        if (!Ok(r)) { Bad(Reason(r, "没存上")); return false; }
        return true;
    }

    private void RenderItems(JsonObject r)
    {
        var items = Arr(r["items"]) ?? new JsonArray();
        var add = new Button
        {
            Content = "＋ 加一条",
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Height = 28,
        };
        add.Click += async (_, _) =>
        {
            _memEdit = "m:__new__";
            await BuildMemoryAsync();
        };
        var count = L(Sub(r, "stats")["count"], items.Count);
        var holder = CardWithHead($"全部记忆 · 共 {count} 条", add);

        foreach (var it in items)
        {
            if (Obj(it) is not { } m) continue;
            var id = S(m["id"]);
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            if (_memEdit == "m:" + id)
            {
                var box = Input(S(m["text"]), 500, multi: true, height: 70);
                sp.Children.Add(box);
                sp.Children.Add(Wrap(
                    Btn("保存", async () =>
                    {
                        var rr = await _api.Post("api/memory/update", new JsonObject
                        {
                            ["id"] = id,
                            ["text"] = box.Text.Trim(),
                        });
                        if (!Ok(rr)) { Bad(Reason(rr, "没存上")); return; }
                        _memEdit = "";
                        Say("存好了");
                        await BuildMemoryAsync();
                    }, "primary"),
                    Btn("取消", async () =>
                    {
                        _memEdit = "";
                        await BuildMemoryAsync();
                    })));
            }
            else
            {
                sp.Children.Add(Text(S(m["text"])));
                var meta = $"{Fmt(L(m["at"]))} · 重要度 {Math.Round(D(m["importance"]) * 100)}%";
                var src = S(m["source"]);
                if (src.Length > 0) meta += " · " + src;
                sp.Children.Add(Text(meta, dim: true, size: 10.5));

                var tags = Arr(m["tags"]);
                if (tags is not null && tags.Count > 0)
                {
                    var tagRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                    foreach (var t in tags)
                    {
                        tagRow.Children.Add(new Border
                        {
                            Background = Theme.Brush("Bg"),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(6, 2, 6, 2),
                            Margin = new Thickness(0, 0, 6, 4),
                            Child = new TextBlock
                            {
                                Text = "#" + S(t),
                                FontSize = 10.5,
                                Foreground = Theme.Brush("Dim"),
                            },
                        });
                    }
                    sp.Children.Add(tagRow);
                }

                sp.Children.Add(Wrap(
                    IconBtn("\uE70F", "修改", action: async () =>
                    {
                        _memEdit = "m:" + id;
                        await BuildMemoryAsync();
                    }),
                    IconBtn("\uE74D", "删除", danger: true, action: async () =>
                    {
                        var rr = await _api.Post("api/memory/delete", new JsonObject { ["id"] = id });
                        if (!Ok(rr)) { Bad(Reason(rr, "没删掉")); return; }
                        Say("删掉了");
                        await BuildMemoryAsync();
                    })));
            }
            holder.Children.Add(sp);
        }
        if (items.Count == 0) holder.Children.Add(Text("还没有记忆。", dim: true));

        if (_memEdit == "m:__new__")
        {
            var box = Input("", 500, multi: true, height: 70);
            holder.Children.Add(box);
            holder.Children.Add(Wrap(
                Btn("保存", async () =>
                {
                    if (box.Text.Trim().Length == 0) return;
                    var rr = await _api.Post("api/memory", new JsonObject
                    {
                        ["text"] = box.Text.Trim(),
                        ["source"] = "manual",
                    });
                    if (!Ok(rr)) { Bad(Reason(rr, "没存上")); return; }
                    _memEdit = "";
                    Say("记下了");
                    await BuildMemoryAsync();
                }, "primary"),
                Btn("取消", async () =>
                {
                    _memEdit = "";
                    await BuildMemoryAsync();
                })));
        }
    }

    private async Task RenderMemSettingsAsync()
    {
        var cfg = await LoadConfigAsync();
        if (cfg is null) return;
        var mem = Sub(cfg, "memory");
        var on = Chk("开启记忆", B(mem["enabled"], true));
        var recall = Input(L(mem["recallCount"], 6).ToString(), 110);
        var daily = Chk("每天自动整理记忆", B(mem["dailyConsolidate"], true));
        var times = Input(S(mem["consolidateTimes"], "04:00,16:00"), 200);

        AddCard("记忆设置",
            Switched(on),
            Field("每次翻几条相关的", recall),
            Switched(daily),
            Row(Cell("整理时间", times), Btn("现在整理一次", async () =>
            {
                var rr = await _api.Post("api/memory/consolidate", new JsonObject());
                if (Ok(rr)) Say("开始整理了");
                else Bad(Reason(rr, "没整理成"));
            })),
            Text("整理时间写 24 小时制，多个用逗号隔开，例如 04:00,16:00。", dim: true, size: 11),
            Btn("保存记忆设置", async () =>
            {
                var list = SplitTags(times.Text).ToList();
                if (list.Count == 0) list.Add("04:00");
                var bad = list.FirstOrDefault(t => !System.Text.RegularExpressions.Regex.IsMatch(t, "^([01]?\\d|2[0-3]):[0-5]\\d$"));
                if (bad is not null)
                {
                    Bad($"整理时间格式不对（{bad}），像 04:00 这样写");
                    return;
                }
                var patch = new JsonObject
                {
                    ["memory"] = new JsonObject
                    {
                        ["enabled"] = on.IsChecked == true,
                        ["recallCount"] = Math.Clamp(IntOf(recall.Text, 6), 1, 30),
                        ["dailyConsolidate"] = daily.IsChecked == true,
                        ["consolidateTimes"] = string.Join(",", list),
                    },
                };
                if (await PatchConfigAsync(patch, "记忆设置存好了", "记忆设置没存上"))
                    await BuildMemoryAsync();
            }, "primary"));
    }

    private async Task BuildLogsPageAsync()
    {
        ResetBody();
        HeadRight.Children.Clear();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var refresh = IconBtn("\uE72C", "刷新", action: () => BuildLogsPageAsync());
        var clear = IconBtn("\uE74D", "清空日志", danger: true, action: async () =>
        {
            var r = await _api.Post("api/logs/clear", new JsonObject());
            if (Ok(r)) Say($"清掉了 {L(r["cleared"])} 条");
            else Bad(Reason(r, "没清掉"));
            await BuildLogsPageAsync();
        });
        bar.Children.Add(refresh);
        bar.Children.Add(clear);
        HeadRight.Children.Add(bar);

        var r = await _api.Get("api/logs");
        if (!Ok(r))
        {
            AddCard(null, Text(Reason(r, "读不到日志（要先以主人身份登录）"), dim: true));
            return;
        }
        var logs = Arr(r["logs"]) ?? new JsonArray();
        var sb = new StringBuilder();
        if (logs.Count == 0) sb.Append("还没有日志");
        for (var i = logs.Count - 1; i >= 0; i--)
        {
            if (Obj(logs[i]) is not { } e) continue;
            var t = S(e["time"]);
            if (t.Length == 0) t = Fmt(L(e["t"]));
            sb.Append(t).Append(" [").Append(S(e["level"], "info")).Append("] ");
            var area = S(e["area"]);
            if (area.Length > 0) sb.Append(area).Append(' ');
            sb.Append(S(e["text"])).Append('\n');
        }
        var view = new TextBox
        {
            Text = sb.ToString().TrimEnd(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            Height = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Theme.Brush("Bg"),
            BorderBrush = Theme.Brush("Line"),
            Padding = new Thickness(10),
        };
        AddCard(null, view);
    }
}
