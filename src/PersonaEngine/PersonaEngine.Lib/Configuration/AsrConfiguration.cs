using Whisper.net;

namespace PersonaEngine.Lib.Configuration;

public record AsrConfiguration
{
    public WhisperConfigTemplate TtsMode { get; init; } = WhisperConfigTemplate.Performant;

    public string TtsPrompt { get; init; } = string.Empty;

    /// <summary>
    ///     Culture name fed to Whisper when <see cref="LanguageAutoDetect" /> is false
    ///     (e.g. "en-US", "zh-CN", "yue", "ja-JP"). Bound to "Config:Asr:Language".
    /// </summary>
    public string Language { get; init; } = "en-US";

    /// <summary>
    ///     When true, Whisper auto-detects the language of each utterance instead of
    ///     using <see cref="Language" />. Auto-detect requires a multilingual Whisper
    ///     model; the default Tiny EN model only transcribes English.
    /// </summary>
    public bool LanguageAutoDetect { get; init; } = false;

    public float VadThreshold { get; init; } = 0.5f;

    public float VadThresholdGap { get; init; } = 0.15f;

    public float VadMinSpeechDuration { get; init; } = 150f;

    public float VadMinSilenceDuration { get; init; } = 450f;
}

public enum WhisperConfigTemplate
{
    Performant,

    Balanced,

    Precise,
}

public static class WhisperConfigTemplateExtensions
{
    public static WhisperProcessorBuilder ApplyTemplate(
        this WhisperProcessorBuilder builder,
        WhisperConfigTemplate template
    )
    {
        switch (template)
        {
            case WhisperConfigTemplate.Performant:
                var sampleBuilderA = (GreedySamplingStrategyBuilder)
                    builder.WithGreedySamplingStrategy();
                sampleBuilderA.WithBestOf(1);

                builder = sampleBuilderA.ParentBuilder;
                builder.WithStringPool();

                break;
            case WhisperConfigTemplate.Balanced:
                var sampleBuilderB = (BeamSearchSamplingStrategyBuilder)
                    builder.WithBeamSearchSamplingStrategy();
                sampleBuilderB.WithBeamSize(2);
                sampleBuilderB.WithPatience(1f);

                builder = sampleBuilderB.ParentBuilder;
                builder.WithStringPool();
                builder.WithTemperature(0.0f);

                break;
            case WhisperConfigTemplate.Precise:
                var sampleBuilderC = (BeamSearchSamplingStrategyBuilder)
                    builder.WithBeamSearchSamplingStrategy();
                sampleBuilderC.WithBeamSize(5);
                sampleBuilderC.WithPatience(1f);

                builder = sampleBuilderC.ParentBuilder;
                builder.WithStringPool();
                builder.WithTemperature(0.0f);

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(template), template, null);
        }

        return builder;
    }
}
