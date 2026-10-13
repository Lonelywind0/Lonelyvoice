# vscard — Windows 虚拟声卡路由工具

把音频送进虚拟声卡，让别的程序把它当"麦克风"读走，全程不依赖真实麦克风或录音硬件。

```
你的程序 (NAudio)  ──WASAPI──▶  CABLE Input  ──虚拟线缆──▶  CABLE Output  ──▶  通话 / 会议 / 直播软件
                                (播放设备)                   (录音设备)         选它作为麦克风输入
```

## 环境自包含

本项目的**所有依赖都装在这个文件夹里**，不写 `%USERPROFILE%`，不改系统 PATH：

| 目录 | 用途 |
|---|---|
| `.dotnet/` | .NET 8 SDK 本体 |
| `.nuget/packages/` | NuGet 包缓存（由 `nuget.config` 的 `globalPackagesFolder` 指定） |
| `.dotnet-home/` | .NET CLI 运行期数据（由 `DOTNET_CLI_HOME` 指定） |
| `src/bin`、`src/obj` | 编译产物 |

`build.cmd` / `vscard.cmd` 会自动设置好 `DOTNET_ROOT`、`NUGET_PACKAGES` 等环境变量，直接用它们即可。

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

## 用法

```cmd
vscard.cmd list                       :: 列出全部端点，并检测虚拟线缆
vscard.cmd doctor                     :: 环境自检 + 通话接入自检
vscard.cmd probe                      :: 端到端自检：验证虚拟声卡两端是否真的连通
vscard.cmd route --tone --seconds 5   :: 送 5 秒 440Hz 测试音
vscard.cmd route -f "song.mp3"        :: 播放文件到虚拟声卡
vscard.cmd route -l -v 0.6            :: 把系统正在播放的声音送出去
```

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
| `--file, -f <路径>` | 音频文件，可重复指定多首 |
| `--loopback, -l` | 混入系统正在播放的声音（WASAPI 回环） |
| `--tone` | 混入 440Hz 测试音 |
| `--volume, -v <0-1>` | 音量，默认 1.0 |
| `--latency <毫秒>` | 输出缓冲，默认 80 |
| `--seconds <秒>` | 运行指定时长后自动退出 |

多路音源可以叠加——`--file` 和 `--loopback` 同时给，会在 `MixingSampleProvider` 里混成一路再送出去，**本地扬声器照常发声，互不影响**。

## 编译

```cmd
build.cmd
```

## 更新 SDK

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -InstallDir "<项目根目录>\.dotnet" -NoPath
```

## 已知限制

- 采样率跟随目标设备的 `MixFormat`，程序内部统一重采样到该采样率。
- 使用 WASAPI **共享模式**，独占模式会锁死设备，代码中未启用。
- 源音频超过 2 声道（5.1 / 7.1）时由 `ChannelMapSampleProvider` 降混：前两声道保持原始电平，其余声道取均值后以半权重混入两侧，并限幅防削波。
