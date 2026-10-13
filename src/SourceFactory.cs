using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VirtualSoundCard;

/// <summary>
/// 构造可送进混音器的音源。所有音源都实现 ISampleProvider（32 位浮点）。
/// </summary>
public static class SourceFactory
{
    public static FileSource FromFile(string path, float volume)
        => new(path, volume);

    public static LoopbackSource FromSystemOutput(float volume)
        => new(volume);

    public static ISampleProvider Tone(int sampleRate, double frequencyHz, float gain)
        => new SignalGenerator(sampleRate, 2)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = frequencyHz,
            Gain = gain,
        };
}

/// <summary>
/// 文件音源。AudioFileReader 自身即 ISampleProvider，这里包一层以获得确定的释放语义。
/// </summary>
public sealed class FileSource : ISampleProvider, IDisposable
{
    private readonly AudioFileReader _reader;

    public FileSource(string path, float volume)
    {
        FullPath = Path.GetFullPath(path);

        if (!File.Exists(FullPath))
        {
            throw new FileNotFoundException($"找不到音频文件：{FullPath}", FullPath);
        }

        _reader = new AudioFileReader(FullPath) { Volume = volume };
    }

    public string FullPath { get; }

    public TimeSpan TotalTime => _reader.TotalTime;

    public WaveFormat WaveFormat => _reader.WaveFormat;

    public int Read(float[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);

    public void Dispose() => _reader.Dispose();
}

/// <summary>
/// 系统输出回环采集。抓取默认渲染设备的输出，不依赖「立体声混音」。
/// </summary>
public sealed class LoopbackSource : ISampleProvider, IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volume;

    public LoopbackSource(float volume)
    {
        _capture = new WasapiLoopbackCapture();

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };

        _capture.DataAvailable += OnDataAvailable;
        _volume = new VolumeSampleProvider(_buffer.ToSampleProvider()) { Volume = volume };
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
    }

    public WaveFormat WaveFormat => _volume.WaveFormat;

    public int Read(float[] buffer, int offset, int count) => _volume.Read(buffer, offset, count);

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;

        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // 采集尚未开始时调用会抛异常，忽略即可。
        }

        _capture.Dispose();
    }
}
