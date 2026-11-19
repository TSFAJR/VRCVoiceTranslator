# VRChat 语音翻译

面向 Windows 10/11 x64、PICO 4 Ultra、Virtual Desktop、SteamVR 和 VRChat PC 版的托盘工具。默认按住 `F8` 或 SteamVR 手柄组合键说中文，松手后用百炼识别，再由所选服务翻译。Chatbox 收到原文和英日译文，头显字幕默认显示英日译文 3 秒。

录音开始时播放两声上行短音，结束时播放两声下行短音。提示音从 Windows 当前默认输出设备播放。

## 启动与配置

安装包安装到当前用户目录并添加开始菜单入口，安装时默认勾选「创建桌面图标」。便携版解压后运行 `VRCVoiceTranslator.exe`。两个版本均自带运行时，共用下文的配置路径。卸载时默认保留配置；勾选「删除所有配置文件」会清除本机保存的设置和 API Key。

1. 运行 `VRCVoiceTranslator.exe`。轻量发布包需要 [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。保留发布目录里的清单和 DLL。
2. 确认 Virtual Desktop 已将 PICO 麦克风传到 Windows。在「麦克风与触发」刷新列表，选择 Virtual Desktop 的录音设备，点「测试电平」并说话。关闭设置或开始语音录音时会结束电平测试。
3. 在「识别与翻译」填入百炼 **Workspace ID（业务空间 ID）** 和 API Key。地域默认华北 2（北京），新加坡业务空间请选择「新加坡」。程序自动生成识别、播报和 Qwen-MT 地址。选择翻译服务、源语言和译文顺序后点「保存设置」。 [Workspace ID 获取方式](https://help.aliyun.com/zh/model-studio/obtain-the-app-id-and-workspace-id)
4. 在 VRChat 的 Action Menu 中打开「Options → OSC → Enabled」。在「Chatbox」点「发送测试消息」，确认游戏内出现中英日样例。也可在「手动输入」打字，点击「翻译并发送」或按 `Ctrl+Enter`。工具按保存的翻译服务、源语言和译文顺序发送原文及可用译文。翻译失败时仍会发送原文。超长内容按 Chatbox 限制裁剪，最近输出预览会显示实际发送内容。默认端口 `9000`，如为 VRChat 指定了不同的 OSC 接收端口，填写对应值。「OSC 已发送」表示本机已提交 UDP 数据报。
5. 在「VR 字幕」通过桌面预览调整字号、不透明度和角度，也可点「在 VR 中预览」在头显里查看实际效果和距离。退出预览后恢复正常字幕行为。点「保存设置」写入修改。关闭设置窗口后应用驻留托盘。

## SteamVR 手柄绑定

1. 让 Virtual Desktop、SteamVR 和工具同时运行。Virtual Desktop 在 SteamVR 中显示为 Oculus Touch 时，工具随附的预设是**左手扳机＋右手握持键**。SteamVR 通常会自动加载预设。当前若选中了已保存的自定义绑定，可在 `VRC Voice Translator` 的控制器绑定页切回该应用的默认绑定。
2. 原生组合键模式优先读取预设中的 `PushToTalk`。如果 SteamVR 报告原生动作不可用，而预设中的两个动作均可读取，工具会使用双动作备用。如需自定义原生组合，在 SteamVR 编辑器为两个物理键各添加 Button 输入，再用「添加新和弦」把两键的 Click 组合绑到 `PushToTalk`，保存个人绑定。让 VRChat 保持在前台，用工具里的按住状态确认两种按键顺序和松开任意一键。
3. 如果原生组合键只产生瞬时按下，或编辑器无法保存和弦，在工具中选择「双动作同时按住」并点「保存设置」。预设已把左手扳机和右手握持键分别映射到 `PushToTalkA`、`PushToTalkB`。自定义时只需在两个物理键各自的 Button → Click 中分配这两个动作。
4. 如果 VRChat 在前台时发生输入冲突，先试一组空闲按键。确有需要时，在 SteamVR 开发者设置启用 **Experimental overlay input overrides**，再勾选工具中的「使用 SteamVR 实验性输入覆盖」并保存。启用后 VRChat 可能收不到这些物理按键。

SteamVR 未启动时，键盘热键、识别、翻译和 Chatbox 仍可工作。录音触发独立于 VRChat 静音状态。

## 选择翻译服务

「识别与翻译」的「翻译服务」同时用于自己的语音、手动输入和来话翻译。DeepL 是升级后的默认选项，使用已保存的 DeepL API Free Key。`qwen-mt-flash` 和 `qwen-mt-lite` 复用百炼的 Workspace ID、地域和 API Key。Flash 支持更多语种，Lite 适合低延迟的日常对话。选到模型不支持的目标语种时，工具会提示原因，并继续翻译其余语种。Qwen-MT 将 `EN-US` 和 `EN-GB` 都映射为英语。 [Qwen-MT 模型与语种说明](https://docs.bailian.console.aliyun.com/zh/model-studio/machine-translation)

## 翻译其他玩家的语音

启动 VRChat PC 版和 SteamVR，确认已保存百炼 Workspace ID、百炼 API Key 和「显示头显字幕」。若翻译服务选了 DeepL，还需填入 DeepL API Key。在「识别与翻译」打开「来话翻译」开关即可持续监听，运行状态显示在开关旁。关闭开关、退出 VRChat、停用头显字幕或退出工具都会停止。工具每次启动默认关闭来话翻译。

来话使用自动语种识别，逐句翻译成简体中文，仅在自己的头显显示「来话」字幕。自己的发言字幕优先显示，来话只补显仍在有效期内的最新一句。来话和自己的录音独立使用已保存的云服务配置。来话约停顿 0.5 秒断句，连续有声音时也会把每段识别音频限制在约 10 秒。连续静音约 5 秒后暂停识别，有声音时自动继续。

- **Windows 11** 按进程采集 VRChat 的声音，包括游戏内的音乐和音效。
- **Windows 10 22H2** 在「识别与翻译」选择 VRChat 实际使用的播放设备，例如 Virtual Desktop 的音频设备，再打开开关。同一设备播放的所有应用声音都可能被采集。想隔离其他应用时，可在 Windows「应用音量和设备首选项」把 VRChat 和其他应用分配到不同的播放设备。选择「Windows 默认播放设备」时，工具在每次开启时选取当时的默认设备。 [Microsoft 播放设备回环说明](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)

采集的音频会发给百炼，识别文本会发给所选翻译服务。本地只做短时内存缓冲，日志只记录状态、错误和耗时。

背景音较重时，先在 VRChat 调低世界和头像音量并保留玩家语音音量。若仍容易串成一长句，可在「识别与翻译」关停来话，勾选「加强底噪过滤」后重新开启。轻声容易漏时可以关掉加强过滤。底噪过滤主要针对较轻的声音，多人同时说话或很响的背景音仍会进入识别。状态可区分未识别到完整句、已经识别为中文、翻译失败。 [百炼断句和噪音参数](https://help.aliyun.com/zh/model-studio/qwen-asr-message-client-events)

## 语音播报

在「语音播报」勾选「启用自动播报」并保存。升级后默认关闭，选中的模式默认为「中文原文」。三种模式可各自编辑音色 ID。「试听」使用当前界面的模式、音色、音量和输出设备，调用已保存百炼工作空间的真实 TTS 接口。总开关关闭时也可试听。

| 模式 | 播报内容 | 默认音色 |
| --- | --- | --- |
| 中文原文 | ASR 完整原文，识别结束后与翻译并行 | 预填的自定义音色 |
| 英文译文 | 完整的 `EN-US` 译文 | 同一自定义音色 |
| 日文译文 | 完整的 `JA` 译文 | `longanfengyue_v3.1` |

英文或日文模式需要在「识别与翻译」中加入对应目标语言。译文不可用时，本次播报会跳过，原因显示在「语音播报」页。每次开始新录音时，正在合成或播放的上一条播报会停止。百炼合成使用同一组工作空间地址和 API Key。自定义音色需要该工作空间的使用权限。 [百炼 Qwen-Audio-TTS WebSocket 指南](https://help.aliyun.com/zh/model-studio/qwen-audio-tts-websocket-api)

开启自动播报后，在「Chatbox」手动输入并点击「翻译并发送」或按 `Ctrl+Enter`，也会按所选模式播报。原文模式读完整的输入文字，英日模式等对应译文。若此时正在录音，仍会发送 Chatbox 消息，本次手动播报跳过。

输出设备默认使用 Windows 默认播放设备。只在自己耳机里听时，可选 Virtual Desktop 耳机输出。让房间里的人听见时，工具选虚拟声卡的 `CABLE Input`，VRChat 麦克风选 `CABLE Output`，并在游戏里开启传声。工具「麦克风与触发」仍选 PICO 或 Virtual Desktop 麦克风。

想让房间和自己的耳机同时听到时，在「语音播报」打开「监听播报」，选择自己的耳机为「监听设备」并保存。两个设备共用同一条合成音频。若选的是同一个设备，只播放一次。监听设备出错时，主输出继续播报，状态会提示监听错误。

## 配置文件

`%LOCALAPPDATA%\VRCVoiceTranslator\config.json` 由首次启动生成。两把 Key 按普通字符串明文保存。可通过「打开配置目录」定位。手动修改后重启。缺失字段补齐默认值，配置错误会显示文件位置，修正后重新启动。

右上角「日志」左侧的太阳、月亮、显示器图标分别选择浅色、深色和跟随系统。选择后立即切换，点击「保存设置」即可保留。配置字段 `appearance` 接受 `system`（默认）、`light` 或 `dark`。跟随系统使用 Windows 的默认应用模式。

右上角「日志」可查看状态、耗时和错误。窗口读取同一目录的 `activity.log` 和上一份 `activity.log.old`。点击「刷新」可查看新记录，可选中文本复制。

升级时，如果旧 `bailianEndpoint` 使用北京或新加坡的官方业务空间域名，程序会自动从中提取 Workspace ID 和地域。若 Workspace ID 输入框为空，请从百炼控制台复制业务空间 ID 后保存。

| JSON 字段 | 默认值与用途 |
| --- | --- |
| `microphoneId` | `null` 使用 Windows 默认麦克风，也可由设置界面保存具体设备 ID |
| `incomingPlaybackDeviceId` | `null` 使用 Windows 默认播放设备，用于 Windows 10 来话回环 |
| `incomingStrongerNoiseFilter` | `false`。开启后加强来话底噪过滤 |
| `keyboardVirtualKey` | `119`（`F8`），Windows 虚拟键码的十进制值 |
| `vrBinding`, `experimentalVrInputOverride` | `"chord"`, `false`。双动作模式为 `"twoActions"` |
| `maxRecordingSeconds` | `12`，范围 1–60 秒 |
| `bailianWorkspaceId`, `bailianRegion` | `""`, `"beijing"`。新加坡为 `"singapore"` |
| `bailianApiKey`, `deepLApiKey` | 两把 Key 初始留空。Qwen-MT 使用百炼 API Key |
| `translationProvider` | `"deepL"`。还可用 `"qwen-mt-flash"`、`"qwen-mt-lite"` |
| `sourceLanguage`, `targetLanguages` | `"chinese"`, `["EN-US", "JA"]`。源语言还可用 `english`、`japanese`、`korean`、`auto` |
| `chatboxEnabled`, `oscPort` | `true`, `9000` |
| `subtitles.enabled`, `subtitles.showOriginal` | `true`, `false` |
| `subtitles.durationSeconds`, `subtitles.fontPercent`, `subtitles.opacityPercent` | `3`, `100`, `90` |
| `subtitles.downAngleDegrees`, `subtitles.distanceMeters` | `22`, `1.5`。负角度向上移动 |
| `speech.enabled`, `speech.mode` | `false`, `"chineseOriginal"`。其他模式为 `"englishTranslation"`、`"japaneseTranslation"` |
| `speech.chineseVoice`, `speech.englishVoice`, `speech.japaneseVoice` | 各模式独立的音色 ID |
| `speech.volumePercent`, `speech.outputDeviceId` | `100`, `null`。`null` 使用 Windows 默认播放设备 |
| `speech.monitorEnabled`, `speech.monitorDeviceId` | `false`, `null`。监听设备默认使用 Windows 默认播放设备 |

## 构建、测试与发布

使用 .NET 10 SDK 在仓库根目录运行。随源码提供的 Valve C# 绑定和 DLL 来自 [OpenVR SDK v2.15.6](https://github.com/ValveSoftware/openvr/releases/tag/v2.15.6)，许可证随发布目录复制。

```powershell
dotnet build VRCVoiceTranslator.slnx -c Release
dotnet run --project tests/Tests.csproj -c Release
dotnet publish src/App/App.csproj -c Release -r win-x64 --self-contained false -o artifacts/win-x64
dotnet publish src/App/App.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64-self-contained
```

使用 Inno Setup 6.7 或更新版本生成安装包和便携 ZIP。将 `-InnoCompiler` 指向 `ISCC.exe`。两个产物位于 `artifacts/release`。

```powershell
.\packaging\Build-Packages.ps1 -InnoCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

自动检查使用模拟百炼 WebSocket、模拟 DeepL 和 Qwen-MT HTTP，以及本机 UDP。实机验收需用真实 Key 跑一次 `F8`，查看日志中的「松手或自动提交至最终原文」与「原文至译文就绪」。随后戴上 PICO，在 VRChat 前台按齐组合键并说话，松开任意一键后检查一条 Chatbox 多语言消息、头显字幕跟随转头、3 秒退场及下一次重新显示。长文本在 Chatbox 中使用保守的换行估算，客户端的字体和自动换行效果需在 VRChat 内确认。

佩戴 PICO 验收来话时，让其他玩家分别说英文和日文，检查是否逐句显示中文字幕。在 Windows 11 上同时用浏览器播放语音，确认字幕只来自 VRChat。在 Windows 10 上确认所选播放设备和 VRChat 的输出一致。测试一轮自己的按键录音，确认自己的译文优先显示。再停止来话翻译或退出游戏，确认字幕立即清除、迟到结果不再显示。日志中的「来话句末至识别（估算）」与「来话识别至字幕」分别记录两段延迟。

播报实机验收需要真实百炼 Key 和音色权限。先分别试听三种模式，再戴上 PICO 用手柄组合键录音。可暂时让翻译服务失败，验证中文原文仍能播报，并在播报中开始下一轮录音确认即时停止。英文、日文模式分别检查对应译文、错误提示及正常的 Chatbox 和 VR 字幕。日志中的「播报文本就绪至首个音频块」和「播报文本就绪至开始播放」记录两段耗时。

Release 构建和自动检查已通过。发布版已连接本机 SteamVR 并成功加载 Oculus Touch 预设。实时探测确认左手扳机、右手握持键的两个动作均可读取。此前也实测了 Virtual Desktop 麦克风、百炼到 DeepL 的真实请求和 OpenVR 字幕层。语音结果和手动输入都通过了 OSC 本机回环检查。VRChat 前台的实际按住与松开、Chatbox 和头显显示仍需佩戴设备确认。
