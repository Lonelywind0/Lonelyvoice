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

    public bool HasStereoMix()
    {
        return CaptureNames.Any(name =>
            name.Contains("立体声混音", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("What U Hear", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _enumerator.Dispose();
}
