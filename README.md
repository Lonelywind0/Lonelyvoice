# vscard — Windows 虚拟声卡路由工具

把音频送进虚拟声卡，让别的程序把它当"麦克风"读走，全程不依赖真实麦克风或录音硬件。

```
你的程序 (NAudio)  ──WASAPI──▶  CABLE Input  ──虚拟线缆──▶  CABLE Output  ──▶  游戏 / 会议 / 直播软件
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

## 虚拟声卡设备：需要安装

本项目负责**把音频推给虚拟声卡**，虚拟设备本身属于驱动层，必须由驱动提供。

### 本机的 ToDesk Virtual Audio 实测不可用

本机存在 `扬声器 (ToDesk Virtual Audio)` 与 `麦克风 (ToDesk Virtual Audio)` 这一对端点，但它**不是可路由的虚拟线缆**。`vscard probe` 实测（基线窗口 2.5 秒）：

| 通道 | 静默基线峰值 | 播放 440Hz 时峰值 |
|---|---|---|
| 播放端回环（我们实际送出的） | 0.00000 | 0.30000 |
| 录音端采集（设备回环回来的） | 0.32849 | 0.32849 |

播放端确实收到了我们的音频，但录音端电平**毫无变化**——它在静默采集时就会自发输出约 0.33 的固定信号，与播放内容完全无关。**结论：该设备不环回，不能当虚拟声卡用。**

> 排查提示：这个自发信号足以骗过"只看播放期电平"的检测。`probe` 因此采用**长基线 + 双端对比**，请以它给出的结论为准。

### 请安装真正的虚拟线缆

- **VB-CABLE**（免费，推荐）：https://vb-audio.com/Cable/
- **VoiceMeeter**（免费，多路混音）：https://vb-audio.com/Voicemeeter/

装完**必须重启**。之后程序会自动优先选用检测到的虚拟设备，无需改参数。装好后用 `vscard probe` 复核一遍即可。

## 用法

```cmd
vscard.cmd list                       :: 列出全部端点，并检测虚拟线缆
vscard.cmd doctor                     :: 环境自检
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
- 若目标程序要求 48kHz 输入而 `CABLE Input` 是 44.1kHz，请到"声音设置 → 属性 → 高级"里统一采样率。
