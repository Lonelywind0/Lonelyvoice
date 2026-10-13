using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace VirtualSoundCard;

/// <summary>
/// 命令行入口。子命令：list / doctor / route。
/// </summary>
internal static class Cli
{
    private const string DefaultTargetHint = "CABLE Input";

    /// <summary>
    /// 探针的基线窗口。必须足够长：部分虚拟设备（如 ToDesk Virtual Audio）在采集
    /// 开始后会自发输出一个固定电平的信号，窗口过短会把它误判成"我们送出的音频"。
    /// </summary>
    private const int BaselineMilliseconds = 2500;

    /// <summary>
    /// 采集启动后先丢弃的唤醒窗口。设备刚打开（以及上一次播放刚结束）时，驱动常会
    /// 产生一次性跳变/爆音；若计入基线会把基线抬高，使真正的测试音看起来"没抬升"。
    /// </summary>
    private const int SettleMilliseconds = 500;

    /// <summary>
    /// 播放结束后的回落窗口。虚拟线缆刚被打开时可能回放上一段音频的残留，
    /// 因此"播放前基线"未必干净；取它与回落基线的较小值作为噪声底才可靠。
    /// </summary>
    private const int QuiescenceMilliseconds = 1500;

    /// <summary>判定电平是否"抬起"的下限，低于它一律视为静音。</summary>
    private const double SilenceFloor = 0.002;

