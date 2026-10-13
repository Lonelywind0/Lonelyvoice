using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VirtualSoundCard.Gui;

/// <summary>
/// 主窗口：把「麦克风 + 音频文件」混成一路送进虚拟线缆，并常驻系统托盘。
/// 音频核心完全复用 src/ 下已验证的实现，本文件只做界面与生命周期编排。
/// </summary>
internal sealed class MainForm : Form
{
    private const string NoMicLabel = "（不使用麦克风）";

    private const string SettingsFileName = "vscard-gui.json";

    private static readonly string[] AudioExtensions =
    [
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg",
        ".aif", ".aiff", ".mp4", ".wmv", ".mkv", ".webm",
    ];

    // 全局热键：Ctrl + Alt + M 一键静音音乐
    private const int WmHotKey = 0x0312;
    private const int HotKeyMuteMusic = 0xB01;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkM = 0x4D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>列表里的一项音频文件。</summary>
    private sealed record FileEntry(string Path, TimeSpan Duration);

    /// <summary>持久化到磁盘的界面设置。</summary>
    private sealed class GuiSettings
    {
        public string? Target { get; set; }

        public string? Mic { get; set; }

        public bool UseMicrophone { get; set; } = true;

        public int MusicVolume { get; set; } = 80;

        public int MicVolume { get; set; } = 100;

        public int Latency { get; set; } = 80;

        public List<string> Files { get; set; } = [];
    }

    private readonly AudioEndpointCatalog _catalog = new();
    private readonly List<FileEntry> _entries = [];

    // 会话存活期间持有的音源引用。释放由 RouterSession 统一负责（它会 Dispose 加入的音源），
    // 这里只用于运行时改音量与读取播放进度，因此停止时只需清空列表。
    private readonly List<AdjustableSource> _musicSources = [];
    private readonly List<FileSource> _activeFiles = [];
    private AdjustableSource? _micSource;

    private RouterSession? _session;
    private readonly Stopwatch _clock = new();
    private TimeSpan _longestTrack;

    private bool _musicMuted;
    private bool _suppressMuteCheck;
    private bool _exiting;
    private string _captureSideName = string.Empty;
    private string? _pendingTarget;
    private string? _pendingMic;
    private bool _pendingUseMic = true;
    private bool _hotKeyRegistered;

