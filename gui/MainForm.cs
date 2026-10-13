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
    private bool _dpiScaled;

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

        // 布局里的坐标与尺寸都是 96 DPI 基准的像素值，而 9pt 字体会随 DPI 自动放大。
        // WinForms 自带的自动缩放在这里不可靠：实测 144 DPI 下把 AutoScaleMode 设为
        // Dpi 并给出 AutoScaleDimensions 后，客户区仍停在 560x692（= 96 DPI 基准值），
        // 于是文字放大 1.5 倍、控件没放大，中文按钮被截断
        //（「添加文件…」只显示「添加文」、「静音音乐 (Ctrl+Alt+M)」只显示「静音音乐（」）。
        // 因此关掉自动缩放，改由 ApplyDpiScale() 在句柄创建时按实际 DPI 手工放大布局。
        AutoScaleMode = AutoScaleMode.None;

        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(560, 692);
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
        UpdateVolumeLabels();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ 界面

    private void BuildUi()
    {
        // 统一用「光标式」纵向排布，避免手写坐标彼此不匹配。
        // 所有尺寸均为 96 DPI 基准值，实际会由 AutoScaleMode.Dpi 等比放大。
        const int Pad = 18;         // 左右外边距
        const int CapH = 18;        // 说明文字行高
        const int RowH = 28;        // 输入控件行高
        const int SliderH = 32;
        const int GbH = 172;        // 「音频文件」分组高度
        const int SmallBtnW = 132;  // 小组件按钮宽度：按 150% 缩放下的中文宽度留足余量

        var inner = ClientSize.Width - Pad * 2;
        var y = 14;

        Label Caption(string text, int x, int top)
            => new()
            {
                Text = text,
                AutoSize = true,
                Location = new Point(x, top),
                ForeColor = SystemColors.GrayText,
            };

        // ---------------- 目标设备 ----------------
        Controls.Add(Caption("目标设备（虚拟声卡的播放端）", Pad, y));
        y += CapH + 2;

        _cmbTarget.SetBounds(Pad, y, inner - SmallBtnW - 8, RowH);
        _cmbTarget.DropDownStyle = ComboBoxStyle.DropDownList;
        Controls.Add(_cmbTarget);

        _btnRefresh.Text = "刷新";
        _btnRefresh.SetBounds(Pad + inner - SmallBtnW, y, SmallBtnW, RowH);
        Controls.Add(_btnRefresh);

        y += RowH + 12;

        // ---------------- 麦克风 ----------------
        Controls.Add(Caption("麦克风（想边说话边放音乐时选它，否则保持不使用）", Pad, y));
        y += CapH + 2;

        _cmbMic.SetBounds(Pad, y, inner, RowH);
        _cmbMic.DropDownStyle = ComboBoxStyle.DropDownList;
        Controls.Add(_cmbMic);

        y += RowH + 14;

        // ---------------- 音频文件 ----------------
        _grpFiles.Text = "音频文件（可直接把文件拖进窗口）";
        _grpFiles.SetBounds(Pad, y, inner, GbH);
        Controls.Add(_grpFiles);

        var gbInner = _grpFiles.Width - 24;

        _txtFile.SetBounds(12, 27, gbInner - SmallBtnW - 8, RowH);
        _txtFile.PlaceholderText = "输入路径，或点右侧按钮选择";
        _grpFiles.Controls.Add(_txtFile);

        _btnAdd.Text = "添加文件…";
        _btnAdd.SetBounds(12 + gbInner - SmallBtnW, 26, SmallBtnW, RowH + 2);
        _grpFiles.Controls.Add(_btnAdd);

        _lstFiles.SetBounds(12, 60, gbInner, 72);
        _lstFiles.SelectionMode = SelectionMode.One;
        _lstFiles.AllowDrop = true;
        _lstFiles.IntegralHeight = false;
        _grpFiles.Controls.Add(_lstFiles);

        _btnRemove.Text = "移除选中";
        _btnClear.Text = "全部清空";
        _btnClear.SetBounds(12 + gbInner - SmallBtnW, 138, SmallBtnW, RowH + 2);
        _btnRemove.SetBounds(12 + gbInner - SmallBtnW * 2 - 8, 138, SmallBtnW, RowH + 2);
        _grpFiles.Controls.Add(_btnRemove);
        _grpFiles.Controls.Add(_btnClear);

        y += GbH + 16;

        // ---------------- 音量 ----------------
        // 「静音音乐」放在音乐音量这一行的右侧、与标题同一行。
        // 原来它与滑块并排，宽度不够时复选框会被窗口边缘切掉。
        const int MuteW = 250;

        _lblMusic.AutoSize = true;
        _lblMusic.Location = new Point(Pad, y + 2);
        Controls.Add(_lblMusic);

        _chkMuteMusic.AutoSize = false;
        _chkMuteMusic.Text = "静音音乐 (Ctrl+Alt+M)";
        _chkMuteMusic.SetBounds(Pad + inner - MuteW, y, MuteW, CapH + 4);
        Controls.Add(_chkMuteMusic);

        y += CapH + 8;

        _tbMusic.SetBounds(Pad, y, inner, SliderH);
        _tbMusic.Minimum = 0;
        _tbMusic.Maximum = 100;
        _tbMusic.TickStyle = TickStyle.None;
        Controls.Add(_tbMusic);

        y += SliderH + 12;

        _lblMicVol.AutoSize = true;
        _lblMicVol.Location = new Point(Pad, y);
        Controls.Add(_lblMicVol);

        y += CapH + 4;

        _tbMicVol.SetBounds(Pad, y, inner, SliderH);
        _tbMicVol.Minimum = 0;
        _tbMicVol.Maximum = 100;
        _tbMicVol.TickStyle = TickStyle.None;
        Controls.Add(_tbMicVol);

        y += SliderH + 12;

        _lblLatency.AutoSize = true;
        _lblLatency.Location = new Point(Pad, y);
        Controls.Add(_lblLatency);

        y += CapH + 4;

        _tbLatency.SetBounds(Pad, y, inner, SliderH);
        _tbLatency.Minimum = 20;
        _tbLatency.Maximum = 300;
        _tbLatency.TickStyle = TickStyle.None;
        Controls.Add(_tbLatency);

        y += SliderH + 18;

        // ---------------- 控制按钮 ----------------
        var btnW = (inner - 16) / 2;

        _btnStart.Text = "开始路由";
        _btnStart.SetBounds(Pad, y, btnW, 38);
        Controls.Add(_btnStart);

        _btnStop.Text = "停止";
        _btnStop.SetBounds(Pad + btnW + 16, y, btnW, 38);
        _btnStop.Enabled = false;
        Controls.Add(_btnStop);

        y += 38 + 16;

        // ---------------- 状态与提示 ----------------
        _lblStatus.AutoSize = false;
        _lblStatus.AutoEllipsis = true;
        _lblStatus.SetBounds(Pad, y, inner, 20);
        Controls.Add(_lblStatus);

        y += 24;

        const int CopyW = 92;

        _lblHint.AutoSize = false;
        _lblHint.SetBounds(Pad, y, inner - CopyW - 10, 66);
        _lblHint.ForeColor = SystemColors.GrayText;
        Controls.Add(_lblHint);

        _btnCopy.Text = "复制";
        _btnCopy.SetBounds(Pad + inner - CopyW, y + 2, CopyW, RowH + 2);
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

    /// <summary>
    /// 按当前显示器的 DPI 等比放大整个布局。
    /// 布局坐标是 96 DPI 基准的像素值，而 9pt 字体已随 DPI 自动放大，
    /// 两者不匹配时中文会被截断（「添加文件…」只剩「添加文」）。
    /// 这里<b>只放大布局、不动字体</b>——字体以 pt 为单位，重复放大反而会溢出。
    /// </summary>
    private void ApplyDpiScale()
    {
        if (_dpiScaled)
        {
            return;
        }

        _dpiScaled = true;

        var scale = DeviceDpi / 96f;

        if (Math.Abs(scale - 1f) < 0.01f)
        {
            return;
        }

        SuspendLayout();
        ScaleDescendants(this, scale);
        ClientSize = new Size(
            (int)Math.Round(ClientSize.Width * scale),
            (int)Math.Round(ClientSize.Height * scale));
        ResumeLayout(true);
    }

    private static void ScaleDescendants(Control parent, float scale)
    {
        foreach (Control child in parent.Controls)
        {
            child.Location = new Point(
                (int)Math.Round(child.Location.X * scale),
                (int)Math.Round(child.Location.Y * scale));

            // 自动按文字定尺寸的控件（说明标签）不能强行改尺寸，否则会被拉伸变形。
            var sizeFollowsText = child.AutoSize && child is Label or CheckBox or RadioButton or LinkLabel;

            if (!sizeFollowsText)
            {
                child.Size = new Size(
                    (int)Math.Round(child.Width * scale),
                    (int)Math.Round(child.Height * scale));
            }

            ScaleDescendants(child, scale);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        ApplyDpiScale();

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

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);

        // 手工缩放后 WinForms 不会自己重算，窗体被拖到另一个缩放的显示器时按比例补一次。
        if (e.DeviceDpiOld <= 0)
        {
            return;
        }

        var ratio = e.DeviceDpiNew / (float)e.DeviceDpiOld;

        if (Math.Abs(ratio - 1f) < 0.01f)
        {
            return;
        }

        SuspendLayout();
        ScaleDescendants(this, ratio);
        ClientSize = new Size(
            (int)Math.Round(ClientSize.Width * ratio),
            (int)Math.Round(ClientSize.Height * ratio));
        ResumeLayout(true);
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
