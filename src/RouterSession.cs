using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VirtualSoundCard;

/// <summary>
/// 一次音频路由会话：把若干音源混成一路，渲染到指定的虚拟音频端点。
/// </summary>
public sealed class RouterSession : IDisposable
{
    private readonly MixingSampleProvider _mixer;
    private readonly WasapiOut _output;
    private readonly MMDevice _target;
    private readonly List<IDisposable> _owned = [];
    private readonly List<IStartableSource> _startables = [];
    private bool _started;
    private bool _disposed;

    public RouterSession(MMDevice target, int latencyMilliseconds = 80)
    {
        _target = target;

        // 用目标设备的采样率 + 立体声构造混音格式，尽量贴近设备原生格式。
        var native = target.AudioClient.MixFormat;
        var mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(native.SampleRate, 2);

        _mixer = new MixingSampleProvider(mixFormat) { ReadFully = true };

        _output = new WasapiOut(target, AudioClientShareMode.Shared, true, latencyMilliseconds);
        _output.Init(_mixer.ToWaveProvider());
    }

    public string TargetName => _target.FriendlyName;

    public WaveFormat MixFormat => _mixer.WaveFormat;

    public int SourceCount => _mixer.MixerInputs.Count();

    /// <summary>
    /// 加入一路音源。采样率与声道数会按混音格式自动对齐。
    /// </summary>
    public void Add(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is IDisposable disposable)
        {
            _owned.Add(disposable);
        }

        if (source is IStartableSource startable)
        {
            _startables.Add(startable);
        }

        _mixer.AddMixerInput(Normalize(source));
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // 先启动采集再开始渲染，保证第一帧就有音源可用。
        foreach (var startable in _startables)
        {
            startable.Start();
        }

        _output.Play();
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _output.Stop();

        foreach (var startable in _startables)
        {
            startable.Stop();
        }
    }

    private ISampleProvider Normalize(ISampleProvider source)
    {
        var target = _mixer.WaveFormat;

        if (source.WaveFormat.SampleRate != target.SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, target.SampleRate);
        }

        if (source.WaveFormat.Channels != target.Channels)
        {
            // 覆盖单声道、立体声以及 5.1 / 7.1 等多声道情形。
            // NAudio 内置的 MonoToStereo / StereoToMono 只支持 1↔2，
            // 源声道数大于 2 时会抛「Source must be stereo」。
            source = new ChannelMapSampleProvider(source, target.Channels);
        }

        return source;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _output.Stop();
        }
        catch
        {
            // 设备可能已被移除，忽略停止失败。
        }

        _output.Dispose();

        foreach (var item in _owned)
        {
            try
            {
                item.Dispose();
            }
            catch
            {
                // 单个音源释放失败不影响其余资源回收。
            }
        }

        _owned.Clear();
        _target.Dispose();
    }
}
