namespace PersonaEngine.Lib.UI.Rendering.Subtitles;

/// <summary>
///     Content-level limits used to split one TTS sentence into shorter
///     subtitle cues without changing the audio sent to the TTS engine.
/// </summary>
public sealed record SubtitleCueOptions
{
    /// <summary>
    ///     Weighted character budget per cue. CJK characters count as one,
    ///     Latin characters and whitespace count as half.
    /// </summary>
    public int MaxWeightedCharsPerCue { get; init; } = 20;

    /// <summary>
    ///     Minimum weighted characters before a comma/soft pause is allowed to
    ///     end a cue. Prevents one- or two-character fragments.
    /// </summary>
    public int MinWeightedCharsPerCue { get; init; } = 4;

    /// <summary>Maximum cue duration before a hard split is forced.</summary>
    public float MaxDurationPerCueSeconds { get; init; } = 3.0f;

    /// <summary>
    ///     Word-boundary silence that is treated as a natural subtitle break.
    ///     Only applied when word-level timestamps are available.
    /// </summary>
    public float PauseThresholdSeconds { get; init; } = 0.45f;
}
