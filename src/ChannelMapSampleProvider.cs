using NAudio.Wave;

namespace VirtualSoundCard;

/// <summary>
/// 通用声道映射：把任意声道数的音源转换到目标声道数。
///
/// NAudio 自带的 MonoToStereo / StereoToMono 只覆盖 1↔2，遇到 5.1、7.1 这类
/// 多声道源会抛「Source must be stereo」，所以这里自己实现一套全覆盖的转换。
/// </summary>
public sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inputChannels;
    private readonly int _outputChannels;
    private float[] _input = [];

    public ChannelMapSampleProvider(ISampleProvider source, int outputChannels)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (outputChannels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(outputChannels), outputChannels, "输出声道数必须大于 0。");
        }

        _source = source;
        _inputChannels = source.WaveFormat.Channels;
        _outputChannels = outputChannels;

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_inputChannels == _outputChannels)
        {
            return _source.Read(buffer, offset, count);
        }

        var frames = count / _outputChannels;
        var needed = frames * _inputChannels;

        if (_input.Length < needed)
        {
            _input = new float[needed];
        }

        var read = _source.Read(_input, 0, needed);
        var framesRead = read / _inputChannels;

        if (framesRead == 0)
        {
            return 0;
        }

        if (_inputChannels == 1)
        {
            // 单声道铺到所有输出声道。
            for (var f = 0; f < framesRead; f++)
            {
                var value = _input[f];
                var destination = offset + f * _outputChannels;

                for (var c = 0; c < _outputChannels; c++)
                {
                    buffer[destination + c] = value;
                }
            }
        }
        else if (_outputChannels == 1)
        {
            // 多声道等权平均降为单声道。
            for (var f = 0; f < framesRead; f++)
            {
                var source = f * _inputChannels;
                var sum = 0f;

                for (var c = 0; c < _inputChannels; c++)
                {
                    sum += _input[source + c];
                }

                buffer[offset + f] = sum / _inputChannels;
            }
        }
        else if (_outputChannels == 2)
        {
            // 多声道 → 立体声：前两个声道（FL / FR）作为主体并保持原始电平，
            // 其余声道（中置 / 环绕 / LFE）取均值后以半权重混入两侧。
            //
            // 早前把「前两声道 + 其它声道」整体按输入声道数取平均，会让主声道
            // 只剩 1/N（5.1 时即 1/3），多声道音乐播放时音量明显偏低，这里修正。
            var extraChannels = _inputChannels - 2;

            for (var f = 0; f < framesRead; f++)
            {
                var source = f * _inputChannels;
                var destination = offset + f * 2;

                var extra = 0f;

                for (var c = 2; c < _inputChannels; c++)
                {
                    extra += _input[source + c];
                }

                extra = extra / extraChannels * 0.5f;

                buffer[destination] = Clamp(_input[source] + extra);
                buffer[destination + 1] = Clamp(_input[source + 1] + extra);
            }
        }
        else
        {
            // 其它目标声道数：按索引取模归并后取平均。
            var groupSize = (float)_inputChannels / _outputChannels;

            for (var f = 0; f < framesRead; f++)
            {
                var source = f * _inputChannels;
                var destination = offset + f * _outputChannels;

                for (var c = 0; c < _outputChannels; c++)
                {
                    buffer[destination + c] = 0f;
                }

                for (var c = 0; c < _inputChannels; c++)
                {
                    buffer[destination + c % _outputChannels] += _input[source + c];
                }

                for (var c = 0; c < _outputChannels; c++)
                {
                    buffer[destination + c] = Clamp(buffer[destination + c] / groupSize);
                }
            }
        }

        return framesRead * _outputChannels;
    }

    private static float Clamp(float value)
    {
        if (value > 1f)
        {
            return 1f;
        }

        return value < -1f ? -1f : value;
    }
}
