using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VirtualSoundCard;

/// <summary>
/// 需要显式启动/停止的音源（如麦克风采集）。由 <see cref="RouterSession"/> 在
/// 开始播放时统一启动、停止播放时统一停止，避免采集先行于播放产生额外延迟。
/// </summary>
public interface IStartableSource
{
    void Start();

    void Stop();
}

/// <summary>
/// 录音端点音源——把真实麦克风（或任意录音设备）的输入当作一路音源，
/// 与音频文件、系统回环混进虚拟线缆。这样对方既能听到你的说话声，也能听到你播放的音频。
/// <para>
/// 采集回调写入 <see cref="BufferedWaveProvider"/>，并以 <c>ReadFully</c> 方式读取：
/// 缓冲不足时补静音而不是返回 0，避免混音器因某一路暂时无数据而输出断续。
/// </para>
/// </summary>
public sealed class CaptureSource : ISampleProvider, IDisposable, IStartableSource
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volume;
    private bool _disposed;

    public CaptureSource(MMDevice device, float volume = 1f, int bufferMilliseconds = 200)
    {
        _device = device;
        DeviceName = device.FriendlyName;

        _capture = new WasapiCapture(device);
        InputFormat = _capture.WaveFormat;

        _buffer = new BufferedWaveProvider(InputFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(Math.Max(100, bufferMilliseconds)),
            DiscardOnBufferOverflow = true,

            // 关键：缓冲不足时补静音。混音器要求每一路都能持续产出样本，
            // 若某一路返回 0，输出会出现卡顿甚至停滞。
            ReadFully = true,
        };

        _capture.DataAvailable += OnDataAvailable;
        _volume = new VolumeSampleProvider(_buffer.ToSampleProvider()) { Volume = volume };
    }

    public string DeviceName { get; }

    public WaveFormat InputFormat { get; }

    public string InputFormatDescription =>
        $"{InputFormat.SampleRate} Hz / {InputFormat.Channels} 声道 / {InputFormat.BitsPerSample} bit / {InputFormat.Encoding}";

    public WaveFormat WaveFormat => _volume.WaveFormat;

    public int Read(float[] buffer, int offset, int count) => _volume.Read(buffer, offset, count);

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // 尚未开始采集时调用会抛异常，忽略。
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _capture.DataAvailable -= OnDataAvailable;
        Stop();
        _capture.Dispose();
        _device.Dispose();
    }
}
