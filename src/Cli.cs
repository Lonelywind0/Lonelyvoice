using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

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
        Console.WriteLine("  --to <名称片段>     目标渲染端点，默认自动选用系统中的虚拟音频设备");
        Console.WriteLine("  --file, -f <路径>   音频文件，可重复指定");
        Console.WriteLine("  --loopback, -l      混入系统正在播放的声音");
        Console.WriteLine("  --tone              混入 440Hz 测试音");
        Console.WriteLine("  --volume, -v <0-1>  音量，默认 1.0");
        Console.WriteLine("  --latency <毫秒>    输出缓冲，默认 80");
        Console.WriteLine("  --seconds <秒>      运行指定时长后自动退出（便于验证）");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine("  vscard probe");
        Console.WriteLine("  vscard route --tone --seconds 5");
        Console.WriteLine("  vscard route -f \"song.mp3\"");
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
        }

        return 0;
    }

    private static int Route(string[] args)
    {
        string? targetHint = null;
        var files = new List<string>();
        var useLoopback = false;
        var useTone = false;
        var volume = 1.0f;
        var latency = 80;
        var seconds = 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--to":
                    targetHint = TakeValue(args, ref i);
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

        if (files.Count == 0 && !useLoopback && !useTone)
        {
            useTone = true;
            Console.WriteLine("[提示] 未指定音源，改用 440Hz 测试音。");
            Console.WriteLine();
        }

        using var catalog = new AudioEndpointCatalog();

        // 未指定目标时，优先自动选用系统里已存在的虚拟音频设备。
        targetHint ??= catalog.DetectVirtualCables().FirstOrDefault() ?? DefaultTargetHint;

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

        if (useTone)
        {
            session.Add(SourceFactory.Tone(session.MixFormat.SampleRate, 440d, 0.2f));
            Console.WriteLine("音源     : 440Hz 测试音");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {session.SourceCount} 路音源，开始路由。");

        session.Start();

        // VB-CABLE 的渲染端叫 "CABLE Input"，配对的录音端是 "CABLE Output"。
        var captureSideName = DeriveCounterpartName(target.FriendlyName);

        if (captureSideName is not null)
        {
            Console.WriteLine($"目标程序请把「{captureSideName}」选为麦克风/输入设备。");
        }
        else
        {
            Console.WriteLine("目标程序请把本机对应的录音端点选为麦克风/输入设备：");

            foreach (var name in catalog.CaptureNames)
            {
                Console.WriteLine($"  - {name}");
            }
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

        renderHint ??= catalog.DetectVirtualCables().FirstOrDefault();

        if (renderHint is null)
        {
            Console.Error.WriteLine("[错误] 未找到虚拟音频设备。请先安装 VB-CABLE，或用 --render 指定播放端。");
            return 2;
        }

        captureHint ??= GuessVirtualCapture(catalog);

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

        // 基线窗口必须足够长，否则会把虚拟设备自发输出的固定信号误判成我们的音频。
        renderMonitor.Start();
        captureMonitor.Start();
        Thread.Sleep(BaselineMilliseconds);

        var renderBaseline = renderMonitor.Peak;
        var captureBaseline = captureMonitor.Peak;
        var captureBaselineBytes = captureMonitor.BytesCaptured;

        Console.WriteLine($"  基线（静默 {BaselineMilliseconds / 1000.0:F1}s）");
        Console.WriteLine($"    播放端回环 : 峰值 {renderBaseline:F5}");
        Console.WriteLine($"    录音端采集 : 峰值 {captureBaseline:F5}，{captureBaselineBytes} 字节");

        renderMonitor.Reset();
        captureMonitor.Reset();

        session.Start();
        Thread.Sleep(Math.Max(1, seconds) * 1000);
        session.Stop();

        var renderPeak = renderMonitor.Peak;
        var capturePeak = captureMonitor.Peak;
        renderMonitor.Stop();
        captureMonitor.Stop();

        Console.WriteLine($"  播放 {seconds}s");
        Console.WriteLine($"    播放端回环 : 峰值 {renderPeak:F5}");
        Console.WriteLine($"    录音端采集 : 峰值 {capturePeak:F5}");
        Console.WriteLine();

        if (captureBaselineBytes == 0)
        {
            Console.WriteLine("结论：录音端完全没有数据流，设备可能被独占或当前不可用。");
            return 3;
        }

        if (renderPeak <= Math.Max(renderBaseline * 3d, 0.005d))
        {
            Console.WriteLine("结论：播放端没有检出音频，问题在音源或输出端点，不在虚拟线缆。");
            return 3;
        }

        if (capturePeak > Math.Max(captureBaseline * 1.5d, 0.005d))
        {
            Console.WriteLine("结论：链路连通 —— 播放端的音频确实被录音端收到了。");
            return 0;
        }

        Console.WriteLine("结论：播放端有音频，但录音端电平相对它自身的基线没有抬升。");
        Console.WriteLine($"      该设备静默采集时就会输出约 {captureBaseline:F5} 的固定信号，");
        Console.WriteLine("      并不把播放端的内容回环过来，因此不能当虚拟线缆用。");
        Console.WriteLine("      请改用 VB-CABLE 或 VoiceMeeter。");
        return 4;
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
