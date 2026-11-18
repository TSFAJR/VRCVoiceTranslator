# VRChat 语音翻译

面向 Windows 11 x64、PICO 4 Ultra、Virtual Desktop、SteamVR 和 VRChat PC 版的托盘工具。默认按住 `F8` 或 SteamVR 手柄组合键说中文，松手后调用百炼和 DeepL。Chatbox 收到原文和英日译文，头显字幕默认显示英日译文 3 秒。

录音开始时播放两声上行短音，结束时播放两声下行短音。提示音从 Windows 当前默认输出设备播放。

## 启动与配置

安装包安装到当前用户目录并添加开始菜单入口，安装时默认勾选「创建桌面图标」。便携版解压后运行 `VRCVoiceTranslator.exe`。两个版本均自带运行时，共用下文的配置路径。卸载时默认保留配置；勾选「删除所有配置文件」会清除本机保存的设置和 API Key。

1. 运行 `VRCVoiceTranslator.exe`。轻量发布包需要 [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。保留发布目录里的清单和 DLL。
2. 确认 Virtual Desktop 已将 PICO 麦克风传到 Windows。在「麦克风与触发」刷新列表，选择 Virtual Desktop 的录音设备，点「测试电平」并说话。关闭设置或开始语音录音时会结束电平测试。
3. 在「识别与翻译」填入百炼 API Key 和工作空间地址，将 `wss://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/api-ws/v1/inference` 中的占位符换成工作空间 ID。填入已有的 DeepL API Free Key，选择源语言，用译文行左侧的手柄拖动排序，点「保存设置」。
4. 在 VRChat 的 Action Menu 中打开「Options → OSC → Enabled」。在「Chatbox」点「发送测试消息」，确认游戏内出现中英日样例。也可在「手动输入」打字，点击「翻译并发送」或按 `Ctrl+Enter`。工具使用已保存的 DeepL、源语言和译文顺序发送原文及可用译文。翻译失败时仍会发送原文。超长内容按 Chatbox 限制裁剪，最近输出预览会显示实际发送内容。默认端口 `9000`，如为 VRChat 指定了不同的 OSC 接收端口，填写对应值。「OSC 已发送」表示本机已提交 UDP 数据报。
5. 在「VR 字幕」通过桌面预览调整字号、不透明度和角度，也可点「在 VR 中预览」在头显里查看实际效果和距离。退出预览后恢复正常字幕行为。点「保存设置」写入修改。关闭设置窗口后应用驻留托盘。

## SteamVR 手柄绑定

1. 让 Virtual Desktop、SteamVR 和工具同时运行。Virtual Desktop 在 SteamVR 中显示为 Oculus Touch 时，工具随附的预设是**左手扳机＋右手握持键**。SteamVR 通常会自动加载预设。当前若选中了已保存的自定义绑定，可在 `VRC Voice Translator` 的控制器绑定页切回该应用的默认绑定。
2. 原生组合键模式优先读取预设中的 `PushToTalk`。如果 SteamVR 报告原生动作不可用，而预设中的两个动作均可读取，工具会使用双动作备用。如需自定义原生组合，在 SteamVR 编辑器为两个物理键各添加 Button 输入，再用「添加新和弦」把两键的 Click 组合绑到 `PushToTalk`，保存个人绑定。让 VRChat 保持在前台，用工具里的按住状态确认两种按键顺序和松开任意一键。
3. 如果原生组合键只产生瞬时按下，或编辑器无法保存和弦，在工具中选择「双动作同时按住」并点「保存设置」。预设已把左手扳机和右手握持键分别映射到 `PushToTalkA`、`PushToTalkB`。自定义时只需在两个物理键各自的 Button → Click 中分配这两个动作。
4. 如果 VRChat 在前台时发生输入冲突，先试一组空闲按键。确有需要时，在 SteamVR 开发者设置启用 **Experimental overlay input overrides**，再勾选工具中的「使用 SteamVR 实验性输入覆盖」并保存。启用后 VRChat 可能收不到这些物理按键。

SteamVR 未启动时，键盘热键、百炼、DeepL 和 Chatbox 仍可工作。录音触发独立于 VRChat 静音状态。

## 配置文件

`%LOCALAPPDATA%\VRCVoiceTranslator\config.json` 由首次启动生成。两把 Key 按普通字符串明文保存。可通过「打开配置目录」定位。手动修改后重启。缺失字段补齐默认值，配置错误会显示文件位置，修正后重新启动。状态和耗时位于同一目录的 `activity.log`。

| JSON 字段 | 默认值与用途 |
| --- | --- |
| `microphoneId` | `null` 使用 Windows 默认麦克风，也可由设置界面保存具体设备 ID |
| `keyboardVirtualKey` | `119`（`F8`），Windows 虚拟键码的十进制值 |
| `vrBinding`, `experimentalVrInputOverride` | `"chord"`, `false`。双动作模式为 `"twoActions"` |
| `maxRecordingSeconds` | `12`，范围 1–60 秒 |
| `bailianEndpoint`, `bailianApiKey`, `deepLApiKey` | 工作空间的 `wss://` 地址和两把 Key，Key 初始留空 |
| `sourceLanguage`, `targetLanguages` | `"chinese"`, `["EN-US", "JA"]`。源语言还可用 `english`、`japanese`、`korean`、`auto` |
| `chatboxEnabled`, `oscPort` | `true`, `9000` |
| `subtitles.enabled`, `subtitles.showOriginal` | `true`, `false` |
| `subtitles.durationSeconds`, `subtitles.fontPercent`, `subtitles.opacityPercent` | `3`, `100`, `90` |
| `subtitles.downAngleDegrees`, `subtitles.distanceMeters` | `22`, `1.5`。负角度向上移动 |

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

自动检查使用模拟百炼 WebSocket、模拟 DeepL HTTP 和本机 UDP。实机验收需用真实 Key 跑一次 `F8`，查看日志中的「松手或自动提交至最终原文」与「原文至译文就绪」。随后戴上 PICO，在 VRChat 前台按齐组合键并说话，松开任意一键后检查一条 Chatbox 多语言消息、头显字幕跟随转头、3 秒退场及下一次重新显示。长文本在 Chatbox 中使用保守的换行估算，客户端的字体和自动换行效果需在 VRChat 内确认。

Release 构建和自动检查已通过。发布版已连接本机 SteamVR 并成功加载 Oculus Touch 预设。实时探测确认左手扳机、右手握持键的两个动作均可读取。此前也实测了 Virtual Desktop 麦克风、百炼到 DeepL 的真实请求和 OpenVR 字幕层。语音结果和手动输入都通过了 OSC 本机回环检查。VRChat 前台的实际按住与松开、Chatbox 和头显显示仍需佩戴设备确认。
