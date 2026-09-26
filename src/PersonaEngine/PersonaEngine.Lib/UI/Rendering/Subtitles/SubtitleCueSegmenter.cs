using System.Text;
using PersonaEngine.Lib.TTS.Synthesis;

namespace PersonaEngine.Lib.UI.Rendering.Subtitles;

/// <summary>
///     Splits a TTS audio segment into short subtitle cues. It prefers
///     sentence punctuation, then clause punctuation, then pauses from word
///     timestamps, and finally falls back to weighted character/duration caps.
/// </summary>
internal static class SubtitleCueSegmenter
{
    private const string StrongBoundaryChars = "。！？!?.";

    private const string MediumBoundaryChars = "；;：:";

    private const string SoftBoundaryChars = "，,、";

    public static IReadOnlyList<SubtitleCue> Split(
        AudioSegment segment,
        SubtitleCueOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(options);

        var tokens = ExpandTokensForCueing(segment.Tokens);
        if (tokens.Count == 0)
        {
            return [];
        }

        var timings = ResolveTimings(tokens, segment.DurationInSeconds);
        var cues = new List<SubtitleCue>();
        var startIndex = 0;
        var weightedChars = 0.0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var tokenChars = CountWeightedChars(token.Text + token.Whitespace);
            var currentDuration = timings.End[i] - timings.Start[startIndex];

            var exceedsChars =
                startIndex < i
                && weightedChars + tokenChars > options.MaxWeightedCharsPerCue;
            var exceedsDuration =
                startIndex < i
                && currentDuration > options.MaxDurationPerCueSeconds;
            var hasPause =
                startIndex < i
                && timings.Start[i] - timings.End[i - 1] >= options.PauseThresholdSeconds;

            if (exceedsChars || exceedsDuration || hasPause)
            {
                cues.Add(CreateCue(tokens, timings, startIndex, i - 1));
                startIndex = i;
                weightedChars = 0.0;
            }

            weightedChars += tokenChars;

            if (
                EndsWithAny(token.Text, StrongBoundaryChars)
                || (
                    weightedChars >= options.MinWeightedCharsPerCue
                    && (
                        EndsWithAny(token.Text, MediumBoundaryChars)
                        || EndsWithAny(token.Text, SoftBoundaryChars)
                    )
                )
            )
            {
                cues.Add(CreateCue(tokens, timings, startIndex, i));
                startIndex = i + 1;
                weightedChars = 0.0;
            }
        }

        if (startIndex < tokens.Count)
        {
            cues.Add(CreateCue(tokens, timings, startIndex, tokens.Count - 1));
        }

