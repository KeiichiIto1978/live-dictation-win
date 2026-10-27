using System.IO;
using LiveDictation.Diagnostics;
using NAudio.Wave;

namespace LiveDictation.Audio;

/// <summary>
/// 16kHz モノラル 16bit PCM の WAV を書き出す。
/// 音声スレッドから呼ばれるため、書き込みはロックで直列化する。
/// Windows はファイルを掴んだままだと削除も移動もできないので、Dispose を確実に通す。
/// </summary>
public sealed class MonoWaveWriter : IDisposable
{
    private readonly object _gate = new();
    private WaveFileWriter? _writer;
    private short[] _scratch = Array.Empty<short>();

    public MonoWaveWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new WaveFileWriter(path, new WaveFormat(SourceCapture.TargetSampleRate, 16, 1));
    }

    public string Path { get; }

    public void Write(float[] samples)
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            if (_scratch.Length < samples.Length)
            {
                _scratch = new short[samples.Length];
            }

            for (var i = 0; i < samples.Length; i++)
            {
                // クリップしてから 16bit に落とす。歪むより切れたほうが聞き取りやすい。
                var value = Math.Clamp(samples[i], -1f, 1f);
                _scratch[i] = (short)(value * short.MaxValue);
            }

            try
            {
                _writer.WriteSamples(_scratch, 0, samples.Length);
            }
            catch (IOException ex)
            {
                Log.Error($"音声の書き込みに失敗しました: {Path}", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (IOException ex)
            {
                Log.Error($"音声ファイルを閉じられませんでした: {Path}", ex);
            }
            finally
            {
                _writer = null;
            }
        }
    }
}
