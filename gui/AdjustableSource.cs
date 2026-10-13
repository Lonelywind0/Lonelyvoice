using NAudio.Wave;

namespace VirtualSoundCard.Gui;

/// <summary>
/// 可运行时调节音量的音源包装器。
///
/// 存在的理由：核心库在构造音源时就把音量写死了（FileSource 交给 AudioFileReader，
/// CaptureSource 交给 VolumeSampleProvider），界面拖动推子时无法改动已构造的对象。
/// 这里在最外层套一层，并把 Start / Stop / Dispose 原样透传给内层，
/// 使 <see cref="RouterSession"/> 仍能像对待原生音源一样统一管理生命周期。
/// </summary>
internal sealed class AdjustableSource : ISampleProvider, IStartableSource, IDisposable
{
    private readonly ISampleProvider _inner;
    private float _volume = 1f;

    public AdjustableSource(ISampleProvider inner, float volume = 1f)
    {
        _inner = inner;
        _volume = volume;
        WaveFormat = inner.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public float Volume
    {
        get => _volume;
        set => _volume = value < 0f ? 0f : value > 1f ? 1f : value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);

        // 满音量时跳过乘法，避免无谓的逐样本开销。
        if (read > 0 && _volume != 1f)
        {
            var volume = _volume;

            for (var i = 0; i < read; i++)
            {
                buffer[offset + i] *= volume;
            }
        }

        return read;
    }

    public void Start()
    {
        if (_inner is IStartableSource startable)
        {
            startable.Start();
        }
    }

    public void Stop()
    {
        if (_inner is IStartableSource startable)
        {
            startable.Stop();
        }
    }

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
