using System.Text;

namespace VirtualSoundCard.Gui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 界面工程是 WinExe，没有控制台可输出，因此自检结果写到文件里。
        // 自检必须在单实例检查之前，否则界面已开时无法运行。
        if (args.Length > 0 && args[0] is "--smoke" or "-s")
        {
            return RunSmoke(args.Length > 1 ? args[1] : null);
        }

        // 单实例：两个实例会抢同一块虚拟线缆，且托盘里会出现两个图标。
        using var mutex = new Mutex(true, "Lonelyvoice.VirtualSoundCard.Gui", out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "虚拟声卡界面已经在运行了，请查看系统托盘。",
                "Lonelyvoice",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// 无界面自检：验证界面层新增的 <see cref="AdjustableSource"/> 与 RouterSession 的整合，
    /// 也就是「包装后的音源能否被会话正常启停、运行时改音量是否生效」。
    /// 音频核心本身已在 CLI 侧验证过，这里只覆盖界面新引入的这一层。
    /// </summary>
    private static int RunSmoke(string? explicitTarget)
    {
        var log = new StringBuilder();
        var code = 0;

        void Line(string text) => log.AppendLine(text);

        Line($"时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        try
        {
            using var catalog = new AudioEndpointCatalog();

            var targetName = explicitTarget ?? catalog.RankVirtualRenderCandidates().FirstOrDefault();

            if (targetName is null)
            {
                Line("结果     : FAIL —— 未找到可用虚拟线缆");
                return Flush(log, 2);
            }

            Line($"目标端点 : {targetName}");

            using var session = new RouterSession(catalog.ResolveRenderEndpoint(targetName), 80);
            Line($"会话格式 : {session.MixFormat.SampleRate} Hz / {session.MixFormat.Channels} 声道");

            // 1) 用测试音验证包装层能正常启停。
            var tone = new AdjustableSource(SourceFactory.Tone(session.MixFormat.SampleRate, 440d, 0.3f));
            session.Add(tone);

            var added = 1;

            // 2) 若存在多声道测试文件，一并用上——它会走 ChannelMapSampleProvider 降混，
            //    是最容易出问题的一条路径。
            //    从 gui/bin/Release/net8.0-windows 上溯四级即项目根目录。
            var probe = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", ".cache", "test-51.wav"));

            if (File.Exists(probe))
            {
                session.Add(new AdjustableSource(SourceFactory.FromFile(probe, 1f), 0.5f));
                Line($"已加入   : {Path.GetFileName(probe)}（多声道降混路径）");
                added++;
            }

            Line($"音源路数 : {session.SourceCount}（预期 {added}）");

            session.Start();
            Thread.Sleep(500);

            // 运行时改音量：这是界面推子的核心动作，必须不抛异常且不中断音频。
            tone.Volume = 0.5f;
            Thread.Sleep(500);

            tone.Volume = 0f;
            Thread.Sleep(300);

            session.Stop();
            session.Dispose();

            Line("结果     : PASS —— 包装后的音源可正常启停，运行时调音量未抛异常");
            code = 0;
        }
        catch (Exception ex)
        {
            Line($"结果     : FAIL —— {ex.GetType().Name}: {ex.Message}");
            Line($"堆栈     : {ex.StackTrace}");
            code = 1;
        }

        return Flush(log, code);
    }

    private static int Flush(StringBuilder log, int code)
    {
        var text = log.ToString();
        Console.WriteLine(text);

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "smoke.log");
            File.WriteAllText(path, text);
        }
        catch
        {
            // 写不进去就算了，退出码仍然可用。
        }

        return code;
    }
}