    // ---- 控件 ----
    private readonly ComboBox _cmbTarget = new();
    private readonly ComboBox _cmbMic = new();
    private readonly Button _btnRefresh = new();
    private readonly GroupBox _grpFiles = new();
    private readonly TextBox _txtFile = new();
    private readonly Button _btnAdd = new();
    private readonly ListBox _lstFiles = new();
    private readonly Button _btnRemove = new();
    private readonly Button _btnClear = new();
    private readonly Label _lblMusic = new();
    private readonly TrackBar _tbMusic = new();
    private readonly CheckBox _chkMuteMusic = new();
    private readonly Label _lblMicVol = new();
    private readonly TrackBar _tbMicVol = new();
    private readonly Label _lblLatency = new();
    private readonly TrackBar _tbLatency = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblHint = new();
    private readonly Button _btnCopy = new();

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 500 };
    private readonly NotifyIcon _tray = new();

    public MainForm()
    {
        Text = "Lonelyvoice 虚拟声卡";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(540, 700);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AllowDrop = true;

        BuildUi();
        WireEvents();
        BuildTray();

        LoadSettings();
        RefreshDevices();
        ApplyPendingDeviceSelection();
        UpdateCaptureHint();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ 界面

    private void BuildUi()
    {
        Label Caption(string text, int x, int y, int w = 500)
            => new() { Text = text, Location = new Point(x, y), Size = new Size(w, 18), ForeColor = SystemColors.GrayText };

        Controls.Add(Caption("目标设备（虚拟声卡的播放端）", 20, 16));

        _cmbTarget.Location = new Point(20, 38);
        _cmbTarget.Size = new Size(420, 25);
        _cmbTarget.DropDownStyle = ComboBoxStyle.DropDownList;
        Controls.Add(_cmbTarget);

        _btnRefresh.Text = "刷新";
        _btnRefresh.Location = new Point(450, 37);
        _btnRefresh.Size = new Size(70, 26);
        Controls.Add(_btnRefresh);

        Controls.Add(Caption("麦克风（想边说话边放音乐时选它，否则保持不使用）", 20, 78));

        _cmbMic.Location = new Point(20, 100);
        _cmbMic.Size = new Size(500, 25);
        _cmbMic.DropDownStyle = ComboBoxStyle.DropDownList;
        Controls.Add(_cmbMic);

        _grpFiles.Text = "音频文件（可直接把文件拖进窗口）";
        _grpFiles.Location = new Point(20, 138);
        _grpFiles.Size = new Size(500, 186);
        Controls.Add(_grpFiles);

        _txtFile.Location = new Point(12, 30);
        _txtFile.Size = new Size(360, 25);
        _txtFile.PlaceholderText = "输入路径，或点右侧按钮选择";
        _grpFiles.Controls.Add(_txtFile);

        _btnAdd.Text = "添加文件…";
        _btnAdd.Location = new Point(380, 29);
        _btnAdd.Size = new Size(100, 26);
        _grpFiles.Controls.Add(_btnAdd);

        _lstFiles.Location = new Point(12, 62);
        _lstFiles.Size = new Size(470, 82);
        _lstFiles.SelectionMode = SelectionMode.One;
        _lstFiles.AllowDrop = true;
        _lstFiles.IntegralHeight = false;
        _grpFiles.Controls.Add(_lstFiles);

        _btnRemove.Text = "移除选中";
        _btnRemove.Location = new Point(290, 150);
        _btnRemove.Size = new Size(88, 26);
        _grpFiles.Controls.Add(_btnRemove);

        _btnClear.Text = "全部清空";
        _btnClear.Location = new Point(386, 150);
        _btnClear.Size = new Size(96, 26);
        _grpFiles.Controls.Add(_btnClear);

        _lblMusic.Location = new Point(20, 334);
        _lblMusic.Size = new Size(300, 18);
        Controls.Add(_lblMusic);

        _tbMusic.Location = new Point(20, 354);
        _tbMusic.Size = new Size(370, 32);
        _tbMusic.Minimum = 0;
        _tbMusic.Maximum = 100;
        _tbMusic.TickStyle = TickStyle.None;
        Controls.Add(_tbMusic);

        _chkMuteMusic.Text = "静音音乐 (Ctrl+Alt+M)";
        _chkMuteMusic.Location = new Point(398, 358);
        _chkMuteMusic.Size = new Size(140, 24);
        Controls.Add(_chkMuteMusic);

        _lblMicVol.Location = new Point(20, 392);
        _lblMicVol.Size = new Size(300, 18);
        Controls.Add(_lblMicVol);

        _tbMicVol.Location = new Point(20, 412);
        _tbMicVol.Size = new Size(500, 32);
        _tbMicVol.Minimum = 0;
        _tbMicVol.Maximum = 100;
        _tbMicVol.TickStyle = TickStyle.None;
        Controls.Add(_tbMicVol);

        _lblLatency.Location = new Point(20, 450);
        _lblLatency.Size = new Size(360, 18);
        Controls.Add(_lblLatency);

        _tbLatency.Location = new Point(20, 470);
        _tbLatency.Size = new Size(500, 32);
        _tbLatency.Minimum = 20;
        _tbLatency.Maximum = 300;
        _tbLatency.TickStyle = TickStyle.None;
        Controls.Add(_tbLatency);

        _btnStart.Text = "开始路由";
        _btnStart.Location = new Point(20, 520);
        _btnStart.Size = new Size(240, 42);
        Controls.Add(_btnStart);

        _btnStop.Text = "停止";
        _btnStop.Location = new Point(280, 520);
        _btnStop.Size = new Size(240, 42);
        _btnStop.Enabled = false;
        Controls.Add(_btnStop);

        _lblStatus.Location = new Point(20, 578);
        _lblStatus.Size = new Size(500, 20);
        Controls.Add(_lblStatus);

        _lblHint.Location = new Point(20, 604);
        _lblHint.Size = new Size(420, 62);
        _lblHint.ForeColor = SystemColors.GrayText;
        Controls.Add(_lblHint);

        _btnCopy.Text = "复制";
        _btnCopy.Location = new Point(450, 608);
        _btnCopy.Size = new Size(70, 28);
        _btnCopy.Enabled = false;
        Controls.Add(_btnCopy);
    }

    private void WireEvents()
    {
        _btnRefresh.Click += (_, _) =>
        {
            RefreshDevices();
            UpdateCaptureHint();
        };

        _cmbTarget.SelectedIndexChanged += (_, _) => UpdateCaptureHint();

        _btnAdd.Click += (_, _) => AddFromDialog();
        _btnRemove.Click += (_, _) => RemoveSelected();
        _btnClear.Click += (_, _) =>
        {
            _entries.Clear();
            RefreshFileList();
        };

        _txtFile.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddFiles([_txtFile.Text]);
            }
        };

        _lstFiles.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
            }
        };

        _tbMusic.ValueChanged += (_, _) =>
        {
            // 拖推子即视为想听声音，自动解除静音。
            if (_musicMuted)
            {
                SetMusicMuted(false, announce: false);
            }

            ApplyMusicVolume();
        };

        _tbMicVol.ValueChanged += (_, _) =>
        {
            if (_micSource is not null)
            {
                _micSource.Volume = _tbMicVol.Value / 100f;
            }

            UpdateVolumeLabels();
        };

        _tbLatency.ValueChanged += (_, _) => UpdateVolumeLabels();

        _chkMuteMusic.CheckedChanged += (_, _) =>
        {
            if (!_suppressMuteCheck)
            {
                SetMusicMuted(_chkMuteMusic.Checked, announce: false);
            }
        };

        _btnStart.Click += (_, _) => StartRouting();
        _btnStop.Click += (_, _) => StopRouting();
        _btnCopy.Click += (_, _) => CopyCaptureName();

        _uiTimer.Tick += (_, _) => UpdateStatus();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _lstFiles.DragEnter += OnDragEnter;
        _lstFiles.DragDrop += OnDragDrop;
    }

    // ------------------------------------------------------------ 文件列表

    private void AddFromDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择音频文件",
            Multiselect = true,
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.ogg;*.aif;*.aiff;*.mp4;*.wmv;*.mkv;*.webm|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var failed = new List<string>();

        foreach (var raw in paths)
        {
            var path = raw.Trim().Trim('"');

            if (path.Length == 0)
            {
                continue;
            }

            // 支持一次粘贴多个用分号 / 竖线分隔的路径。
            if (path.Contains(';') || path.Contains('|'))
            {
                AddFiles(path.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            if (!File.Exists(path))
            {
                failed.Add($"{path}（文件不存在）");
                continue;
            }

            var full = Path.GetFullPath(path);

            if (_entries.Any(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // 读一次时长用于显示进度；读不出来也不影响添加（可能是容器格式，交给解码器）。
            var duration = TimeSpan.Zero;

            try
            {
                using var probe = new AudioFileReader(full);
                duration = probe.TotalTime;
            }
            catch
            {
                // 忽略，时长显示为 "--:--"。
            }

            _entries.Add(new FileEntry(full, duration));
            added++;
        }

        if (added > 0)
        {
            _txtFile.Clear();
            RefreshFileList();
        }

        if (failed.Count > 0)
        {
            MessageBox.Show(
                this,
                "以下文件未能添加：\r\n\r\n" + string.Join("\r\n", failed),
                "Lonelyvoice",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        else if (added == 0 && _entries.Count == 0)
        {
            MessageBox.Show(this, "没有添加任何文件。", "Lonelyvoice", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void RemoveSelected()
    {
        var index = _lstFiles.SelectedIndex;

        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        _entries.RemoveAt(index);
        RefreshFileList();
    }

    private void RefreshFileList()
    {
        var selected = _lstFiles.SelectedIndex;

        _lstFiles.BeginUpdate();
        _lstFiles.Items.Clear();

        foreach (var entry in _entries)
        {
            var length = entry.Duration > TimeSpan.Zero
                ? $"{entry.Duration:mm\\:ss}"
                : "--:--";

            _lstFiles.Items.Add($"{Path.GetFileName(entry.Path)}   [{length}]");
        }

        _lstFiles.EndUpdate();

        if (selected >= 0 && selected < _lstFiles.Items.Count)
        {
            _lstFiles.SelectedIndex = selected;
        }

        UpdateStatus();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddFiles(paths);
        }
    }

    // -------------------------------------------------------------- 设备

    private void RefreshDevices()
    {
        var previousTarget = _cmbTarget.SelectedItem as string;
        var previousMic = _cmbMic.SelectedItem as string;

        _cmbTarget.BeginUpdate();
        _cmbTarget.Items.Clear();

        // 排过序的候选：成熟线缆优先，避开打不开的 CABLE In 16 Ch 与不环回的 ToDesk。
        foreach (var name in _catalog.RankVirtualRenderCandidates())
        {
            _cmbTarget.Items.Add(name);
        }

        // 兜底：把其余渲染端点也列出来，方便接耳机或真实扬声器。
        foreach (var name in _catalog.RenderNames)
        {
            if (!_cmbTarget.Items.Contains(name))
            {
                _cmbTarget.Items.Add(name);
            }
        }

        _cmbTarget.EndUpdate();

        _cmbMic.BeginUpdate();
        _cmbMic.Items.Clear();
        _cmbMic.Items.Add(NoMicLabel);

        var microphoneGuess = -1;

        foreach (var name in _catalog.CaptureNames)
        {
            // 线缆的录音端不是麦克风来源，排除掉，否则「把自己的输出再喂回去」会成环。
            if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _cmbMic.Items.Add(name);

            if (microphoneGuess < 0 && name.Contains("麦克风阵列", StringComparison.OrdinalIgnoreCase))
            {
                microphoneGuess = _cmbMic.Items.Count - 1;
            }
        }

        if (microphoneGuess < 0 && _cmbMic.Items.Count > 1)
        {
            microphoneGuess = 1;
        }

        _cmbMic.EndUpdate();

        RestoreSelection(_cmbTarget, previousTarget);
        RestoreSelection(_cmbMic, previousMic, microphoneGuess);
    }

    private static void RestoreSelection(ComboBox combo, string? wanted, int fallbackIndex = 0)
    {
        if (wanted is not null)
        {
            var index = combo.Items.IndexOf(wanted);

            if (index >= 0)
            {
                combo.SelectedIndex = index;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = Math.Clamp(fallbackIndex, 0, combo.Items.Count - 1);
        }
    }

    private void UpdateCaptureHint()
    {
        var target = _cmbTarget.SelectedItem as string;
        _captureSideName = target is null ? string.Empty : _catalog.FindPairedCapture(target) ?? string.Empty;

        if (_captureSideName.Length == 0)
        {
            _lblHint.Text = "提示：通话软件里把「麦克风」选为虚拟线缆的录音端（形如 CABLE Output）。";
            _btnCopy.Enabled = false;
        }
        else
        {
            _lblHint.Text = $"通话软件里把「麦克风」选为：\r\n{_captureSideName}\r\n（若该程序只有「系统默认设备」，请把 Windows 默认输入设备也改成它）";
            _btnCopy.Enabled = true;
        }
    }

    private void CopyCaptureName()
    {
        if (_captureSideName.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_captureSideName);
            _lblHint.Text = $"已复制：{_captureSideName}";
        }
        catch
        {
            // 剪贴板被其他进程占用时会失败，忽略。
        }
    }

    // -------------------------------------------------------------- 路由

    private void StartRouting()
    {
        if (_session is not null)
        {
            return;
        }

        var targetName = _cmbTarget.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(targetName))
        {
            Warn("请先选择目标设备。");
            return;
        }

        var micName = _cmbMic.SelectedIndex > 0 ? _cmbMic.SelectedItem as string : null;
        var hasMusic = _entries.Count > 0;

        if (!hasMusic && micName is null)
        {
            Warn("至少需要一路音源：添加音频文件，或选择一个麦克风。");
            return;
        }

        MMDevice? target = null;

        try
        {
            target = _catalog.ResolveRenderEndpoint(targetName);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
            return;
        }

        RouterSession session;

        try
        {
            session = new RouterSession(target, _tbLatency.Value);
        }
        catch (Exception ex)
        {
            target.Dispose();
            Warn($"无法打开目标设备「{targetName}」：\r\n{ex.Message}\r\n\r\n若提示设备被占用，请关闭可能独占它的程序后重试。");
            return;
        }

        var musicVolume = _musicMuted ? 0f : _tbMusic.Value / 100f;

        try
        {
            _musicSources.Clear();
            _activeFiles.Clear();

            foreach (var entry in _entries)
            {
                var file = SourceFactory.FromFile(entry.Path, 1f);
                _activeFiles.Add(file);

                var adjustable = new AdjustableSource(file, musicVolume);
                _musicSources.Add(adjustable);
                session.Add(adjustable);
            }

            if (micName is not null)
            {
                var micDevice = _catalog.ResolveCaptureEndpoint(micName);

                // MMDevice 的所有权转移给 CaptureSource，由它在 Dispose 时释放。
                var capture = SourceFactory.FromMicrophone(micDevice, 1f);
                _micSource = new AdjustableSource(capture, _tbMicVol.Value / 100f);
                session.Add(_micSource);
            }

            _longestTrack = _entries.Count == 0 ? TimeSpan.Zero : _entries.Max(e => e.Duration);

            session.Start();
        }
        catch (Exception ex)
        {
            session.Dispose();
            _session = null;
            _musicSources.Clear();
            _activeFiles.Clear();
            _micSource = null;
            Warn($"启动失败：\r\n{ex.Message}");
            return;
        }

        _session = session;
        _clock.Restart();
        _uiTimer.Start();

        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _cmbTarget.Enabled = false;
        _cmbMic.Enabled = false;
        _grpFiles.Enabled = false;

        UpdateStatus();

        _tray.ShowBalloonTip(2000, "Lonelyvoice", $"开始路由到「{targetName}」", ToolTipIcon.Info);
    }

    private void StopRouting()
    {
        _uiTimer.Stop();
        _clock.Stop();

        // RouterSession.Dispose 会依次释放加入的音源，因此这里只清引用、不重复释放。
        if (_session is not null)
        {
            try
            {
                _session.Stop();
            }
            catch
            {
                // 设备可能已被移除。
            }

            _session.Dispose();
            _session = null;
        }

        _musicSources.Clear();
        _activeFiles.Clear();
        _micSource = null;

        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _cmbTarget.Enabled = true;
        _cmbMic.Enabled = true;
        _grpFiles.Enabled = true;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_session is null)
        {
            var count = _entries.Count;
            _lblStatus.Text = count == 0
                ? "已停止 · 尚未选择音源"
                : $"已停止 · 待播放 {count} 个文件";
            return;
        }

        var elapsed = _clock.Elapsed;
        var parts = new List<string>
        {
            $"正在路由 · 已运行 {elapsed:hh\\:mm\\:ss}",
            $"{_session.SourceCount} 路音源",
            $"{_session.MixFormat.SampleRate} Hz / {_session.MixFormat.Channels} 声道",
        };

        if (_activeFiles.Count > 0)
        {
            try
            {
                var position = _activeFiles.Max(f => f.CurrentTime);
                var total = _longestTrack > TimeSpan.Zero ? _longestTrack : position;
                parts.Add($"音乐 {position:mm\\:ss} / {total:mm\\:ss}");
            }
            catch
            {
                // 个别文件可能已到结尾或被释放，忽略。
            }
        }

        _lblStatus.Text = string.Join(" · ", parts);
    }

    // -------------------------------------------------------------- 音量

    private void ApplyMusicVolume()
    {
        var volume = _musicMuted ? 0f : _tbMusic.Value / 100f;

        foreach (var source in _musicSources)
        {
            source.Volume = volume;
        }

        UpdateVolumeLabels();
    }

    private void SetMusicMuted(bool muted, bool announce)
    {
        if (_musicMuted == muted && !announce)
        {
            return;
        }

        _musicMuted = muted;

        _suppressMuteCheck = true;
        _chkMuteMusic.Checked = muted;
        _suppressMuteCheck = false;

        ApplyMusicVolume();

        if (announce)
        {
            _tray.ShowBalloonTip(
                1500,
                "Lonelyvoice",
                muted ? "音乐已静音（只留你的麦克风）" : "音乐已恢复",
                ToolTipIcon.None);
        }
    }

    private void UpdateVolumeLabels()
    {
        _lblMusic.Text = _musicMuted
            ? "音乐音量  ▸  已静音"
            : $"音乐音量  ▸  {_tbMusic.Value}%";

        _lblMicVol.Text = _micSource is null
            ? $"麦克风音量  ▸  {_tbMicVol.Value}%（开始后生效）"
            : $"麦克风音量  ▸  {_tbMicVol.Value}%";

        _lblLatency.Text = $"输出延迟  ▸  {_tbLatency.Value} ms（开始后生效）";
    }

    // -------------------------------------------------------------- 托盘

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("显示窗口", null, (_, _) => RestoreWindow());
        menu.Items.Add("开始路由", null, (_, _) => StartRouting());
        menu.Items.Add("停止路由", null, (_, _) => StopRouting());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("静音 / 恢复音乐", null, (_, _) => SetMusicMuted(!_musicMuted, announce: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _exiting = true;
            Close();
        });

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "Lonelyvoice 虚拟声卡";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => RestoreWindow();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // -------------------------------------------------------- 生命周期

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 句柄可能被重建，重复注册会失败并误报「被占用」。
        if (_hotKeyRegistered)
        {
            return;
        }

        if (RegisterHotKey(Handle, HotKeyMuteMusic, ModControl | ModAlt | ModNoRepeat, VkM))
        {
            _hotKeyRegistered = true;
        }
        else
        {
            // 热键被别的程序占用时不阻断使用，只是没有快捷键。
            _chkMuteMusic.Text = "静音音乐（快捷键被占用）";
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey && m.WParam.ToInt32() == HotKeyMuteMusic)
        {
            SetMusicMuted(!_musicMuted, announce: true);
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点右上角关闭只收进托盘，路由继续跑；真正退出走托盘菜单。
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2000, "Lonelyvoice", "已最小化到托盘，路由继续运行。", ToolTipIcon.Info);
            return;
        }

        SaveSettings();

        _uiTimer.Stop();
        StopRouting();

        if (_hotKeyRegistered)
        {
            UnregisterHotKey(Handle, HotKeyMuteMusic);
            _hotKeyRegistered = false;
        }
        _tray.Visible = false;
        _tray.Dispose();
        _catalog.Dispose();

        base.OnFormClosing(e);
    }

    // -------------------------------------------------------- 设置持久化

    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                _tbMusic.Value = 80;
                _tbMicVol.Value = 100;
                _tbLatency.Value = 80;
                UpdateVolumeLabels();
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<GuiSettings>(json) ?? new GuiSettings();

            _tbMusic.Value = Math.Clamp(settings.MusicVolume, 0, 100);
            _tbMicVol.Value = Math.Clamp(settings.MicVolume, 0, 100);
            _tbLatency.Value = Math.Clamp(settings.Latency, _tbLatency.Minimum, _tbLatency.Maximum);

            _pendingTarget = settings.Target;
            _pendingMic = settings.Mic;
            _pendingUseMic = settings.UseMicrophone;

            foreach (var path in settings.Files ?? [])
            {
                if (File.Exists(path) &&
                    !_entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    var duration = TimeSpan.Zero;

                    try
                    {
                        using var probe = new AudioFileReader(path);
                        duration = probe.TotalTime;
                    }
                    catch
                    {
                        // 忽略。
                    }

                    _entries.Add(new FileEntry(path, duration));
                }
            }

            RefreshFileList();
            UpdateVolumeLabels();
        }
        catch
        {
            // 设置文件损坏时用默认值，不影响启动。
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new GuiSettings
            {
                Target = _cmbTarget.SelectedItem as string,
                Mic = _cmbMic.SelectedIndex > 0 ? _cmbMic.SelectedItem as string : null,
                UseMicrophone = _cmbMic.SelectedIndex > 0,
                MusicVolume = _tbMusic.Value,
                MicVolume = _tbMicVol.Value,
                Latency = _tbLatency.Value,
                Files = _entries.Select(e => e.Path).ToList(),
            };

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 写不进去（目录只读等）就算了，不打扰用户。
        }
    }

    /// <summary>启动后把设置里记住的设备选回去。RefreshDevices 之后调用。</summary>
    public void ApplyPendingDeviceSelection()
    {
        if (_pendingTarget is not null)
        {
            var index = _cmbTarget.Items.IndexOf(_pendingTarget);

            if (index >= 0)
            {
                _cmbTarget.SelectedIndex = index;
            }
        }

        if (!_pendingUseMic)
        {
            _cmbMic.SelectedIndex = 0;
        }
        else if (_pendingMic is not null)
        {
            var index = _cmbMic.Items.IndexOf(_pendingMic);

            if (index >= 0)
            {
                _cmbMic.SelectedIndex = index;
            }
        }

        UpdateCaptureHint();
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Lonelyvoice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
