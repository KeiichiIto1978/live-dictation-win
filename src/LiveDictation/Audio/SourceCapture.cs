using System.Diagnostics;
using LiveDictation.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveDictation.Audio;

/// <summary>
/// WASAPI の 1 系統を 16kHz モノラル float に正規化して流し続ける。
/// マイクとループバックで同じ扱いにできるよう、デバイス差（サンプルレート・チャンネル数）はここで吸収する。
/// </summary>
public sealed class SourceCapture : IDisposable
{
    public const int TargetSampleRate = 16000;

    /// <summary>1 回に送る長さ。短いほど字が出るのが速いが、呼び出し回数が増える。</summary>
    private const int FrameSamples = TargetSampleRate / 10; // 100ms

    /// <summary>
    /// 実時間からこれだけ遅れるまでは無音を挿し込まない。
    /// デバイス供給の揺らぎやクロック差で一時的に遅れるのは正常なため、
    /// ここを狭くすると発話の途中に無音が入って認識できなくなる。
    /// </summary>
    private const int SilenceToleranceSamples = TargetSampleRate * 3 / 10; // 300ms

    private readonly AudioSourceKind _kind;
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _pipeline;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startStopGate = new();

    private Task? _pumpTask;
    private bool _disposed;

    /// <summary>16kHz モノラルのサンプルが用意できたときに発火する。</summary>
    public event Action<AudioSourceKind, float[]>? SamplesReady;

    /// <summary>デバイス側の都合でキャプチャが落ちたときに発火する。</summary>
    public event Action<AudioSourceKind, Exception?>? CaptureStopped;

    public SourceCapture(AudioSourceKind kind, MMDevice device)
    {
        _kind = kind;

        _capture = kind == AudioSourceKind.SystemPlayback
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device);

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            // 取りこぼしよりも、落ちずに走り続けることを優先する。
            DiscardOnBufferOverflow = true,
            // ここで自動ゼロ埋めをさせない。
            // 取り込みの揺らぎで一瞬データが間に合わないだけでも無音が挿し込まれ、
            // 発話が 100ms 単位で寸断されて認識できなくなるため。
            // 本当に無音が続いたときだけ、ポンプ側で明示的に無音を送る。
            ReadFully = false,
        };

        ISampleProvider provider = _buffer.ToSampleProvider();
        if (provider.WaveFormat.Channels != 1)
        {
            provider = new MonoDownmixSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != TargetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);
        }

        _pipeline = provider;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        Log.Info($"{kind} のキャプチャを準備しました: {_capture.WaveFormat}");
    }

    public void Start()
    {
        lock (_startStopGate)
        {
            if (_pumpTask is not null)
            {
                return;
            }

            _capture.StartRecording();
            _pumpTask = Task.Run(() => PumpLoopAsync(_cts.Token));
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        // ここは音声スレッド。重い処理はせず、バッファに積むだけにする。
        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log.Error($"{_kind} のキャプチャが異常終了しました", e.Exception);
        }

        CaptureStopped?.Invoke(_kind, e.Exception);
    }

    /// <summary>
    /// 取り込めた音をできるだけ早く下流へ流す。
    ///
    /// 届いている音はその場で全部出し、実時間から一定以上遅れたときだけ無音を足して時間軸を合わせる。
    /// 「実時間ぴったりに切り出す」方式にすると、供給の揺らぎのたびに無音が挿し込まれ、
    /// 発話が寸断されて認識できなくなる。
    /// </summary>
    private async Task PumpLoopAsync(CancellationToken token)
    {
        // デバイスが最初のデータを寄越すまでに時間がかかるため、
        // 起動直後から時計を回すと冒頭に無音が挿し込まれる。最初の音が来てから数える。
        var clock = new Stopwatch();
        long emittedSamples = 0;
        var frame = new float[FrameSamples];
        float[]? silence = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // 1) 届いている分はためこまずに全部流す。
                while (!token.IsCancellationRequested)
                {
                    var read = _pipeline.Read(frame, 0, FrameSamples);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (!clock.IsRunning)
                    {
                        clock.Start();
                    }

                    emittedSamples += read;

                    var payload = new float[read];
                    Array.Copy(frame, payload, read);
                    SamplesReady?.Invoke(_kind, payload);

                    if (read < FrameSamples)
                    {
                        // 中途半端な長さしか残っていない。続きは次の周回で拾う。
                        break;
                    }
                }

                // 2) 本当に音が来ていない区間だけ、無音で時間を進める。
                //    これがないと発話区切り（エンドポイント）が確定せず、文字が伸び続ける。
                var fillUntil = clock.IsRunning
                    ? (long)(clock.Elapsed.TotalSeconds * TargetSampleRate) - SilenceToleranceSamples
                    : 0;
                while (emittedSamples + FrameSamples <= fillUntil && !token.IsCancellationRequested)
                {
                    silence ??= new float[FrameSamples];
                    emittedSamples += FrameSamples;
                    SamplesReady?.Invoke(_kind, silence);
                }

                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求による正常終了。
        }
        catch (Exception ex)
        {
            Log.Error($"{_kind} の取り込みループが停止しました", ex);
            CaptureStopped?.Invoke(_kind, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_startStopGate)
        {
            _cts.Cancel();

            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex)
            {
                Log.Warn($"{_kind} のキャプチャ停止で例外が出ました: {ex.Message}");
            }

            // ポンプが _pipeline を触っている間に破棄すると落ちるため、必ず待ってから片付ける。
            try
            {
                _pumpTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // キャンセル由来の例外は無視してよい。
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _cts.Dispose();
        }
    }
}
