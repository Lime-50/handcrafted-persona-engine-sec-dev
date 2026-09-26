using NSubstitute;
using PersonaEngine.Lib.TTS.Synthesis.Engine;
using PersonaEngine.Lib.TTS.Synthesis.TextProcessing;
using Xunit;

namespace PersonaEngine.Lib.Tests.TTS.Synthesis.Engine;

public class IncrementalSentenceAccumulatorTests
{
    private readonly ITextNormalizer _normalizer = Substitute.For<ITextNormalizer>();
    private readonly ISentenceSegmenter _segmenter = Substitute.For<ISentenceSegmenter>();

    private IncrementalSentenceAccumulator CreateAccumulator() => new(_normalizer, _segmenter);

    [Fact]
    public void TakeCompletedSentences_NoPunctuation_ReturnsEmpty()
    {
        var acc = CreateAccumulator();

        acc.Append("Hello world");
        var result = acc.TakeCompletedSentences();

        Assert.Empty(result);
        _normalizer.DidNotReceive().Normalize(Arg.Any<string>());
        _segmenter.DidNotReceive().Segment(Arg.Any<string>());
    }

    [Fact]
    public void TakeCompletedSentences_WithPunctuation_SegmentsAndReturnsCompleted()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Hello world. How are").Returns("Hello world. How are");
        _segmenter
            .Segment("Hello world. How are")
            .Returns(new List<string> { "Hello world.", "How are" });

