using System.IO;
using LiveDictation.Audio;
using LiveDictation.Config;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace LiveDictation.Recognition;

/// <summary>
/// 保存済みの音声を、VAD で発話単位に切ってオフラインモデルで認識する。
///
/// ストリーミング認識と違って発話全体を見てから判断できるため、日本語の変換精度が上がる。
/// 区切りも VAD が決めるので、エンドポイントの秒数調整に振り回されない。
/// </summary>
public sealed class OfflineTranscriber : IDisposable
{
    /// <summary>silero VAD が受け付ける窓の大きさ。16kHz ではこの値で固定。</summary>
    private const int VadWindowSize = 512;

    private readonly OfflineRecognizer _recognizer;
    private readonly string _vadModelPath;
    private readonly AppSettings _settings;
    private readonly TextReplacer _replacer;
    private bool _disposed;

    public OfflineTranscriber(OfflineModelPaths model, string vadModelPath, AppSettings settings)
    {
        _vadModelPath = vadModelPath;
        _settings = settings;
        _replacer = TextReplacer.Load(AppSettings.ReplacementsPath);

        var config = new OfflineRecognizerConfig
        {
            FeatConfig = new FeatureConfig
            {
                SampleRate = SourceCapture.TargetSampleRate,
                FeatureDim = 80,
            },
            ModelConfig = new OfflineModelConfig
            {
                NeMoCtc = new OfflineNemoEncDecCtcModelConfig { Model = model.Model },
                Tokens = model.Tokens,
                ModelType = model.ModelType,
                NumThreads = Math.Max(1, settings.OfflineNumThreads),
                Provider = settings.Provider,
                Debug = 0,
            },
            DecodingMethod = "greedy_search",
        };

        try
        {
            _recognizer = new OfflineRecognizer(config);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "仕上げパス用の音声認識を初期化できませんでした。モデルの種類が合っていない可能性があります。", ex);
        }
    }

    /// <summary>1 本の WAV を発話単位に認識する。progress には 0.0〜1.0 を渡す。</summary>
    public List<TranscriptSegment> Transcribe(
        string wavPath,
        AudioSourceKind source,
        Action<double>? progress = null)
    {
        var results = new List<TranscriptSegment>();

        if (!File.Exists(wavPath))
        {
            return results;
        }

        var vadConfig = new VadModelConfig
        {
            SileroVad = new SileroVadModelConfig
            {
                Model = _vadModelPath,
                Threshold = (float)_settings.VadThreshold,
                MinSilenceDuration = (float)_settings.VadMinSilenceSeconds,
                MinSpeechDuration = 0.25F,
                WindowSize = VadWindowSize,
                // 会議の発話は長い。既定の 5 秒では文の途中で切れる。
                MaxSpeechDuration = (float)_settings.VadMaxSpeechSeconds,
            },
            SampleRate = SourceCapture.TargetSampleRate,
            NumThreads = 1,
            Provider = "cpu",
        };

        using var vad = new VoiceActivityDetector(vadConfig, bufferSizeInSeconds: 120);
        using var reader = new AudioFileReader(wavPath);

        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels != 1)
        {
            provider = new MonoDownmixSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != SourceCapture.TargetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SourceCapture.TargetSampleRate);
        }

        var totalSamples = Math.Max(1L, (long)(reader.TotalTime.TotalSeconds * SourceCapture.TargetSampleRate));
        var window = new float[VadWindowSize];
        long processed = 0;

        while (true)
        {
            var read = provider.Read(window, 0, VadWindowSize);
            if (read <= 0)
            {
                break;
            }

            if (read < VadWindowSize)
            {
                // VAD は固定長しか受け付けないため、末尾は無音で埋める。
                Array.Clear(window, read, VadWindowSize - read);
            }

            vad.AcceptWaveform(window);
            processed += read;

            DrainSegments(vad, source, results);
            progress?.Invoke(Math.Min(1.0, processed / (double)totalSamples));
        }

        // 末尾に残っている発話を吐き出させる。
        vad.Flush();
        DrainSegments(vad, source, results);

        progress?.Invoke(1.0);
        return results;
    }

    private void DrainSegments(VoiceActivityDetector vad, AudioSourceKind source, List<TranscriptSegment> results)
    {
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            vad.Pop();

            if (segment.Samples.Length == 0)
            {
                continue;
            }

            var text = Recognize(segment.Samples);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var start = TimeSpan.FromSeconds(segment.Start / (double)SourceCapture.TargetSampleRate);
            var end = start + TimeSpan.FromSeconds(segment.Samples.Length / (double)SourceCapture.TargetSampleRate);

            results.Add(new TranscriptSegment(source, start, end, text));
        }
    }

    private string Recognize(float[] samples)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(SourceCapture.TargetSampleRate, samples);
        _recognizer.Decode(stream);

        return _replacer.Apply(TextNormalizer.Normalize(stream.Result.Text));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer.Dispose();
    }
}
