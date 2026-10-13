using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VirtualSoundCard;

/// <summary>
/// 采集指定录音端点并累计电平统计，用于判断虚拟声卡的播放端与录音端是否真的连通。
/// </summary>
public sealed class LevelMeter : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly object _gate = new();

    private double _sumOfSquares;
    private long _sampleCount;
    private float _peak;
    private long _bytes;

    public LevelMeter(MMDevice device, bool useLoopback = false)
    {
        _capture = useLoopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        _capture.DataAvailable += OnDataAvailable;
    }

    public string FormatDescription =>
        $"{_capture.WaveFormat.SampleRate} Hz / {_capture.WaveFormat.Channels} 声道 / {_capture.WaveFormat.BitsPerSample} bit / {_capture.WaveFormat.Encoding}";

    public double Rms
    {
        get
        {
            lock (_gate)
            {
                return _sampleCount == 0 ? 0d : Math.Sqrt(_sumOfSquares / _sampleCount);
            }
        }
    }

    public float Peak
    {
        get
        {
            lock (_gate)
            {
                return _peak;
            }
        }
    }

    public long BytesCaptured
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

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

    /// <summary>
    /// 清零累计统计，便于把基线与测试段的读数分开。
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _sumOfSquares = 0d;
            _sampleCount = 0;
            _peak = 0f;
            _bytes = 0;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture.WaveFormat;

        lock (_gate)
        {
            _bytes += e.BytesRecorded;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                for (var i = 0; i + 4 <= e.BytesRecorded; i += 4)
                {
                    Accumulate(BitConverter.ToSingle(e.Buffer, i));
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                for (var i = 0; i + 2 <= e.BytesRecorded; i += 2)
                {
                    Accumulate(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                }
            }
        }
    }

    private void Accumulate(float value)
    {
        _sumOfSquares += (double)value * value;
        _sampleCount++;

        var magnitude = Math.Abs(value);
        if (magnitude > _peak)
        {
            _peak = magnitude;
        }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        Stop();
        _capture.Dispose();
    }
}
