# vscard — Windows 虚拟声卡路由工具

把音频送进虚拟声卡，让别的程序把它当"麦克风"读走，全程不依赖真实麦克风或录音硬件。

```
你的程序 (NAudio)  ──WASAPI──▶  CABLE Input  ──虚拟线缆──▶  CABLE Output  ──▶  通话 / 会议 / 直播软件
                                (播放设备)                   (录音设备)         选它作为麦克风输入
```

## 两种用法

| 入口 | 适合 | 说明 |
|---|---|---|
| **图形界面**（推荐） | 日常使用 | `gui.cmd` 双击即用：下拉选设备、拖入文件、推子调音量、托盘常驻、`Ctrl+Alt+M` 一键静音音乐 |
| 命令行 | 脚本 / 排查 | `vscard.cmd ...`，另有 `probe`、`doctor`、`mic` 等自检命令 |

两边用的是**同一套音频核心**——`gui/` 工程通过源码链接复用 `src/` 下的实现，不存在两套代码不一致的问题。

### 图形界面

```cmd
gui.cmd
```

首次运行会自动编译（十几秒），之后直接启动。

- **目标设备**：自动优先选中真正的虚拟线缆（本机为 VB-CABLE），并避开打不开的 `CABLE In 16 Ch`
- **麦克风**：想边说话边放音乐就选它；只想放音乐则保持「不使用麦克风」
- **音频文件**：可直接把**文件或整个文件夹**拖进窗口，支持多首同时叠加
- **音频目录自动识别**：见下
- **音乐 / 麦克风音量**：两个独立推子，**拖动实时生效**
- **输出延迟**：20–300 ms，开始路由后生效
- **托盘常驻**：点右上角关闭只是收进托盘，路由继续运行；真正退出走托盘右键菜单
- **`Ctrl+Alt+M`**：一键静音 / 恢复音乐，只留你的麦克风
- 设置（设备、音量、延迟、最近文件、已移除的文件）自动记忆，下次打开原样恢复

界面右下角会直接告诉你**通话软件里该把麦克风选成哪个设备**，旁边可一键复制。

### 音频目录自动识别

项目根目录下的 **`audio clip/`** 是自动识别目录：

- **启动时自动扫描**（含子目录），把里面的音频文件加进播放列表
- 播放列表为空时直接点「开始路由」即可，不用手动挑文件
- 点 **「扫描音频目录」** 可随时重新扫描（鼠标悬停会显示完整路径）
- 识别到哪些格式：`mp3 / wav / flac / m4a / aac / wma / ogg / aif / aiff / mp4 / wmv / mkv / webm`

几个行为约定：

| 操作 | 结果 |
|---|---|
| 「移除选中」 | 记住这个路径，**下次扫描不会自动加回来** |
| 「全部清空」 | 清空列表**并忘掉移除记录**，下次扫描重新载入整个目录 |
| 手动「添加文件…」或拖入 | 视为明确想要，会自动解除该文件的移除屏蔽 |

> `audio clip/` 里的音频只在本机使用，`.gitignore` 已排除其内容，不会被推到 GitHub。

## 环境自包含

本项目的**所有依赖都装在这个文件夹里**，不写 `%USERPROFILE%`，不改系统 PATH：

| 目录 | 用途 |
|---|---|
| `.dotnet/` | .NET 8 SDK 本体 |
| `.nuget/packages/` | NuGet 包缓存（由 `nuget.config` 的 `globalPackagesFolder` 指定） |
| `.dotnet-home/` | .NET CLI 运行期数据（由 `DOTNET_CLI_HOME` 指定） |
| `src/bin`、`src/obj` | 命令行版编译产物 |
| `gui/bin`、`gui/obj` | 界面版编译产物 |

`build.cmd` / `vscard.cmd` / `gui.cmd` 会自动设置好 `DOTNET_ROOT`、`NUGET_PACKAGES` 等环境变量，直接用它们即可。

