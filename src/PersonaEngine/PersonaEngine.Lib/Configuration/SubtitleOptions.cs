namespace PersonaEngine.Lib.Configuration;

/// <summary>
///     Configuration for the subtitle renderer.
/// </summary>
public record SubtitleOptions
{
    public string Font { get; set; } = "DynaPuff.ttf";

    public int FontSize { get; set; } = 125;

    public string Color { get; set; } = "#FFf8f6f7";

    public string HighlightColor { get; set; } = "#FFc4251e";

    public int BottomMargin { get; set; } = 250;

    public int SideMargin { get; set; } = 30;

    public float InterSegmentSpacing { get; set; } = 10f;

    public int MaxVisibleLines { get; set; } = 2;

    /// <summary>Weighted character budget for one subtitle cue. CJK counts as 1, Latin as 0.5.</summary>
    public int MaxCharsPerCue { get; set; } = 20;

    /// <summary>Minimum weighted characters before soft punctuation may end a cue.</summary>
    public int MinCharsPerCue { get; set; } = 4;

    /// <summary>Maximum duration of one subtitle cue in seconds.</summary>
    public float MaxCueDurationSeconds { get; set; } = 3.0f;

    /// <summary>Word-gap duration treated as a natural subtitle break, in seconds.</summary>
    public float CuePauseThresholdSeconds { get; set; } = 0.45f;

    public float AnimationDuration { get; set; } = 0.3f;

    public int StrokeThickness { get; set; } = 3;

    public int Width { get; set; } = 1080;

    public int Height { get; set; } = 1920;
}