        acc.Append("Hello world. How are");
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
        Assert.Equal("Hello world.", result[0]);
    }

    [Fact]
    public void TakeCompletedSentences_RetainsIncompleteLastSentence()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Hello world. How").Returns("Hello world. How");
        _segmenter
            .Segment("Hello world. How")
            .Returns(new List<string> { "Hello world.", "How" });

        acc.Append("Hello world. How");
        acc.TakeCompletedSentences();

        _normalizer.Normalize("How are you").Returns("How are you");
        _segmenter.Segment("How are you").Returns(new List<string> { "How are you" });

        acc.Append(" are you");
        var result = acc.TakeCompletedSentences();

        Assert.Empty(result);
    }

    [Fact]
    public void TakeCompletedSentences_MultipleSentences_ReturnsAllButLast()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("A. B. C").Returns("A. B. C");
        _segmenter.Segment("A. B. C").Returns(new List<string> { "A.", "B.", "C" });

        acc.Append("A. B. C");
        var result = acc.TakeCompletedSentences();

        Assert.Equal(2, result.Count);
        Assert.Equal("A.", result[0]);
        Assert.Equal("B.", result[1]);
    }

    [Fact]
    public void TakeCompletedSentences_SingleCompletedSentence_IsReleasedImmediately()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Hello world.").Returns("Hello world.");
        _segmenter.Segment("Hello world.").Returns(new List<string> { "Hello world." });

        acc.Append("Hello world.");
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
        Assert.Equal("Hello world.", result[0]);
    }

    [Fact]
    public void TakeCompletedSentences_CompleteSentenceFollowedByPartial_ReleasesOnlyComplete()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Done. And then").Returns("Done. And then");
        _segmenter
            .Segment("Done. And then")
            .Returns(new List<string> { "Done.", "And then" });

        acc.Append("Done. And then");
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
        Assert.Equal("Done.", result[0]);
    }

    [Fact]
    public void TakeCompletedSentences_OrdinalPeriod_IsNotTreatedAsComplete()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Section 3.").Returns("Section 3.");
        _segmenter.Segment("Section 3.").Returns(new List<string> { "Section 3." });

        acc.Append("Section 3.");
        var result = acc.TakeCompletedSentences();

        Assert.Empty(result);
    }

    [Fact]
    public void TakeCompletedSentences_AbbreviationPeriod_IsNotTreatedAsComplete()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("I met Dr.").Returns("I met Dr.");
        _segmenter.Segment("I met Dr.").Returns(new List<string> { "I met Dr." });

        acc.Append("I met Dr.");
        var result = acc.TakeCompletedSentences();

        Assert.Empty(result);
    }

    [Fact]
    public void TakeCompletedSentences_LongCjkClauseWithoutTerminator_FlushesAtComma()
    {
        var acc = CreateAccumulator();
        const string text = "今天天气真的非常不错，我们大家一起去公园走一走吧";

        acc.Append(text);
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
        Assert.EndsWith("，", result[0]);
        Assert.True(result[0].Length < text.Length);
    }

    [Fact]
    public void TakeCompletedSentences_ShortTextWithoutTerminator_BuffersWithoutFlushing()
    {
        var acc = CreateAccumulator();

        acc.Append("今天天气不错");
        var result = acc.TakeCompletedSentences();

        Assert.Empty(result);
    }

    [Fact]
    public void TakeCompletedSentences_StreamingEmotionTag_IsNotSplitAtColon()
    {
        var acc = CreateAccumulator();

        // The LLM streams the tag token by token; the ':' must not be treated as a
        // sentence boundary that would leak "[EMOTION:" into synthesis.
        acc.Append("[EMOTION:");
        Assert.Empty(acc.TakeCompletedSentences());

        acc.Append("😄]");
        Assert.Empty(acc.TakeCompletedSentences());

        _normalizer.Normalize("[EMOTION:😄] Hello world.").Returns("[EMOTION:😄] Hello world.");
        _segmenter
            .Segment("[EMOTION:😄] Hello world.")
            .Returns(new List<string> { "[EMOTION:😄] Hello world." });

        acc.Append(" Hello world.");
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
        Assert.Equal("[EMOTION:😄] Hello world.", result[0]);
    }

    [Fact]
    public void TakeCompletedSentences_TagOnlyBuffer_WaitsForSpeakableText()
    {
        var acc = CreateAccumulator();

        acc.Append("[Aria][EMOTION:😄]");
        var result = acc.TakeCompletedSentences();

        Assert.Empty(result);
        _normalizer.DidNotReceive().Normalize(Arg.Any<string>());
    }

    [Fact]
    public void TakeCompletedSentences_LongTextAfterTag_KeepsTagWithItsSentence()
    {
        var acc = CreateAccumulator();

        acc.Append("[EMOTION:😄] 今天天气真的非常非常不错哦");
        var result = acc.TakeCompletedSentences();

        Assert.Empty(result);
    }

    [Fact]
    public void Flush_ReturnsRemainingText()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Hello world").Returns("Hello world");
        _segmenter.Segment("Hello world").Returns(new List<string> { "Hello world" });

        acc.Append("Hello world");
        var result = acc.Flush();

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void Flush_EmptyBuffer_ReturnsNull()
    {
        var acc = CreateAccumulator();

        var result = acc.Flush();

        Assert.Null(result);
    }

    [Fact]
    public void Flush_WhitespaceOnly_ReturnsNull()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("   ").Returns("   ");
        _segmenter.Segment("   ").Returns(new List<string> { "   " });

        acc.Append("   ");
        var result = acc.Flush();

        Assert.Null(result);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var acc = CreateAccumulator();
        acc.Append("Hello world");

        acc.Reset();
        var result = acc.Flush();

        Assert.Null(result);
    }

    [Fact]
    public void Append_SemicolonTriggersPunctuation()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("first; second").Returns("first; second");
        _segmenter.Segment("first; second").Returns(new List<string> { "first;", "second" });

        acc.Append("first; second");
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
        _normalizer.Received(1).Normalize(Arg.Any<string>());
    }

    [Fact]
    public void Append_QuestionMarkTriggersPunctuation()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Really? Yes").Returns("Really? Yes");
        _segmenter.Segment("Really? Yes").Returns(new List<string> { "Really?", "Yes" });

        acc.Append("Really? Yes");
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
    }

    [Fact]
    public void Append_ExclamationMarkTriggersPunctuation()
    {
        var acc = CreateAccumulator();
        _normalizer.Normalize("Wow! Cool").Returns("Wow! Cool");
        _segmenter.Segment("Wow! Cool").Returns(new List<string> { "Wow!", "Cool" });

        acc.Append("Wow! Cool");
        var result = acc.TakeCompletedSentences();

        Assert.Single(result);
    }
}