> **改脚本前必读**：这三个 `.cmd` 必须是 **CRLF 换行 + 纯 ASCII**。
> `cmd.exe` 的批处理解析器遇到 LF-only 文件会把行边界读歪，症状是报出一堆莫名其妙的
> `'XXX' 不是内部或外部命令`、变量名被从中间劈开；若再混入 UTF-8 中文，会被按 GBK
> 误读成乱码并可能吞掉下一行的开头。仓库根部的 `.gitattributes` 已锁定 `*.cmd` 为 CRLF，
> 但用编辑器另存时仍需注意不要存成 UTF-8 并选 LF。提示信息一律用英文写。
> 换行符自检：`cmd /c "find /v "" build.cmd >nul"`（或直接用支持 CRLF 的编辑器查看）。

## 工程结构

```
Lonelyvoice.sln
├─ audio clip/   自动识别的音频目录（内容不入库，只保留 .gitkeep）
├─ src/          命令行工程（vscard.exe）
│    RouterSession.cs            路由会话：多路音源混成一路后渲染到虚拟端点
│    CaptureSource.cs            录音端点音源（真实麦克风）
│    AudioEndpointCatalog.cs     端点枚举、虚拟线缆识别与配对
│    ChannelMapSampleProvider.cs 任意声道数互转（5.1 / 7.1 降混）
│    LevelMeter.cs               电平统计，供 probe 判定链路是否连通
│    SourceFactory.cs            音源构造
│    Cli.cs / Program.cs         命令行入口
└─ gui/          界面工程（vscard-gui.exe）
     MainForm.cs                 主窗口 + 托盘 + 全局热键 + 音频目录扫描
     AdjustableSource.cs         可运行时调音量的音源包装器
     Program.cs                  入口 + 单实例 + --smoke 自检
```

`gui/` 不复制任何音频代码，而是用 `<Compile Include="..\src\*.cs" />` 直接引用 `src/` 的源码，
因此修 bug 只需改一处。排除 `src/Program.cs` 是为了避免与界面的入口冲突。

界面层自检（不需要界面就能跑，结果写入 `gui/bin/Release/net8.0-windows/smoke.log`）：

```cmd
gui\bin\Release\net8.0-windows\vscard-gui.exe --smoke
```

自检分两段：

- **界面布局自检** —— 报告当前显示器 DPI、缩放后的客户区尺寸、识别到的音频目录与已载入文件数，
  并逐个比对每个带文字控件的「实际宽度」与「文字所需宽度」，任一控件放不下就报 `CLIP`。
  改完界面先跑一遍，能直接发现被截断的中文。
- **音频链路自检** —— 验证 `AdjustableSource` 包装层能否被会话正常启停、运行时改音量是否生效。

> 布局检查里有个坑：`Control.GetPreferredSize()` 对**非 AutoSize 的 Button 会直接返回当前尺寸**，
> 用它做判定等于空转（永远报 OK）。必须用 `TextRenderer.MeasureText()` 自己量文字宽度再加控件留白。

### 关于高 DPI

布局坐标是 **96 DPI 基准的像素值**，而 9pt 字体会随 DPI 自动放大，两者必须一起缩放，
否则中文会被截断（`添加文件…` 只剩 `添加文`）。

WinForms 自带的 `AutoScaleMode` 在这里**不生效**（实测把 `AutoScaleMode.Dpi` +
`AutoScaleDimensions` 设好后，144 DPI 下客户区仍停在 96 DPI 的基准值），
因此改用 `MainForm.ApplyDpiScale()`：在句柄创建时按 `DeviceDpi / 96` **手工放大布局**，
只缩放坐标与尺寸、**不动字体**（字体以 pt 计，已随 DPI 放大，重复放大反而溢出）。
窗体被拖到另一块不同缩放的显示器时由 `OnDpiChanged` 按比例补一次。

## 虚拟声卡设备

