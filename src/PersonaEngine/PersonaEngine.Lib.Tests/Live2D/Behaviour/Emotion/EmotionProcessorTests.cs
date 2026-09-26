using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonaEngine.Lib.Live2D.Behaviour.Emotion;
using Xunit;

namespace PersonaEngine.Lib.Tests.Live2D.Behaviour.Emotion;

public class EmotionProcessorTests
{
    private static EmotionProcessor CreateProcessor() =>
        new(Substitute.For<IEmotionService>(), NullLoggerFactory.Instance);

    [Fact]
    public async Task ProcessAsync_EmotionTag_IsRemovedFromSpokenText()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync("[EMOTION:😄] Hello there.");

        Assert.Equal(" Hello there.", result.ProcessedText);
    }

    [Fact]
    public async Task ProcessAsync_EmptyEmotionTag_IsStillRemoved()
    {
        var processor = CreateProcessor();

        // Regression: an empty value used to short-circuit before the tag was stripped,
        // so the raw "[EMOTION:]" marker was spoken aloud.
        var result = await processor.ProcessAsync("[EMOTION:] Hello there.");

        Assert.Equal(" Hello there.", result.ProcessedText);
        Assert.DoesNotContain("EMOTION", result.ProcessedText);
    }

    [Fact]
    public async Task ProcessAsync_LowercaseEmotionTag_IsRemoved()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync("[emotion:😄] Hello there.");

        Assert.Equal(" Hello there.", result.ProcessedText);
    }

    [Fact]
    public async Task ProcessAsync_MultipleTags_RemovesAllAndKeepsCount()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync("[EMOTION:😄] Hi [EMOTION:🙄] there.");

        Assert.Equal(" Hi  there.", result.ProcessedText);
        Assert.True(result.Metadata.ContainsKey("Emotions"));
    }

    [Fact]
    public async Task ProcessAsync_TextWithoutTags_IsUnchanged()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync("Hello there.");

        Assert.Equal("Hello there.", result.ProcessedText);
        Assert.Empty(result.Metadata);
    }
}