        return cues;
    }

    public static bool EndsWithBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return EndsWithAny(text, StrongBoundaryChars)
            || EndsWithAny(text, MediumBoundaryChars)
            || EndsWithAny(text, SoftBoundaryChars);
    }

    public static double CountWeightedChars(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0.0;
        }

        var weighted = 0.0;
        foreach (var ch in text)
        {
            weighted += IsCjk(ch) ? 1.0 : 0.5;
        }

        return weighted;
    }

    public static double CountWeightedCharsForTokens(IReadOnlyList<Token> tokens)
    {
        var weighted = 0.0;
        for (var i = 0; i < tokens.Count; i++)
        {
            weighted += CountWeightedChars(tokens[i].Text + tokens[i].Whitespace);
        }

        return weighted;
    }

    private static List<Token> ExpandTokensForCueing(IReadOnlyList<Token> source)
    {
        var result = new List<Token>(source.Count);

        for (var i = 0; i < source.Count; i++)
        {
            var token = source[i];
            var units = SplitDisplayUnits(token.Text);
            if (units.Count <= 1)
            {
                result.Add(token);
                continue;
            }

            var totalWeight = 0.0;
            for (var j = 0; j < units.Count; j++)
            {
                totalWeight += Math.Max(
                    0.5,
                    CountWeightedChars(units[j].Text + units[j].Whitespace)
                );
            }

            totalWeight = Math.Max(0.5, totalWeight);
            var hasTiming = token.StartTs.HasValue;
            var tokenStart = token.StartTs ?? 0.0;
            var tokenEnd = token.EndTs ?? tokenStart + 0.01;
            var tokenDuration = Math.Max(0.01, tokenEnd - tokenStart);
            var cursor = tokenStart;

            for (var j = 0; j < units.Count; j++)
            {
                var unit = units[j];
                var unitWeight = Math.Max(
                    0.5,
                    CountWeightedChars(unit.Text + unit.Whitespace)
                );
                var unitDuration = tokenDuration * unitWeight / totalWeight;

                result.Add(
                    token with
                    {
                        Text = unit.Text,
                        Whitespace = unit.Whitespace,
                        StartTs = hasTiming ? cursor : null,
                        EndTs = hasTiming ? cursor + unitDuration : null,
                    }
                );

                cursor += unitDuration;
            }
        }

        return result;
    }

    private static List<DisplayUnit> SplitDisplayUnits(string text)
    {
        var units = new List<DisplayUnit>();
        if (string.IsNullOrEmpty(text))
        {
            return units;
        }

        var buffer = new StringBuilder();
        var pendingWhitespace = false;

        void ApplyPendingWhitespace()
        {
            if (!pendingWhitespace || units.Count == 0)
            {
                return;
            }

            var last = units[^1];
            if (string.IsNullOrEmpty(last.Whitespace))
            {
                units[^1] = last with { Whitespace = " " };
            }

            pendingWhitespace = false;
        }

        void FlushBuffer()
        {
            ApplyPendingWhitespace();
            if (buffer.Length == 0)
            {
                return;
            }

            units.Add(new DisplayUnit(buffer.ToString(), string.Empty));
            buffer.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushBuffer();
                units.Add(new DisplayUnit(ch.ToString(), string.Empty));
            }
            else if (IsDisplayPunctuation(ch))
            {
                FlushBuffer();
                if (units.Count == 0)
                {
                    units.Add(new DisplayUnit(ch.ToString(), string.Empty));
                }
                else
                {
                    var last = units[^1];
                    units[^1] = last with { Text = last.Text + ch };
                }
            }
            else if (char.IsWhiteSpace(ch))
            {
                FlushBuffer();
                pendingWhitespace = true;
            }
            else
            {
                ApplyPendingWhitespace();
                buffer.Append(ch);
            }
        }

        FlushBuffer();
        ApplyPendingWhitespace();

        return units;
    }

    private static SubtitleCue CreateCue(
        IReadOnlyList<Token> tokens,
        TokenTimings timings,
        int startIndex,
        int endIndex
    )
    {
        var cueStart = timings.Start[startIndex];
        var cueEnd = timings.End[endIndex];
        var cueTokens = new Token[endIndex - startIndex + 1];

        for (var i = startIndex; i <= endIndex; i++)
        {
            var token = tokens[i];
            cueTokens[i - startIndex] = token with
            {
                StartTs = timings.Start[i] - cueStart,
                EndTs = timings.End[i] - cueStart,
            };
        }

        var text = string.Concat(cueTokens.Select(t => t.Text + t.Whitespace)).Trim();

        return new SubtitleCue(
            cueTokens,
            (float)cueStart,
            (float)cueEnd,
            text
        );
    }

    private static TokenTimings ResolveTimings(
        IReadOnlyList<Token> tokens,
        double segmentDurationSeconds
    )
    {
        var starts = new double[tokens.Count];
        var ends = new double[tokens.Count];
        var hasAnyTiming = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].StartTs.HasValue)
            {
                hasAnyTiming = true;
                break;
            }
        }

        if (!hasAnyTiming)
        {
            var totalWeight = Math.Max(0.001, CountWeightedCharsForTokens(tokens));
            var duration = Math.Max(0.01, segmentDurationSeconds);
            var cursor = 0.0;

            for (var i = 0; i < tokens.Count; i++)
            {
                var weight = Math.Max(0.5, CountWeightedChars(tokens[i].Text + tokens[i].Whitespace));
                starts[i] = cursor;
                cursor += duration * weight / totalWeight;
                ends[i] = cursor;
            }

            return new TokenTimings(starts, ends);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            starts[i] = token.StartTs
                ?? (i > 0 ? ends[i - 1] : 0.0);

            ends[i] = token.EndTs
                ?? (
                    i + 1 < tokens.Count && tokens[i + 1].StartTs.HasValue
                        ? tokens[i + 1].StartTs.Value
                        : starts[i] + 0.01
                );

            if (ends[i] <= starts[i])
            {
                ends[i] = starts[i] + 0.01;
            }

            if (i > 0 && starts[i] < ends[i - 1])
            {
                starts[i] = ends[i - 1];
                ends[i] = Math.Max(ends[i], starts[i] + 0.01);
            }
        }

        return new TokenTimings(starts, ends);
    }

    private static bool EndsWithAny(string text, string chars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().TrimEnd();
        if (span.IsEmpty)
        {
            return false;
        }

        var last = span[^1];
        if (last == '.' && span.Length >= 2 && char.IsDigit(span[^2]))
        {
            return false;
        }

        return chars.IndexOf(last) >= 0;
    }

    private static bool IsCjk(char value) =>
        value >= '\u2E80' && value <= '\u9FFF';

    private static bool IsDisplayPunctuation(char value) =>
        "。！？!?.;；:：,，、…".IndexOf(value) >= 0;

    private readonly record struct TokenTimings(double[] Start, double[] End);

    private readonly record struct DisplayUnit(string Text, string Whitespace);
}

internal sealed record SubtitleCue(
    IReadOnlyList<Token> Tokens,
    float StartOffsetSeconds,
    float EndOffsetSeconds,
    string Text
);
