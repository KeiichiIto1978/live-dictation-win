using System.IO;
using LiveDictation.Audio;
using LiveDictation.Config;
using LiveDictation.Diagnostics;

namespace LiveDictation.Session;

/// <summary>
/// 1 回の録音。音声を取り込んで WAV に落とすところまでを受け持つ。
///
/// 文字起こしは録音中には行わない。
/// ストリーミング認識は日本語の精度が出ず、画面に別言語の文字を並べるだけだったため、
/// 認識は停止後の仕上げパス（<see cref="FinalPass"/>）に一本化している。
/// </summary>
public sealed class RecordingSession : IDisposable
{
    /// <summary>メーターの落ち方。1 フレーム (100ms) ごとにこの割合まで減衰させる。</summary>
    private const float LevelDecay = 0.80f;

    /// <summary>メーターの下端。これより静かな音は 0 として扱う。</summary>
    private const float FloorDecibels = 60f;

    private readonly AudioCaptureManager _capture = new();
    private readonly MonoWaveWriter? _microphoneWave;
    private readonly MonoWaveWriter? _systemWave;
    private readonly object _gate = new();

    private float _microphoneLevel;
    private float _systemPlaybackLevel;
    private long _microphoneSamples;
    private long _systemPlaybackSamples;
    private bool _disposed;

    public DateTime StartedAt { get; } = DateTime.Now;

    public string Directory { get; }

    public string? Trigger { get; }

    public RecordingSession(AppSettings settings, string? trigger)
    {
        Trigger = trigger;

        Directory = Path.Combine(settings.ResolvedSessionRoot, StartedAt.ToString("yyyy-MM-dd_HHmmss"));
        System.IO.Directory.CreateDirectory(Directory);

        _microphoneWave = new MonoWaveWriter(Path.Combine(Directory, "microphone.wav"));
        _systemWave = new MonoWaveWriter(Path.Combine(Directory, "system.wav"));

        _capture.SamplesReady += OnSamplesReady;
        _capture.Start();

        Log.Info($"録音を開始しました: {Directory}");
    }

    public bool IsMicrophoneActive => _capture.IsMicrophoneActive;

    public bool IsSystemPlaybackActive => _capture.IsSystemPlaybackActive;

    public TimeSpan Elapsed => DateTime.Now - StartedAt;

    /// <summary>マイク入力の大きさ (0.0〜1.0)。画面のインジケーター用。</summary>
    public float MicrophoneLevel => _microphoneLevel;

    /// <summary>再生音の大きさ (0.0〜1.0)。画面のインジケーター用。</summary>
    public float SystemPlaybackLevel => _systemPlaybackLevel;

    /// <summary>
    /// マイクから受け取った音の長さ（秒）。
    /// マイクによっては無音時に完全な digital silence を返すため、レベルだけでは
    /// 「静かなだけ」と「取り込めていない」を区別できない。増えていれば生きている。
    /// </summary>
    public double MicrophoneSeconds => Interlocked.Read(ref _microphoneSamples) / (double)SourceCapture.TargetSampleRate;

    /// <summary>再生音から受け取った音の長さ（秒）。</summary>
    public double SystemPlaybackSeconds => Interlocked.Read(ref _systemPlaybackSamples) / (double)SourceCapture.TargetSampleRate;

    private void OnSamplesReady(AudioSourceKind kind, float[] samples)
    {
        if (kind == AudioSourceKind.Microphone)
        {
            _microphoneWave?.Write(samples);
            _microphoneLevel = NextLevel(_microphoneLevel, samples);
            Interlocked.Add(ref _microphoneSamples, samples.Length);
        }
        else
        {
            _systemWave?.Write(samples);
            _systemPlaybackLevel = NextLevel(_systemPlaybackLevel, samples);
            Interlocked.Add(ref _systemPlaybackSamples, samples.Length);
        }
    }

    /// <summary>
    /// 表示用のレベルを求める。
    ///
    /// 振幅をそのまま使うと、部屋の暗騒音（-50dB 前後）がメーター上で 0 に潰れ、
    /// マイクが生きているのか死んでいるのか分からなくなる。
    /// 「取り込めていること」を確認するのが目的なので、dB に直して下端を広げる。
    /// 上がるときは即座に、下がるときはゆっくり追従させて点滅を防ぐ。
    /// </summary>
    private static float NextLevel(float current, float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        var scaled = ToDisplayScale(peak);
        var decayed = current * LevelDecay;
        return scaled > decayed ? scaled : decayed;
    }

    /// <summary>振幅 (0〜1) を -<see cref="FloorDecibels"/>dB 〜 0dB の範囲で 0〜1 に写す。</summary>
    private static float ToDisplayScale(float peak)
    {
        if (peak <= 0f)
        {
            return 0f;
        }

        var decibels = 20f * MathF.Log10(peak);
        if (decibels <= -FloorDecibels)
        {
            return 0f;
        }

        return MathF.Min(1f, (decibels + FloorDecibels) / FloorDecibels);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // 入口から閉じる。閉じたあとに音が流れ込むと、破棄済みのライターに書き込んでしまう。
        _capture.SamplesReady -= OnSamplesReady;
        _capture.Dispose();

        _microphoneWave?.Dispose();
        _systemWave?.Dispose();

        _microphoneLevel = 0f;
        _systemPlaybackLevel = 0f;

        Log.Info($"録音を終了しました: {Directory}");
    }
}