本项目负责**把音频推给虚拟声卡**，虚拟设备本身属于驱动层，必须由驱动提供。

本机已装 **VB-CABLE**，`vscard probe` 实测连通：

| 通道 | 静默基线 | 播放 0.3 幅度 440Hz |
|---|---|---|
| 播放端回环 | 0.00000 RMS | 0.20461 RMS |
| 录音端采集 | 0.00000 RMS | 0.20580 RMS |

两个端点：播放端 `扬声器 (VB-Audio Virtual Cable)`，录音端 `CABLE Output (VB-Audio Virtual Cable)`。

> VB-CABLE 还会多出一个 `CABLE In 16 Ch` 播放端，它是多声道辅助入口，**打开会返回 `AUDCLNT_E_DEVICE_IN_USE`**，不要选它；程序自动识别时会避开。
>
> 本机的 `ToDesk Virtual Audio` 实测**不可用**：录音端静默采集时就自发输出约 0.33 的固定信号，与播放内容无关，不构成回环。

换方案可选 VoiceMeeter（免费，多路混音）：https://vb-audio.com/VoiceMeeter/

## 让语音通话播放音频（对方能听到）

关键点：**对方听到的是「录音端」**，也就是 `CABLE Output`。所以必须让通话软件把它选为麦克风。

```cmd
vscard.cmd probe                  :: 先确认线缆本身通，结论应为「链路连通」
vscard.cmd route -f "song.mp3"    :: 把音频送进线缆
vscard.cmd route -l -v 0.6        :: 或把系统正在播放的声音送出去
vscard.cmd doctor                 :: 查看默认输入设备是否已指向线缆
```

让通话软件接入，二选一：

- **A（推荐）**：通话软件 → 设置 → 音频 / 语音 → 麦克风 → 选 `CABLE Output (VB-Audio Virtual Cable)`
- **B**：设为系统默认 → 设置 → 系统 → 声音 → 输入 → 选 `CABLE Output`

**仍然没声音时，按顺序排查：**

| 检查项 | 位置 |
|---|---|
| 麦克风权限 | 设置 → 隐私和安全性 → 麦克风 → 允许桌面应用访问麦克风 |
| 音量 / 静音 | 声音设置 → CABLE Output → 属性 → 级别 |
| 通话软件降噪 | 关闭「自动增益 / 降噪 / 回声消除」，长时间音乐会被当噪声压掉 |
| 独占模式 | CABLE Output → 属性 → 高级 → 取消「允许应用程序独占控制该设备」 |
| 采样率不一致 | 属性 → 高级，把 CABLE Input / Output 统一为 48kHz |

> 若通话软件开启回声消除、同时本地也在外放同一路音频，AEC 会把送出的音乐消掉。改用耳机，或让本地播放在另一块设备上。

## 换成 CABLE Output 后，对方听不到我说话了

这是**预期行为**，不是故障。虚拟线缆里只有你主动送进去的音频，你的真实麦克风并不会自动流进去。

解决办法：把麦克风和音频**混成一路**再送进线缆。

```cmd
:: 先确认麦克风本身有信号（对着麦克风正常说话）
vscard.cmd mic

:: 边说话边放音频 —— 对方两样都能听到
vscard.cmd route --mic "麦克风阵列" -f "song.mp3" -v 0.7 --mic-volume 1.0

:: 只想把麦克风转进线缆（等于"延长线"）
vscard.cmd route --mic "麦克风阵列"
```

麦克风名字用 `vscard.cmd list` 里「录音端点」的名字，写片段即可（如 `麦克风阵列`）。

`MixingSampleProvider` 会把麦克风、音频文件、系统回环叠加成一路，音量分别用 `--mic-volume` 和 `--volume` 控制。

> **一定要戴耳机。** 麦克风会把外放的声音再拾进去，形成回声，音量稍大就会啸叫。这是 AEC 之外物理层面的问题，软件无法完全消除。

## 用法

