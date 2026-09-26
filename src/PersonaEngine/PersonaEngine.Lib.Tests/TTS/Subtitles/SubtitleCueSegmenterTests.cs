using PersonaEngine.Lib.TTS.Synthesis;
using PersonaEngine.Lib.UI.Rendering.Subtitles;
using Xunit;

namespace PersonaEngine.Lib.Tests.TTS.Subtitles;

public sealed class SubtitleCueSegmenterTests
{
    [Fact]
    public void Split_ChinesePunctuation_CreatesShortCues()
    {
        var segment = CreateSegment(
            ("今天", 0.0, 0.3),
            ("天气", 0.3, 0.6),
            ("很好，", 0.6, 0.9),
            ("我们", 0.9, 1.2),
            ("一起", 1.2, 1.5),
            ("出去玩", 1.5, 1.8),
            ("吧。", 1.8, 2.1)
        );

        var cues = SubtitleCueSegmenter.Split(
            segment,
            new SubtitleCueOptions
            {
                MaxWeightedCharsPerCue = 40,
                MinWeightedCharsPerCue = 1,
                MaxDurationPerCueSeconds = 10f,
                PauseThresholdSeconds = 10f,
            }
        );

        Assert.Equal(2, cues.Count);
        Assert.Equal("今天天气很好，", cues[0].Text);
        Assert.Equal("我们一起出去玩吧。", cues[1].Text);
    }

    [Fact]
    public void Split_UsesWordPauseAsBoundary()
    {
        var segment = CreateSegment(
            ("Hello", 0.0, 0.3),
            ("world", 1.0, 1.3)
        );

        var cues = SubtitleCueSegmenter.Split(
            segment,
            new SubtitleCueOptions
            {
                MaxWeightedCharsPerCue = 100,
                MinWeightedCharsPerCue = 1,
                MaxDurationPerCueSeconds = 10f,
                PauseThresholdSeconds = 0.45f,
            }
        );

        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello", cues[0].Text);
        Assert.Equal("world", cues[1].Text);
    }

    [Fact]
    public void Split_ExpandsSingleChineseTokenAndSplitsAtComma()
    {
        var segment = CreateSegment(("今天天气很好，我们一起出去玩吧。", 0.0, 2.1));

        var cues = SubtitleCueSegmenter.Split(
            segment,
            new SubtitleCueOptions
            {
                MaxWeightedCharsPerCue = 100,
                MinWeightedCharsPerCue = 4,
                MaxDurationPerCueSeconds = 10f,
                PauseThresholdSeconds = 10f,
            }
        );

        Assert.Equal(2, cues.Count);
        Assert.Equal("今天天气很好，", cues[0].Text);
        Assert.Equal("我们一起出去玩吧。", cues[1].Text);
    }

    [Fact]
    public void Split_EnforcesMaxDurationWithoutPunctuation()
    {
        var segment = CreateSegment(
            ("one", 0.0, 0.4),
            ("two", 0.4, 0.8),
            ("three", 0.8, 1.2),
            ("four", 1.2, 1.6),
            ("five", 1.6, 2.0),
            ("six", 2.0, 2.4)
        );

        var cues = SubtitleCueSegmenter.Split(
            segment,
            new SubtitleCueOptions
            {
                MaxWeightedCharsPerCue = 100,
                MinWeightedCharsPerCue = 1,
                MaxDurationPerCueSeconds = 1.0f,
                PauseThresholdSeconds = 10f,
            }
        );

        Assert.True(cues.Count >= 3);
        Assert.All(cues, cue => Assert.True(cue.EndOffsetSeconds - cue.StartOffsetSeconds <= 1.2f));
    }

    private static AudioSegment CreateSegment(params (string Text, double Start, double End)[] words)
    {
        var tokens = words
            .Select(
                word =>
                    new Token
                    {
                        Text = word.Text,
                        Whitespace = word.Text.Any(
                            ch => ch >= '\u2E80' && ch <= '\u9FFF'
                        )
                            ? string.Empty
                            : " ",
                        StartTs = word.Start,
                        EndTs = word.End,
                    }
            )
            .ToArray();

        return new AudioSegment(Memory<float>.Empty, 24000, tokens);
    }
}
