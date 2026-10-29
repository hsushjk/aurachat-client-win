# aurachat-client-win

![warn](./other/warn.png)

DSH 插件 [dsh-aurachat](https://github.com/hsushjk/dsh-aurachat) 的 Windows 客户端：C# + WPF，`net9.0-windows`，单文件。协议与 [安卓端](https://github.com/hsushjk/aurachat-client-android) 一致：HTTP + Bearer token + JSON，实时消息走 SSE。

[实机截图](./other/1.png)

**入口 / 连接**

- 服务端默认监听 `2332` 端口的前提下，地址栏可仅填 ip ：`192.168.1.5` → `http://192.168.1.5:2332`
- 支持https
- 登录态本地保存
- 自签证书会弹「指纹（SHA-256）确认」对话框，核对一致才信任并连接，其余连接仍走系统信任链。

**导航**

- 聊天 / 空间 / 心情 / 概览 / 记忆 / 设置 / 日志 —— 结构照网页端顶层导航；概览、记忆、日志按角色只对主人显示
- 空间 / 心情 / 概览 / 记忆 / 日志点开是独立的 standalone 窗口（`AuraChat · 空间/心情/概览/记忆/日志`）
- 心情页：心情值 + 区间进度条 + 最近变化 + 「心情怎么算」（半衰期 / 平常区间 / 判断情绪 / 重置）
- 概览页：6 格统计 + 状态 + 「调试 / 试一下」
- 设置（齿轮或顶栏「设置」进入，20 页）

**聊天**

- 文字 / 表情 / 图片 / 语音收发，SSE 实时收消息 + 断线自动重连 + 重连补拉（按 id 去重）。
- 图片 / 语音按 `mediaId` 下载并落盘缓存；媒体被服务端清掉显示「图片已过期 / 语音已过期」。
- 分片分页：首屏 `file=last`，滚到顶自动 `file=prev&before=`，顶部「查看更早的记录」按钮兜底，`history` 事件裁掉过期消息。
- 语音播放进度（`2″ / 6″` + 蓝色进度条），再点一下停止；未缓存时先显示「下载中…」再自动播。
- 发送失败重试：灰底 + 红感叹号 + 「重试」按钮。
- 未读数：窗口标题 `AuraChat (n)`、托盘提示、右下角圆形「↓」跳回底部。
- 草稿：输入 500ms 后自动存，重启回填；头部放大镜搜索（Ctrl+F 不变，Esc 关闭）。
- 日期分隔（今天 / 昨天 / M月d日）；右键气泡：复制文字 / 保存图片… / 保存语音…（存到 `图片\AuraChat`）。
- 双击托盘回到聊天、窗口不在前台时弹通知。


## 编译

需要 .NET **9 SDK**。提供工具链脚本，会装到 `%USERPROFILE%\tools\dotnet`，不修改系统 PATH：

```
powershell -ExecutionPolicy Bypass -File _setup-toolchain.ps1
build.cmd
```

已有 .NET 9 SDK 的机器跳过第一步。`build.cmd` 会先找本地 tools 目录，再找 PATH 上的 `dotnet`，然后把单文件 exe 发布到 `dist\`。

手动发布：

```
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o ..\dist
```

> 编译前先关掉正在运行的 `AuraChat.exe`，否则复制产物时会因文件占用失败（`MSB3027`）；`build.cmd` 会提前检查。

## 连接方式

| 你填的 | 实际请求的 |
| --- | --- |
| `192.168.1.5` | `http://192.168.1.5:2332` |
| `192.168.1.5:2332` | `http://192.168.1.5:2332` |
| `chat.example.com` | `http://chat.example.com:2332` |
| `https://chat.example.com` | `https://chat.example.com` |

## 数据

数据文件跟 `AuraChat.exe` 同目录：`client.json`（登录态）、`draft.txt`（草稿）、`ui.json`（主题）、`media\`（媒体缓存，LRU 300 MB）。删目录即彻底清数据。聊天记录和媒体本体存**服务端**。

## 使用的接口

`api/login`、`api/logout`、`api/state`、`api/messages`（GET/POST，`?file=last|prev&before=`）、`api/media/<id>`、`api/typing`、`api/stream`、`api/sticker/<md5>`、`api/stickers`（含 upload/patch/delete）、`api/config`（GET/PATCH）、`api/status`、`api/logs`、`api/test`、`api/mood`、`api/memory`（含 profile/style/insight/consolidate）、`api/diary`（含 comment/reply/delete/run/social-run）、`api/personas`（含 detail/save/delete）、`api/guests`、`api/account/password`、`api/avatar`、`api/background`、`api/selfdrive`、`api/voice/test`、`api/voice/edge`。

## 目录结构

```text
aurachat-client-win/
├── AuraChat.Windows/                源码
│   ├── App.xaml / App.xaml.cs       入口 + 主题加载
│   ├── MainWindow.xaml(.cs)         主窗（聊天 + 顶栏导航）
│   ├── SettingsWindow.xaml(.cs)     设置窗口（子窗口 standalone 也用）
│   ├── SettingsWindow.Pages.cs / .Sections.cs / .Sections2.cs
│   ├── Api.cs                       HTTP 接口 + 自签证书指纹
│   ├── Bubble.cs                    消息气泡模型
│   ├── MediaCache.cs                媒体下载缓存
│   ├── ImageUtil.cs                 图片处理
│   ├── Mci.cs                       语音播放
│   ├── Theme.cs                     主题与玻璃效果
│   ├── AuraChat.Windows.csproj
│   ├── app.ico / app.manifest
├── icon/                            图标源
├── build.cmd                        编译
├── _setup-toolchain.ps1
└── README.md
```