    public static int Run(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 部分宿主（如重定向管道）不允许改编码，忽略即可。
        }

        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" or "ls" or "devices" => ListDevices(),
                "doctor" or "check" => Doctor(),
                "probe" or "selftest" => Probe(args[1..]),
                "mic" or "mike" or "microphone" => MicCheck(args[1..]),
                "route" or "run" => Route(args[1..]),
                "help" or "-h" or "--help" => PrintHelp(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (AudioEndpointNotFoundException ex)
        {
            Console.Error.WriteLine($"[错误] {ex.Message}");
            Console.Error.WriteLine("       提示：执行 vscard list 查看全部端点。若列表中没有虚拟线缆，请先安装 VB-CABLE：https://vb-audio.com/Cable/");
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"[错误] {ex.Message}");
            return 3;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[错误] {ex.Message}");
            Console.Error.WriteLine("       执行 vscard help 查看用法。");
            return 4;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x8889000A))
        {
            Console.Error.WriteLine("[错误] 目标音频设备已被占用，无法打开（AUDCLNT_E_DEVICE_IN_USE）。");
            Console.Error.WriteLine("       请关闭正在独占该设备的程序，或用 --to / --render 指定其它端点。");
            Console.Error.WriteLine("       提示：VB-CABLE 的 \"CABLE In 16 Ch\" 是多声道辅助入口，常被独占，请改用标准端点。");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string name)
    {
        Console.Error.WriteLine($"[错误] 未知子命令：{name}");
        Console.Error.WriteLine();
        PrintHelp();
        return 4;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("vscard - 虚拟声卡路由工具");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  vscard list                              列出全部音频端点，并检测虚拟线缆");
        Console.WriteLine("  vscard doctor                            环境自检");
        Console.WriteLine("  vscard probe [选项]                      端到端自检：验证虚拟声卡两端是否连通");
        Console.WriteLine("  vscard mic [选项]                        检测麦克风输入电平（混音前先确认麦克风有信号）");
        Console.WriteLine("  vscard route [选项]                      把音频路由到虚拟声卡");
        Console.WriteLine();
        Console.WriteLine("probe 选项：");
        Console.WriteLine("  --render <名称片段>   播放端，默认自动识别虚拟设备");
        Console.WriteLine("  --capture <名称片段>  录音端，默认自动识别虚拟设备");
        Console.WriteLine("  --seconds <秒>        送测试音时长，默认 3");
        Console.WriteLine("  --frequency <Hz>      测试音频率，默认 440（指定 --file 时忽略）");
        Console.WriteLine("  --file, -f <路径>     用音频文件代替测试音");
        Console.WriteLine();
        Console.WriteLine("route 选项：");
        Console.WriteLine("  --to <名称片段>        目标渲染端点，默认自动选用系统中的虚拟音频设备");
        Console.WriteLine("  --mic, -m <名称片段>   混入一个录音端点（通常是真实麦克风），对方就能听到你说话");
        Console.WriteLine("  --mic-volume, --mv <0-1>");
        Console.WriteLine("                         麦克风音量，默认 1.0（与音频文件音量分开调）");
        Console.WriteLine("  --file, -f <路径>      音频文件，可重复指定");
        Console.WriteLine("  --loopback, -l         混入系统正在播放的声音");
        Console.WriteLine("  --tone                 混入 440Hz 测试音");
        Console.WriteLine("  --volume, -v <0-1>     音频文件音量，默认 1.0");
        Console.WriteLine("  --latency <毫秒>       输出缓冲，默认 80");
        Console.WriteLine("  --seconds <秒>         运行指定时长后自动退出（便于验证）");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine("  vscard probe");
        Console.WriteLine("  vscard route --tone --seconds 5");
        Console.WriteLine("  vscard route -f \"song.mp3\"");
        Console.WriteLine("  vscard route -m \"麦克风阵列\" -f \"song.mp3\" -v 0.7    :: 边说话边放歌");
        Console.WriteLine("  vscard route -m \"麦克风阵列\"                          :: 只把麦克风转进线缆");
        Console.WriteLine("  vscard route -l -v 0.6 --to \"CABLE Input\"");
        return 0;
    }

    private static int ListDevices()
    {
        using var catalog = new AudioEndpointCatalog();

        Console.WriteLine("播放端点 (Render)");
        var renders = catalog.RenderNames;
        for (var i = 0; i < renders.Count; i++)
        {
            Console.WriteLine($"  {i + 1,2}. {renders[i]}");
        }

        Console.WriteLine();
        Console.WriteLine("录音端点 (Capture)");
        var captures = catalog.CaptureNames;
        for (var i = 0; i < captures.Count; i++)
        {
            Console.WriteLine($"  {i + 1,2}. {captures[i]}");
        }

        Console.WriteLine();
        var cables = catalog.DetectVirtualCables();

        if (cables.Count > 0)
        {
            Console.WriteLine("检测到疑似虚拟音频设备：");
            foreach (var cable in cables)
            {
                Console.WriteLine($"  * {cable}");
            }
        }
        else
        {
            Console.WriteLine("未检测到虚拟音频线缆（如 VB-CABLE）。");
            Console.WriteLine("  要与真实麦克风无关地把音频送给其他程序，请先安装：https://vb-audio.com/Cable/");
            Console.WriteLine($"  临时替代：让目标程序直接选用录音端点里的「立体声混音」（本机当前{(catalog.HasStereoMix() ? "可用" : "不可用")}）。");
        }

        return 0;
    }

    private static int Doctor()
    {
        using var catalog = new AudioEndpointCatalog();
        var cables = catalog.DetectVirtualCables();

        Console.WriteLine("环境自检");
        Console.WriteLine($"  运行时     : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  操作系统   : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  播放端点数 : {catalog.RenderNames.Count}");
        Console.WriteLine($"  录音端点数 : {catalog.CaptureNames.Count}");
        Console.WriteLine($"  虚拟线缆   : {(cables.Count > 0 ? "已安装 -> " + string.Join(" / ", cables) : "未安装")}");
        Console.WriteLine($"  立体声混音 : {(catalog.HasStereoMix() ? "可用" : "不可用")}");

        if (cables.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("结论：route 子命令需要虚拟线缆，才能把音频送进「麦克风」通道。");
            Console.WriteLine("      安装 VB-CABLE 后重启，再执行 vscard route --tone --seconds 5 验证。");
            return 0;
        }

        // 通话场景的关键：对方听到的是「录音端」，必须让通话软件选中它。
        var renderName = catalog.RankVirtualRenderCandidates().FirstOrDefault();
        var captureName = renderName is null ? null : catalog.FindPairedCapture(renderName);

        Console.WriteLine();
        Console.WriteLine("通话接入自检");
        Console.WriteLine($"  线缆播放端     : {renderName ?? "(未识别)"}");
        Console.WriteLine($"  线缆录音端     : {captureName ?? "(未识别)"}");

        if (captureName is not null && catalog.GetCaptureVolume(captureName) is { } volume)
        {
            var state = volume.Muted ? "已静音（对方会完全听不到）" : "未静音";
            Console.WriteLine($"  录音端音量     : {volume.Volume * 100:F0}% / {state}");
        }

        var defaultCapture = catalog.GetDefaultEndpointName(DataFlow.Capture, Role.Multimedia);
        var defaultCommCapture = catalog.GetDefaultEndpointName(DataFlow.Capture, Role.Communications);
        Console.WriteLine($"  默认输入设备   : {defaultCapture ?? "(未知)"}");
        Console.WriteLine($"  默认通信输入   : {defaultCommCapture ?? "(未知)"}");
        Console.WriteLine();

        var captureIsDefault = captureName is not null &&
            (string.Equals(defaultCapture, captureName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(defaultCommCapture, captureName, StringComparison.OrdinalIgnoreCase));

        if (captureIsDefault)
        {
            Console.WriteLine("结论：虚拟线缆已是默认输入设备，通话软件若用「系统默认设备」就能收到你送出的音频。");
        }
        else
        {
            Console.WriteLine("[!] 虚拟线缆还不是默认输入设备 —— 通话软件的「麦克风」多半仍指向真实麦克风，");
            Console.WriteLine("    所以对方听不到你送出的音频。二选一：");
            Console.WriteLine($"      A. 在通话软件的音频设置里，把「麦克风」手动选为：{captureName}");
            Console.WriteLine("      B. 设为系统默认：设置 → 系统 → 声音 → 输入 → 选 CABLE Output");
        }

        Console.WriteLine();
        Console.WriteLine("重要：通话软件的麦克风一旦换成线缆，你的真实声音就不会自动进去了。");
        Console.WriteLine("      要同时说话，必须把麦克风混进来：");
        Console.WriteLine("        vscard route --mic \"<麦克风名称>\"              :: 只转麦克风");
        Console.WriteLine("        vscard route --mic \"<麦克风名称>\" -f \"歌.mp3\"   :: 边说话边放音频");
        Console.WriteLine("      （名称为 vscard list 里的录音端点名，用片段即可）");
        Console.WriteLine();
        Console.WriteLine("若上面已选对但仍无声，依次排查：");
        Console.WriteLine("  1. 隐私：设置 → 隐私和安全性 → 麦克风 → 允许桌面应用访问麦克风（微信 / QQ 等需打开）");
        Console.WriteLine("  2. 级别：声音设置 → CABLE Output → 属性 → 级别，音量拉到 100、取消静音");
        Console.WriteLine("  3. 降噪：通话软件的「自动增益 / 降噪 / 回声消除」会把长时间音乐当噪声压掉，建议关闭");
        Console.WriteLine("  4. 独占：CABLE Output 属性 → 高级 → 取消「允许应用程序独占控制该设备」");
        Console.WriteLine("  5. 先跑 vscard probe，结论显示「链路连通」才说明线缆本身没问题");

        return 0;
    }

    private static int Route(string[] args)
    {
        string? targetHint = null;
        string? micHint = null;
        var files = new List<string>();
        var useLoopback = false;
        var useTone = false;
        var volume = 1.0f;
        var micVolume = 1.0f;
        var latency = 80;
        var seconds = 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--to":
                    targetHint = TakeValue(args, ref i);
                    break;

                case "--mic" or "-m":
                    micHint = TakeValue(args, ref i);
                    break;

                case "--mic-volume" or "--mv":
                    micVolume = float.Parse(TakeValue(args, ref i), CultureInfo.InvariantCulture);
                    break;

                case "--file" or "-f":
                    files.Add(TakeValue(args, ref i));
                    break;

                case "--loopback" or "-l":
                    useLoopback = true;
                    break;

                case "--tone":
                    useTone = true;
                    break;

                case "--volume" or "-v":
                    volume = float.Parse(TakeValue(args, ref i), CultureInfo.InvariantCulture);
                    break;

                case "--latency":
                    latency = int.Parse(TakeValue(args, ref i), CultureInfo.InvariantCulture);
                    break;

                case "--seconds":
                    seconds = int.Parse(TakeValue(args, ref i), CultureInfo.InvariantCulture);
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new ArgumentException($"未知选项：{args[i]}");
                    }

                    files.Add(args[i]);
                    break;
            }
        }

        if (files.Count == 0 && !useLoopback && !useTone && micHint is null)
        {
            useTone = true;
            Console.WriteLine("[提示] 未指定音源，改用 440Hz 测试音。");
            Console.WriteLine();
        }

        using var catalog = new AudioEndpointCatalog();

        // 未指定目标时，优先自动选用系统里最可能可用的虚拟音频设备。
        targetHint ??= catalog.RankVirtualRenderCandidates().FirstOrDefault() ?? DefaultTargetHint;

        using var target = catalog.ResolveRenderEndpoint(targetHint);
        using var session = new RouterSession(target, latency);

        Console.WriteLine($"目标端点 : {session.TargetName}");
        Console.WriteLine($"混音格式 : {session.MixFormat.SampleRate} Hz / {session.MixFormat.Channels} 声道 / 32-bit float");

        foreach (var file in files)
        {
            var source = SourceFactory.FromFile(file, volume);
            session.Add(source);
            Console.WriteLine($"音源     : 文件 {Path.GetFileName(file)}  时长 {source.TotalTime:hh\\:mm\\:ss}  源 {source.WaveFormat.SampleRate}Hz/{source.WaveFormat.Channels}ch");
        }

        if (useLoopback)
        {
            session.Add(SourceFactory.FromSystemOutput(volume));
            Console.WriteLine("音源     : 系统输出（WASAPI 回环）");
        }

        if (micHint is not null)
        {
            // MMDevice 的所有权转移给 CaptureSource，由会话负责释放。
            var microphone = SourceFactory.FromMicrophone(catalog.ResolveCaptureEndpoint(micHint), micVolume);
            session.Add(microphone);
            Console.WriteLine($"音源     : 麦克风 {microphone.DeviceName}  音量 {micVolume:F2}  采集 {microphone.InputFormatDescription}");
        }

        if (useTone)
        {
            session.Add(SourceFactory.Tone(session.MixFormat.SampleRate, 440d, 0.2f));
            Console.WriteLine("音源     : 440Hz 测试音");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {session.SourceCount} 路音源，开始路由。");

        session.Start();

        // 优先按驱动标识找配对的录音端；找不到再按名字替换推导。
        var captureSideName = catalog.FindPairedCapture(target.FriendlyName)
                              ?? DeriveCounterpartName(target.FriendlyName);

        if (captureSideName is not null)
        {
            Console.WriteLine($"通话 / 目标程序请把「麦克风」选为：{captureSideName}");
            Console.WriteLine("（若该程序只有「系统默认设备」，请把 Windows 默认输入设备也改成它；");
            Console.WriteLine("  执行 vscard doctor 可查看当前默认输入设备是否为线缆。）");
        }
        else
        {
            Console.WriteLine("目标程序请把本机对应的录音端点选为麦克风/输入设备：");

            foreach (var name in catalog.CaptureNames)
            {
                Console.WriteLine($"  - {name}");
            }
        }

        if (micHint is not null)
        {
            Console.WriteLine("已把麦克风混入线缆：对方会同时听到你的说话声与播放的音频。");
            Console.WriteLine("  建议戴耳机 —— 否则外放的声音会被麦克风拾取，形成回声甚至啸叫。");
        }
        else if (captureSideName is not null)
        {
            Console.WriteLine("注意：当前只送了音频，没有混入麦克风，对方听不到你说话。");
            Console.WriteLine("      若需要同时说话，加上 --mic \"<麦克风名>\" 重新运行（名字见 vscard list）。");
        }

        Console.WriteLine(seconds > 0 ? $"运行 {seconds} 秒后自动停止..." : "按 Ctrl+C 停止。");

        var clock = Stopwatch.StartNew();

        while (seconds <= 0 || clock.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(100);
        }

        session.Stop();
        Console.WriteLine("已停止。");
        return 0;
    }

    private static int Probe(string[] args)
    {
        string? renderHint = null;
        string? captureHint = null;
        string? file = null;
        var seconds = 3;
        var frequency = 440d;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--render" or "--to":
                    renderHint = TakeValue(args, ref i);
                    break;

                case "--capture" or "--from":
                    captureHint = TakeValue(args, ref i);
                    break;

                case "--file" or "-f":
                    file = TakeValue(args, ref i);
                    break;

                case "--seconds":
                    seconds = int.Parse(TakeValue(args, ref i), CultureInfo.InvariantCulture);
                    break;

                case "--frequency" or "--freq":
                    frequency = double.Parse(TakeValue(args, ref i), CultureInfo.InvariantCulture);
                    break;

                default:
                    throw new ArgumentException($"未知选项：{args[i]}");
            }
        }

        using var catalog = new AudioEndpointCatalog();

        renderHint ??= catalog.RankVirtualRenderCandidates().FirstOrDefault();

        if (renderHint is null)
        {
            Console.Error.WriteLine("[错误] 未找到虚拟音频设备。请先安装 VB-CABLE，或用 --render 指定播放端。");
            return 2;
        }

        captureHint ??= catalog.FindPairedCapture(renderHint) ?? GuessVirtualCapture(catalog);

        if (captureHint is null)
        {
            Console.Error.WriteLine("[错误] 无法自动识别虚拟录音端点，请用 --capture 显式指定。");
            return 2;
        }

        using var renderDevice = catalog.ResolveRenderEndpoint(renderHint);
        using var captureDevice = catalog.ResolveCaptureEndpoint(captureHint);

        using var renderMonitor = new LevelMeter(renderDevice, useLoopback: true);
        using var captureMonitor = new LevelMeter(captureDevice);
        using var session = new RouterSession(renderDevice, 60);

        string sourceDescription;

        if (file is null)
        {
            session.Add(SourceFactory.Tone(session.MixFormat.SampleRate, frequency, 0.3f));
            sourceDescription = $"{frequency:F0}Hz 测试音";
        }
        else
        {
            var fileSource = SourceFactory.FromFile(file, 0.6f);
            session.Add(fileSource);
            sourceDescription = $"{Path.GetFileName(file)}（{fileSource.WaveFormat.SampleRate} Hz / {fileSource.WaveFormat.Channels} 声道）";
        }

        Console.WriteLine("虚拟声卡端到端自检");
        Console.WriteLine($"  播放端   : {renderDevice.FriendlyName}");
        Console.WriteLine($"  录音端   : {captureDevice.FriendlyName}");
        Console.WriteLine($"  输出格式 : {session.MixFormat.SampleRate} Hz / {session.MixFormat.Channels} 声道 / 32-bit float");
        Console.WriteLine($"  音源     : {sourceDescription}");
        Console.WriteLine();

        // 先丢弃唤醒瞬态，再测基线。
        renderMonitor.Start();
        captureMonitor.Start();
        Thread.Sleep(SettleMilliseconds);
        renderMonitor.Reset();
        captureMonitor.Reset();

        // 基线窗口必须足够长，否则会把虚拟设备自发输出的固定信号误判成我们的音频。
        Thread.Sleep(BaselineMilliseconds);

        var renderBaselinePeak = renderMonitor.Peak;
        var renderBaselineRms = renderMonitor.Rms;
        var captureBaselinePeak = captureMonitor.Peak;
        var captureBaselineRms = captureMonitor.Rms;
        var captureBaselineBytes = captureMonitor.BytesCaptured;

        Console.WriteLine($"  基线（静默 {BaselineMilliseconds / 1000.0:F1}s）");
        Console.WriteLine($"    播放端回环 : 峰值 {renderBaselinePeak:F5}  RMS {renderBaselineRms:F5}");
        Console.WriteLine($"    录音端采集 : 峰值 {captureBaselinePeak:F5}  RMS {captureBaselineRms:F5}，{captureBaselineBytes} 字节");

        renderMonitor.Reset();
        captureMonitor.Reset();

        session.Start();
        Thread.Sleep(Math.Max(1, seconds) * 1000);
        session.Stop();

        var renderPeak = renderMonitor.Peak;
        var renderRms = renderMonitor.Rms;
        var capturePeak = captureMonitor.Peak;
        var captureRms = captureMonitor.Rms;

        // 停播后等待回落，再测一次噪声底。
        renderMonitor.Reset();
        captureMonitor.Reset();
        Thread.Sleep(QuiescenceMilliseconds);

        var renderAfterRms = renderMonitor.Rms;
        var captureAfterRms = captureMonitor.Rms;
        renderMonitor.Stop();
        captureMonitor.Stop();

        Console.WriteLine($"  播放 {seconds}s");
        Console.WriteLine($"    播放端回环 : 峰值 {renderPeak:F5}  RMS {renderRms:F5}");
        Console.WriteLine($"    录音端采集 : 峰值 {capturePeak:F5}  RMS {captureRms:F5}");
        Console.WriteLine($"  回落（静默 {QuiescenceMilliseconds / 1000.0:F1}s）");
        Console.WriteLine($"    播放端回环 : RMS {renderAfterRms:F5}");
        Console.WriteLine($"    录音端采集 : RMS {captureAfterRms:F5}");
        Console.WriteLine();

        if (captureBaselineBytes == 0)
        {
            Console.WriteLine("结论：录音端完全没有数据流，设备可能被独占或当前不可用。");
            return 3;
        }

        // 判定一律基于 RMS：单个采样点的爆音能把峰值抬得很高，却几乎不影响 RMS。
        // 噪声底取"播放前"与"回落后"两者的较小值，避免线缆回放残留抬高基线。
        var renderFloor = Math.Min(renderBaselineRms, renderAfterRms);
        var captureFloor = Math.Min(captureBaselineRms, captureAfterRms);

        var renderRose = renderRms > Math.Max(renderFloor * 2d, SilenceFloor);
        var captureRose = captureRms > Math.Max(captureFloor * 2d, SilenceFloor);

        if (!renderRose)
        {
            Console.WriteLine("结论：播放端没有检出音频，问题在音源或输出端点，不在虚拟线缆。");
            return 3;
        }

        if (captureRose)
        {
            Console.WriteLine("结论：链路连通 —— 播放端的音频确实被录音端收到了。");
            return 0;
        }

        Console.WriteLine("结论：播放端有音频，但录音端的电平相对基线没有抬升。");

        Console.WriteLine(captureBaselineRms > SilenceFloor
            ? $"      该设备静默时本身就输出约 {captureBaselineRms:F5}(RMS) 的固定信号，"
            : "      录音端有数据流，但电平没有被抬起来。");

        Console.WriteLine("      该设备并不把播放端的内容回环过来，因此不能当虚拟线缆用。");
        Console.WriteLine("      请改用 VB-CABLE 或 VoiceMeeter。");
        return 4;
    }

    /// <summary>
    /// 麦克风输入检测。把麦克风混进虚拟线缆之前，先用它确认麦克风本身有信号，
    /// 免得把"麦克风被静音 / 被隐私设置拦掉"误判成路由工具的问题。
    /// </summary>
    private static int MicCheck(string[] args)
    {
        string? deviceHint = null;
        var seconds = 5;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device" or "-d" or "--to":
                    deviceHint = TakeValue(args, ref i);
                    break;

                case "--seconds":
                    seconds = int.Parse(TakeValue(args, ref i), CultureInfo.InvariantCulture);
                    break;

                default:
                    throw new ArgumentException($"未知选项：{args[i]}");
            }
        }

        using var catalog = new AudioEndpointCatalog();

        deviceHint ??= catalog.GetDefaultEndpointName(DataFlow.Capture, Role.Communications)
                       ?? catalog.GetDefaultEndpointName(DataFlow.Capture, Role.Multimedia);

        if (deviceHint is null)
        {
            Console.Error.WriteLine("[错误] 找不到可用的录音端点。");
            return 2;
        }

        using var device = catalog.ResolveCaptureEndpoint(deviceHint);
        using var meter = new LevelMeter(device);

        Console.WriteLine("麦克风输入检测");
        Console.WriteLine($"  设备      : {device.FriendlyName}");
        Console.WriteLine($"  采集格式  : {meter.FormatDescription}");

        if (catalog.GetCaptureVolume(deviceHint) is { } volume)
        {
            Console.WriteLine($"  音量 / 静音: {volume.Volume * 100:F0}% / {(volume.Muted ? "已静音" : "未静音")}");
        }

        Console.WriteLine($"  时长      : {seconds} 秒 —— 请正常说话");
        Console.WriteLine();

        // 先丢弃唤醒瞬态，再开始计量。
        meter.Start();
        Thread.Sleep(SettleMilliseconds);
        meter.Reset();
        Thread.Sleep(Math.Max(1, seconds) * 1000);

        var peak = meter.Peak;
        var rms = meter.Rms;
        var bytes = meter.BytesCaptured;
        meter.Stop();

        Console.WriteLine($"  结果      : 峰值 {peak:F5}  RMS {rms:F5}  {bytes} 字节");
        Console.WriteLine();

        if (bytes == 0)
        {
            Console.WriteLine("结论：采集端没有任何数据流 —— 设备被独占，或系统禁止了麦克风访问。");
            return 3;
        }

        if (peak < 0.0005)
        {
            Console.WriteLine("结论：几乎收不到声音。若刚才你确实说话了，依次检查：");
            Console.WriteLine("  1. 音量：声音设置 → 该麦克风 → 属性 → 级别，把音量拉满（很多机器默认偏低）");
            Console.WriteLine("  2. 隐私：设置 → 隐私和安全性 → 麦克风 → 允许桌面应用访问麦克风");
            Console.WriteLine("  3. 硬件：麦克风物理静音开关、耳机麦克风是否插好");
            Console.WriteLine("  4. 独占：属性 → 高级 → 取消「允许应用程序独占控制该设备」");
            Console.WriteLine("若只是没说话，重跑一次并对着麦克风正常说话即可。");
            return 4;
        }

        Console.WriteLine("结论：麦克风有输入信号，可以混入虚拟线缆（route --mic \"<麦克风名>\"）。");
        return 0;
    }

    private static string? GuessVirtualCapture(AudioEndpointCatalog catalog)
    {
        string[] hints = ["Virtual", "虚拟", "CABLE"];

        return catalog.CaptureNames.FirstOrDefault(name =>
            hints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? DeriveCounterpartName(string renderName)
    {
        // VB-CABLE 的渲染端叫 "CABLE Input"，对应录音端 "CABLE Output"。
        return renderName.Contains("Input", StringComparison.OrdinalIgnoreCase)
            ? renderName.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase)
            : null;
    }

    private static string TakeValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"选项 {args[index]} 缺少取值。");
        }

        return args[++index];
    }
}
