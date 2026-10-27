namespace LiveDictation.Audio;

/// <summary>
/// 音の出どころ。mac 版と同じく「声の聞き分け」ではなく取得経路で話者を分ける。
/// </summary>
public enum AudioSourceKind
{
    /// <summary>自分の声。マイク入力。</summary>
    Microphone,

    /// <summary>相手の声。スピーカーに出ている音のループバック。</summary>
    SystemPlayback,
}

public static class AudioSourceKindExtensions
{
    public static string ToDisplayName(this AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Microphone => "自分",
        AudioSourceKind.SystemPlayback => "相手",
        _ => kind.ToString(),
    };
}
