using NAudio.Wave;

namespace LiveDictation.Audio;

/// <summary>
/// 任意チャンネル数のサンプルを単純平均でモノラルに落とす。
/// NAudio の StereoToMonoSampleProvider は 2ch 専用で、
/// 会議デバイスには 4ch 以上を名乗るものがあるため自前で持つ。
/// </summary>
public sealed class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_sourceChannels == 1)
        {
            return _source.Read(buffer, offset, count);
        }

        var needed = count * _sourceChannels;
        if (_sourceBuffer.Length < needed)
        {
            _sourceBuffer = new float[needed];
        }

        var read = _source.Read(_sourceBuffer, 0, needed);
        var framesRead = read / _sourceChannels;

        for (var frame = 0; frame < framesRead; frame++)
        {
            var sum = 0f;
            var baseIndex = frame * _sourceChannels;
            for (var channel = 0; channel < _sourceChannels; channel++)
            {
                sum += _sourceBuffer[baseIndex + channel];
            }

            buffer[offset + frame] = sum / _sourceChannels;
        }

        return framesRead;
    }
}
