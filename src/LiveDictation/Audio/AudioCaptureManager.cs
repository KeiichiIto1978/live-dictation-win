using LiveDictation.Diagnostics;
using NAudio.CoreAudioApi;

namespace LiveDictation.Audio;

/// <summary>
/// マイクと再生音の 2 系統を同時に取り込む。
/// mac 版が ScreenCaptureKit で担っていた役割を WASAPI で置き換えたもの。
/// </summary>
public sealed class AudioCaptureManager : IDisposable
{
    private readonly object _gate = new();
    private SourceCapture? _microphone;
    private SourceCapture? _systemPlayback;
    private MMDeviceEnumerator? _enumerator;
    private bool _disposed;

    public event Action<AudioSourceKind, float[]>? SamplesReady;

    /// <summary>マイクが使えなかった場合など、片側だけ動いている状態を UI に伝えるために持つ。</summary>
    public bool IsMicrophoneActive { get; private set; }

    public bool IsSystemPlaybackActive { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_microphone is not null || _systemPlayback is not null)
            {
                return;
            }

            _enumerator = new MMDeviceEnumerator();

            _systemPlayback = TryCreate(
                AudioSourceKind.SystemPlayback,
                () => _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia),
                "既定の再生デバイスが見つからないため、相手の声は取り込めません。");
            IsSystemPlaybackActive = _systemPlayback is not null;

            _microphone = TryCreate(
                AudioSourceKind.Microphone,
                () => _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications),
                "既定のマイクが見つからないため、自分の声は取り込めません。");
            IsMicrophoneActive = _microphone is not null;

            if (_microphone is null && _systemPlayback is null)
            {
                throw new InvalidOperationException(
                    "マイクも再生デバイスも利用できませんでした。サウンド設定で既定のデバイスを確認してください。");
            }
        }
    }

    private SourceCapture? TryCreate(AudioSourceKind kind, Func<MMDevice> deviceFactory, string failureMessage)
    {
        try
        {
            var device = deviceFactory();
            var capture = new SourceCapture(kind, device);
            capture.SamplesReady += OnSamplesReady;
            capture.CaptureStopped += OnCaptureStopped;
            capture.Start();
            return capture;
        }
        catch (Exception ex)
        {
            // 片側が駄目でも、もう片側だけで録れるほうが利用者には有用なので続行する。
            Log.Error(failureMessage, ex);
            return null;
        }
    }

    private void OnSamplesReady(AudioSourceKind kind, float[] samples) => SamplesReady?.Invoke(kind, samples);

    private void OnCaptureStopped(AudioSourceKind kind, Exception? error)
    {
        if (kind == AudioSourceKind.Microphone)
        {
            IsMicrophoneActive = false;
        }
        else
        {
            IsSystemPlaybackActive = false;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            DisposeCapture(ref _microphone);
            DisposeCapture(ref _systemPlayback);
            IsMicrophoneActive = false;
            IsSystemPlaybackActive = false;

            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    private void DisposeCapture(ref SourceCapture? capture)
    {
        if (capture is null)
        {
            return;
        }

        capture.SamplesReady -= OnSamplesReady;
        capture.CaptureStopped -= OnCaptureStopped;
        capture.Dispose();
        capture = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
