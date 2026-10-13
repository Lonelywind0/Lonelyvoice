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
        else
        {
            // 多声道降混：按索引取模归并到输出声道后取平均。
            // 取平均而不是求和，是为了让总增益不随声道数增长，杜绝削波。
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
                    buffer[destination + c] /= groupSize;
                }
            }
        }

        return framesRead * _outputChannels;
    }
}
