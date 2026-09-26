using System.Text;
using PersonaEngine.Lib.TTS.Synthesis.TextProcessing;

namespace PersonaEngine.Lib.TTS.Synthesis.Engine;

/// <summary>
///     Buffers incoming LLM text chunks and yields completed sentences incrementally.
///     Uses a cheap punctuation pre-check to skip normalization + segmentation for
///     chunks that cannot contain sentence boundaries (~90% of LLM tokens).
///     <para>
///         Two latency-oriented behaviours on top of strict sentence detection:
///         1. A trailing sentence that is already terminated is released immediately
///         instead of waiting for the next sentence to begin.
///         2. Run-on text with no sentence terminator is flushed at the latest clause
///         boundary (comma / whitespace) once it grows past a language-aware length,
///         so TTS can start speaking without waiting for the whole paragraph.
///     </para>
/// </summary>
internal sealed class IncrementalSentenceAccumulator
{
    private static readonly char[] SentenceEndingChars = ['.', '!', '?', ';', ':', '\u2014'];

    // Clause-level flush tuning. CJK text packs far more meaning per character than
    // Latin text, so the same speech duration maps to very different character counts.
    private const int CjkSoftFlushMinChars = 8;
    private const int CjkSoftFlushMaxChars = 16;
    private const int LatinSoftFlushMinChars = 20;
    private const int LatinSoftFlushMaxChars = 60;
    private const int HardCapChars = 160;

    // A "[...]" control tag (e.g. "[EMOTION:😄]") that is still being streamed must not
    // be split: the fragment would be spoken because the tag is no longer parseable.
    // Tags longer than this are treated as literal text so a stray '[' cannot stall TTS.
    private const int MaxTagLength = 48;

