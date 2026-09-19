using Whisper.net;

namespace PersonaEngine.Lib.Configuration;

public record AsrConfiguration
{
    /// <summary>
    ///     Which speech recognition engine is used for live transcription.
    /// </summary>
    public AsrProvider Provider { get; init; } = AsrProvider.Local;

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

    /// <summary>
    ///     Volcengine/Doubao big-model streaming ASR settings. Only used when
    ///     <see cref="Provider" /> is <see cref="AsrProvider.Doubao" />.
    /// </summary>
    public DoubaoAsrOptions Doubao { get; init; } = new();
}

public enum AsrProvider
{
    Local,

    Doubao,
}

public enum DoubaoAuthMode
{
    /// <summary>
    ///     New speech console: a single API key is sent as X-Api-Key.
    /// </summary>
    ApiKey,

    /// <summary>
    ///     Legacy speech console: App ID and Access Token are sent as
    ///     X-Api-App-Key and X-Api-Access-Key.
    /// </summary>
    AppIdToken,
}

public record DoubaoAsrOptions
{
    /// <summary>
    ///     WebSocket endpoint for the v3 big-model streaming ASR API.
    ///     Defaults to the optimized bidirectional streaming endpoint.
    /// </summary>
    public string Endpoint { get; init; } =
        "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async";

    /// <summary>
    ///     Authentication scheme used for the WebSocket handshake.
    /// </summary>
    public DoubaoAuthMode AuthMode { get; init; } = DoubaoAuthMode.ApiKey;

    /// <summary>
    ///     New-console API key sent as X-Api-Key.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    ///     Legacy-console App ID sent as X-Api-App-Key.
    /// </summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>
    ///     Legacy-console Access Token sent as X-Api-Access-Key.
    /// </summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>
    ///     Volcengine resource ID for the purchased ASR quota.
    ///     bigmodel_async uses Doubao Streaming ASR 2.0, whose hour-metered
    ///     resource is volc.seedasr.sauc.duration.
    /// </summary>
    public string ResourceId { get; init; } = "volc.seedasr.sauc.duration";

    /// <summary>
    ///     Optional request user identifier.
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    ///     Audio language, e.g. "zh-CN", "en-US", "ja-JP". Leave blank to use the
    ///     model's built-in language detection (bigmodel endpoint).
    /// </summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>
    ///     Audio chunk duration sent to the WebSocket, in milliseconds.
    /// </summary>
    public int PacketDurationMs { get; init; } = 200;

    /// <summary>
    ///     Response granularity: "single" returns the current recognized text,
    ///     while "full" may include additional metadata.
    /// </summary>
    public string ResultType { get; init; } = "single";

    public bool ShowUtterances { get; init; } = true;

    public bool EnablePunctuation { get; init; } = true;

    public bool EnableItn { get; init; } = true;

    public bool EnableDdc { get; init; } = false;
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