```cmd
vscard.cmd list                          :: 列出全部端点，并检测虚拟线缆
vscard.cmd doctor                        :: 环境自检 + 通话接入自检
vscard.cmd probe                         :: 端到端自检：验证虚拟声卡两端是否真的连通
vscard.cmd mic                           :: 检测麦克风输入电平
vscard.cmd route --tone --seconds 5      :: 送 5 秒 440Hz 测试音
vscard.cmd route -f "song.mp3"           :: 播放文件到虚拟声卡
vscard.cmd route -m "麦克风阵列" -f "song.mp3"   :: 麦克风 + 音频一起送出去
vscard.cmd route -l -v 0.6               :: 把系统正在播放的声音送出去
```

`mic` 选项：

| 选项 | 说明 |
|---|---|
| `--device, -d <名称片段>` | 要检测的录音端点，默认取系统默认输入设备 |
| `--seconds <秒>` | 检测时长，默认 5 |

退出码：`0` 有信号 / `3` 无数据流 / `4` 几乎无信号。

`probe` 选项：

| 选项 | 说明 |
|---|---|
| `--render <名称片段>` | 播放端，默认自动识别虚拟设备 |
| `--capture <名称片段>` | 录音端，默认自动识别虚拟设备 |
| `--seconds <秒>` | 送测试音时长，默认 3 |
| `--frequency <Hz>` | 测试音频率，默认 440（指定 `--file` 时忽略） |
| `--file, -f <路径>` | 用音频文件代替测试音 |

`probe` 退出码：`0` 链路连通 / `3` 播放端或录音端无数据 / `4` 设备不环回（播放端有音频但录音端电平未抬升）。

判定逻辑（这两点都是踩过坑才加上的）：

- **基线取"播放前"与"停播回落后"两个窗口的较小值。** 线缆刚被打开时可能回放上一段音频的残留，只测"播放前"会把基线抬高，导致真正的音频看起来"没抬升"。
- **判定基于 RMS 而非峰值。** 设备初始化 / 上一次播放结束时会有一次性爆音，单个采样点就能把峰值抬到 0.3，恰好等于测试音幅度，从而造成误判。

`route` 选项：

| 选项 | 说明 |
|---|---|
| `--to <名称片段>` | 目标渲染端点，默认自动选用检测到的虚拟设备 |
| `--mic, -m <名称片段>` | 混入一个录音端点（通常是真实麦克风），对方就能听到你说话 |
| `--mic-volume, --mv <0-1>` | 麦克风音量，默认 1.0 |
| `--file, -f <路径>` | 音频文件，可重复指定多首 |
| `--loopback, -l` | 混入系统正在播放的声音（WASAPI 回环） |
| `--tone` | 混入 440Hz 测试音 |
| `--volume, -v <0-1>` | 音频文件音量，默认 1.0 |
| `--latency <毫秒>` | 输出缓冲，默认 80 |
| `--seconds <秒>` | 运行指定时长后自动退出 |

多路音源可以叠加——`--mic`、`--file`、`--loopback` 同时给，会在 `MixingSampleProvider` 里混成一路再送出去，**本地扬声器照常发声，互不影响**。

麦克风采集内部有 200ms 缓冲（`CaptureSource`），并对缓冲不足的部分补静音，避免混音输出断续。因此麦克风这一路会比纯音频多约 200ms 延迟。

## 编译

```cmd
build.cmd
```

一次编译命令行与界面两个工程。

## 更新 SDK

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -InstallDir "<项目根目录>\.dotnet" -NoPath
```

## 已知限制

- 采样率跟随目标设备的 `MixFormat`，程序内部统一重采样到该采样率。
- 使用 WASAPI **共享模式**，独占模式会锁死设备，代码中未启用。
- 源音频超过 2 声道（5.1 / 7.1）时由 `ChannelMapSampleProvider` 降混：前两声道保持原始电平，其余声道取均值后以半权重混入两侧，并限幅防削波。
