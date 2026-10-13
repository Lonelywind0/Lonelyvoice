using NAudio.CoreAudioApi;

namespace VirtualSoundCard;

/// <summary>
/// 找不到目标音频端点时抛出，携带当前可用设备名以便给出可读提示。
/// </summary>
public sealed class AudioEndpointNotFoundException : Exception
{
    public AudioEndpointNotFoundException(string fragment, IReadOnlyList<string> available)
        : base($"找不到匹配「{fragment}」的音频设备。当前可用：{(available.Count == 0 ? "无" : string.Join(" / ", available))}")
    {
        Available = available;
    }

    public IReadOnlyList<string> Available { get; }
}

/// <summary>
/// 音频端点目录：负责枚举系统端点、按名称定位渲染端点、识别虚拟线缆。
/// </summary>
public sealed class AudioEndpointCatalog : IDisposable
{
    private static readonly string[] VirtualCableHints =
    [
        "CABLE Input",
        "CABLE Output",
        "VB-Audio",
        "VoiceMeeter",
        "Virtual Audio",
        "虚拟音频",
    ];

    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<string> RenderNames => NamesOf(DataFlow.Render);

    public IReadOnlyList<string> CaptureNames => NamesOf(DataFlow.Capture);

    private IReadOnlyList<string> NamesOf(DataFlow flow)
    {
        var collection = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var names = new List<string>(collection.Count);

        for (var i = 0; i < collection.Count; i++)
        {
            using var device = collection[i];
            names.Add(device.FriendlyName);
        }

        return names;
    }

    /// <summary>
    /// 按名称片段查找渲染端点（不区分大小写）。调用方负责释放返回的 MMDevice。
    /// </summary>
    public MMDevice ResolveRenderEndpoint(string nameFragment)
    {
        if (string.IsNullOrWhiteSpace(nameFragment))
        {
            throw new ArgumentException("设备名片段不能为空。", nameof(nameFragment));
        }

        return Resolve(DataFlow.Render, nameFragment);
    }

    /// <summary>
    /// 按名称片段查找录音端点（不区分大小写）。调用方负责释放返回的 MMDevice。
    /// </summary>
    public MMDevice ResolveCaptureEndpoint(string nameFragment)
    {
        if (string.IsNullOrWhiteSpace(nameFragment))
        {
            throw new ArgumentException("设备名片段不能为空。", nameof(nameFragment));
        }

        return Resolve(DataFlow.Capture, nameFragment);
    }

    private MMDevice Resolve(DataFlow flow, string nameFragment)
    {
        var collection = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var all = new List<MMDevice>(collection.Count);

        for (var i = 0; i < collection.Count; i++)
        {
            all.Add(collection[i]);
        }

        MMDevice? match = null;
        var names = new List<string>(all.Count);

        foreach (var device in all)
        {
            names.Add(device.FriendlyName);

            if (match is null &&
                device.FriendlyName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            {
                match = device;
            }
        }

        if (match is null)
        {
            foreach (var device in all)
            {
                device.Dispose();
            }

            throw new AudioEndpointNotFoundException(nameFragment, names);
        }

        foreach (var device in all)
        {
            if (!ReferenceEquals(device, match))
            {
                device.Dispose();
            }
        }

        return match;
    }

    /// <summary>
    /// 找出形似虚拟音频线缆的渲染端点。
    /// </summary>
    public IReadOnlyList<string> DetectVirtualCables()
    {
        return RenderNames
            .Where(name => VirtualCableHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// 在形似虚拟线缆的渲染端点中排序，把最可能真正可用的排在最前。
    /// 依据：① 是否存在配对录音端（说明它是一根完整的"线缆"而不是单向输出通道）；
    /// ② 是否为成熟的虚拟线缆驱动；③ 排除多声道辅助入口（如 "CABLE In 16 Ch"，
    /// 这类端点常被独占或打不开）；④ 已知不做回环的设备降权。
    /// </summary>
    public IReadOnlyList<string> RankVirtualRenderCandidates()
    {
        var captureNames = CaptureNames;

        return DetectVirtualCables()
            .OrderByDescending(name => ScoreVirtualRender(name, captureNames))
            .ToList();
    }

    private static int ScoreVirtualRender(string renderName, IReadOnlyList<string> captureNames)
    {
        var score = 0;
        var vendor = VendorTag(renderName);

        if (vendor.Length > 0 &&
            captureNames.Any(name => string.Equals(VendorTag(name), vendor, StringComparison.OrdinalIgnoreCase)))
        {
            score += 40;
        }

        if (renderName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            renderName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
            renderName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        // 多声道辅助入口不是标准线缆端点：实测 "CABLE In 16 Ch" 会返回
        // AUDCLNT_E_DEVICE_IN_USE(0x8889000A)，打开即失败。
        if (renderName.Contains("16 Ch", StringComparison.OrdinalIgnoreCase) ||
            renderName.Contains("16Ch", StringComparison.OrdinalIgnoreCase))
        {
            score -= 60;
        }

        // ToDesk Virtual Audio 采集时会自发输出固定电平，并不回环，实测不可用。
        if (renderName.Contains("ToDesk", StringComparison.OrdinalIgnoreCase))
        {
            score -= 40;
        }

        return score;
    }

    /// <summary>
    /// 取设备名末尾括号内的驱动标识：如 "扬声器 (VB-Audio Virtual Cable)" -> "VB-Audio Virtual Cable"。
    /// 用它判断渲染端与录音端是否属于同一条线缆。
    /// </summary>
    private static string VendorTag(string friendlyName)
    {
        var open = friendlyName.LastIndexOf('(');
        var close = friendlyName.LastIndexOf(')');

        return open >= 0 && close > open
            ? friendlyName[(open + 1)..close].Trim()
            : string.Empty;
    }

    /// <summary>
    /// 找出与指定渲染端配对（驱动标识相同）的录音端名称，找不到返回 null。
    /// </summary>
    public string? FindPairedCapture(string renderName)
    {
        var vendor = VendorTag(renderName);

        if (vendor.Length == 0)
        {
            return null;
        }

        return CaptureNames.FirstOrDefault(name =>
            string.Equals(VendorTag(name), vendor, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 取指定用途（默认 / 通信）下的默认端点名称，取不到返回 null。
    /// </summary>
    public string? GetDefaultEndpointName(DataFlow flow, Role role)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 取某录音端点的音量与静音状态，供通话场景排查（音量 0 或静音会让对方完全听不到）。
    /// </summary>
    public (float Volume, bool Muted)? GetCaptureVolume(string nameFragment)
    {
        try
        {
            using var device = Resolve(DataFlow.Capture, nameFragment);
            var volume = device.AudioEndpointVolume;
            return (volume.MasterVolumeLevelScalar, volume.Mute);
        }
        catch
        {
            return null;
        }
    }

    public bool HasStereoMix()
    {
        return CaptureNames.Any(name =>
            name.Contains("立体声混音", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("What U Hear", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _enumerator.Dispose();
}