    // Periods that end an abbreviation rather than a sentence. Without this guard the
    // lookahead-free "release a terminated trailing sentence" rule would split words
    // like "Dr. Smith" into two utterances.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "dr",
        "mr",
        "mrs",
        "ms",
        "prof",
        "st",
        "jr",
        "sr",
        "vs",
        "etc",
        "eg",
        "ie",
        "no",
        "fig",
        "approx",
        "dept",
        "est",
        "inc",
        "ltd",
        "corp",
    };

    private readonly ITextNormalizer _normalizer;
    private readonly ISentenceSegmenter _segmenter;
    private readonly StringBuilder _buffer = new(4096);

    public IncrementalSentenceAccumulator(ITextNormalizer normalizer, ISentenceSegmenter segmenter)
    {
        _normalizer = normalizer;
        _segmenter = segmenter;
    }

    public void Append(string chunk)
    {
        _buffer.Append(chunk);
    }

    public IReadOnlyList<string> TakeCompletedSentences()
    {
        if (_buffer.Length == 0)
        {
            return [];
        }

        // The LLM streams control tags such as "[EMOTION:😄]" token by token and the ':'
        // inside them looks like a sentence terminator. Hold the buffer until the tag
        // closes (and while it contains nothing but tags) so a fragment never reaches
        // synthesis and the tags stay attached to the text they annotate.
        if (ShouldWaitForTag())
        {
            return [];
        }

        if (!ContainsSentenceEndingPunctuation())
        {
            return TrySoftFlush();
        }

        var text = _buffer.ToString();
        var normalized = _normalizer.Normalize(text);

        if (string.IsNullOrEmpty(normalized))
        {
            return TrySoftFlush();
        }

        var sentences = _segmenter.Segment(normalized);

        if (sentences is null || sentences.Count == 0)
        {
            return TrySoftFlush();
        }

        // When the final detected sentence is already terminated there is nothing
        // pending, so it can be synthesized right away instead of being held back
        // until the next sentence starts streaming in.
        var lastIsComplete = IsCompleteSentence(sentences[^1]);
        var completedCount = lastIsComplete ? sentences.Count : sentences.Count - 1;

        if (completedCount <= 0)
        {
            return TrySoftFlush();
        }

        var completed = new List<string>(completedCount);
        for (var i = 0; i < completedCount; i++)
        {
            var sentence = sentences[i].Trim();
            if (sentence.Length > 0)
            {
                completed.Add(sentence);
            }
        }

        _buffer.Clear();
        if (!lastIsComplete)
        {
            var tail = sentences[^1];
            if (!string.IsNullOrWhiteSpace(tail))
            {
                _buffer.Append(tail);
            }
        }

        return completed.Count > 0 ? completed : TrySoftFlush();
    }

    public string? Flush()
    {
        if (_buffer.Length == 0)
        {
            return null;
        }

        var text = _buffer.ToString();
        var normalized = _normalizer.Normalize(text);

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var sentences = _segmenter.Segment(normalized);
        var joined = string.Join(" ", sentences).Trim();

        _buffer.Clear();

        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    public void Reset()
    {
        _buffer.Clear();
    }

    private bool ContainsSentenceEndingPunctuation()
    {
        foreach (var chunk in _buffer.GetChunks())
        {
            if (chunk.Span.IndexOfAny(SentenceEndingChars) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Emits the leading portion of an over-long, still-growing clause at the latest
    ///     clause boundary so synthesis can begin before the sentence is finished.
    ///     Returns an empty list when the buffer is still short enough to keep buffering.
    /// </summary>
    private IReadOnlyList<string> TrySoftFlush()
    {
        var length = _buffer.Length;
        if (length == 0)
        {
            return [];
        }

        var (minChars, maxChars) = GetSoftFlushBounds();
        if (length < maxChars)
        {
            return [];
        }

        var boundary = -1;
        for (var i = length - 1; i >= 0; i--)
        {
            if (!IsSoftBoundary(_buffer[i]))
            {
                continue;
            }

            // Scanning backwards only ever shortens the prefix, so the first
            // boundary below the minimum means no earlier one can qualify either.
            if (i + 1 < minChars)
            {
                break;
            }

            boundary = i;
            break;
        }

        if (boundary < 0)
        {
            if (length < HardCapChars)
            {
                return [];
            }

            boundary = length - 1;
        }

        var prefix = _buffer.ToString(0, boundary + 1).Trim();
        var remainder = _buffer.ToString(boundary + 1, length - boundary - 1).TrimStart();

        // Never emit a slice that is only control tags — it carries no speech and the
        // tags must stay attached to the text they annotate.
        if (IsTagOnly(prefix.AsSpan()))
        {
            return [];
        }

        _buffer.Clear();
        _buffer.Append(remainder);

        return prefix.Length == 0 ? [] : [prefix];
    }

    /// <summary>
    ///     True when the pending buffer must not be segmented yet because of a
    ///     streaming control tag: it either ends inside an unclosed <c>"[...]"</c>
    ///     span, or holds nothing but complete tags (no speech content).
    ///     Tags longer than <see cref="MaxTagLength" /> are treated as literal text so a
    ///     stray opening bracket cannot stall synthesis indefinitely.
    /// </summary>
    private bool ShouldWaitForTag()
    {
        var sawRealText = false;
        var sawTag = false;
        var insideTag = false;
        var openLength = 0;

        foreach (var chunk in _buffer.GetChunks())
        {
            foreach (var c in chunk.Span)
            {
                if (insideTag)
                {
                    if (c == ']')
                    {
                        insideTag = false;
                    }
                    else
                    {
                        openLength++;
                    }

                    continue;
                }

                if (c == '[')
                {
                    insideTag = true;
                    sawTag = true;
                    openLength = 0;

                    continue;
                }

                if (!char.IsWhiteSpace(c))
                {
                    sawRealText = true;
                }
            }
        }

        // Ends mid-tag: wait for the closing bracket (unless it is implausibly long).
        if (insideTag)
        {
            return openLength <= MaxTagLength;
        }

        // Nothing but complete tags and whitespace: no speech to synthesize yet.
        return sawTag && !sawRealText;
    }

    private static bool IsTagOnly(ReadOnlySpan<char> text)
    {
        var sawTag = false;
        var insideTag = false;

        foreach (var c in text)
        {
            if (insideTag)
            {
                if (c == ']')
                {
                    insideTag = false;
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c == '[')
            {
                insideTag = true;
                sawTag = true;
                continue;
            }

            return false;
        }

        return sawTag && !insideTag;
    }

    private (int MinChars, int MaxChars) GetSoftFlushBounds()
    {
        var cjk = 0;
        var other = 0;

        foreach (var chunk in _buffer.GetChunks())
        {
            foreach (var c in chunk.Span)
            {
                if (IsCjk(c))
                {
                    cjk++;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    other++;
                }
            }
        }

        return cjk >= other
            ? (CjkSoftFlushMinChars, CjkSoftFlushMaxChars)
            : (LatinSoftFlushMinChars, LatinSoftFlushMaxChars);
    }

    private static bool IsSoftBoundary(char c) =>
        char.IsWhiteSpace(c) || c is ',' or '\uff0c' or '\u3001' or ';' or '\uff1b' or ':' or '\uff1a' or '\u2026';

    private static bool IsCjk(char c) =>
        c is >= '\u4e00' and <= '\u9fff' // CJK unified ideographs
        or >= '\u3040' and <= '\u30ff' // Hiragana / Katakana
        or >= '\uac00' and <= '\ud7af' // Hangul syllables
        or >= '\u3000' and <= '\u303f' // CJK punctuation
        or >= '\uff00' and <= '\uffef'; // Fullwidth forms

    /// <summary>
    ///     True when <paramref name="sentence" /> ends with a sentence terminator
    ///     (ignoring trailing quotes/brackets). A bare <c>'.'</c> preceded by a digit
    ///     is treated as an ordinal/decimal and therefore not a sentence end.
    /// </summary>
    private static bool IsCompleteSentence(string sentence)
    {
        var span = sentence.AsSpan().TrimEnd();

        while (span.Length > 0 && IsTrailingCloser(span[^1]))
        {
            span = span[..^1].TrimEnd();
        }

        if (span.Length == 0)
        {
            return false;
        }

        var last = span[^1];
        if (last == '.')
        {
            if (span.Length >= 2 && char.IsDigit(span[^2]))
            {
                return false;
            }

            var word = TrailingWord(span[..^1]);

            // Single-letter initials ("U.S.", "e.g.") and known abbreviations are
            // not sentence ends.
            return word.Length != 1 && !Abbreviations.Contains(word);
        }

        return last
            is '!'
                or '?'
                or ';'
                or ':'
                or '\u3002' // 。
                or '\uff01' // ！
                or '\uff1f' // ？
                or '\uff1b' // ；
                or '\uff1a' // ：
                or '\u2026' // …
                or '\u2014'; // —
    }

    private static bool IsTrailingCloser(char c) =>
        c
            is '"'
                or '\''
                or ')'
                or ']'
                or '}'
                or '\u300d' // 」
                or '\u300f' // 』
                or '\uff09' // ）
                or '\u201d'; // ”

    private static string TrailingWord(ReadOnlySpan<char> span)
    {
        var end = span.Length;
        var start = end;

        while (start > 0 && char.IsLetter(span[start - 1]))
        {
            start--;
        }

        return start == end ? string.Empty : span[start..end].ToString();
    }
}
