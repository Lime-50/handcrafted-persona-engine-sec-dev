using PersonaEngine.Lib.ASR.Transcriber;
using PersonaEngine.Lib.Audio;
using PersonaEngine.Lib.Configuration;
using Xunit;

namespace PersonaEngine.Lib.Tests.ASR.Transcriber;

public class RealtimeTranscriptorOptionsTests
{
    private static readonly RealtimeSpeechTranscriptorOptions Behavior = new()
    {
        AutodetectLanguageOnce = false,
        IncludeSpeechRecogizingEvents = false,
        RetrieveTokenDetails = false,
    };

    [Fact]
    public void ComposeOptions_Defaults_PreserveFixedEnglishBehavior()
    {
        var options = RealtimeTranscriptor.ComposeOptions(Behavior, new AsrConfiguration());

        Assert.False(options.LanguageAutoDetect);
        Assert.Equal("en-US", options.Language.Name);
        Assert.Equal(WhisperConfigTemplate.Performant, options.Template);
        Assert.Equal(string.Empty, options.Prompt);
    }

    [Fact]
    public void ComposeOptions_AppliesLanguagePromptAndTemplate()
    {
        var asr = new AsrConfiguration
        {
            Language = "zh-CN",
            LanguageAutoDetect = true,
            TtsPrompt = "Aria, Joobel",
            TtsMode = WhisperConfigTemplate.Precise,
        };

        var options = RealtimeTranscriptor.ComposeOptions(Behavior, asr);

        Assert.True(options.LanguageAutoDetect);
        Assert.Equal("zh-CN", options.Language.Name);
        Assert.Equal("Aria, Joobel", options.Prompt);
        Assert.Equal(WhisperConfigTemplate.Precise, options.Template);
    }

    [Fact]
    public void ComposeOptions_FallsBackToEnglishOnEmptyCulture()
    {
        var asr = new AsrConfiguration { Language = "" };

        var options = RealtimeTranscriptor.ComposeOptions(Behavior, asr);

        Assert.Equal("en-US", options.Language.Name);
    }

    [Fact]
    public void ComposeOptions_KeepsBehaviorFlags()
    {
        var behavior = Behavior with
        {
            AutodetectLanguageOnce = true,
            IncludeSpeechRecogizingEvents = true,
        };

        var options = RealtimeTranscriptor.ComposeOptions(behavior, new AsrConfiguration());

        Assert.True(options.AutodetectLanguageOnce);
        Assert.True(options.IncludeSpeechRecogizingEvents);
    }
}
