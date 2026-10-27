using LiveDictation.Audio;

namespace LiveDictation.Recognition;

/// <summary>確定した 1 発言。</summary>
public sealed record TranscriptSegment(
    AudioSourceKind Source,
    TimeSpan Start,
    TimeSpan End,
    string Text)
{
    public string SpeakerName => Source.ToDisplayName();

    /// <summary>画面表示用の経過時刻。</summary>
    public string TimeLabel => Format(Start);

    public string ToMarkdownLine() =>
        $"- `{Format(Start)}` **{SpeakerName}**: {Text}";

    private static string Format(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
