namespace PersonaEngine.Lib.Configuration;

public enum DoubaoTtsAuthMode
{
    /// <summary>New console: a single API key is sent as X-Api-Key.</summary>
    ApiKey,

    /// <summary>Legacy console: App ID and Access Key are sent together.</summary>
    AppIdAccessKey,
}

/// <summary>
///     Configuration for the Doubao (Volcengine) speech synthesis engine.
///     Bound to "Config:Tts:Doubao" in appsettings.json.
/// </summary>
/// <remarks>
///     Uses the "豆包语音合成 V3 HTTP SSE 单向流式" API:
///     POST https://openspeech.bytedance.com/api/v3/tts/unidirectional/sse
///     Authentication prefers the new-console API key (X-Api-Key); the legacy
///     console AppId + AccessKey pair is used as a fallback when ApiKey is empty.
/// </remarks>
public sealed record DoubaoTtsOptions
{
    /// <summary>Authentication scheme used for the TTS HTTP request.</summary>
    public DoubaoTtsAuthMode AuthMode { get; init; } = DoubaoTtsAuthMode.ApiKey;

    /// <summary>New console API key (X-Api-Key). Preferred authentication.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Legacy console App ID (X-Api-App-Id). Used when ApiKey is empty.</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>Legacy console Access Token (X-Api-Access-Key). Used when ApiKey is empty.</summary>
    public string AccessKey { get; init; } = string.Empty;

    /// <summary>
    ///     Model resource id (X-Api-Resource-Id). seed-tts-2.0 only accepts
    ///     *_uranus_bigtts voices; seed-tts-1.0 / seed-tts-1.0-concurr only
    ///     accept the legacy BV*_streaming voices.
    /// </summary>
    public string ResourceId { get; init; } = "seed-tts-2.0";

    /// <summary>Default voice id (speaker). See the Volcengine voice list.</summary>
    public string DefaultVoice { get; init; } = "zh_female_shuangkuaisisi_uranus_bigtts";

    /// <summary>API endpoint. Override only when using a proxy/gateway.</summary>
    public string Endpoint { get; init; } =
        "https://openspeech.bytedance.com/api/v3/tts/unidirectional/sse";

    /// <summary>
    ///     Output encoding requested from the API: "pcm" (raw s16le, lowest latency)
    ///     or "mp3". pcm is recommended for the streaming conversation pipeline.
    /// </summary>
    public string Format { get; init; } = "pcm";

    /// <summary>Audio sample rate. Must match one of 8000/16000/22050/24000/32000/44100/48000.</summary>
    public int SampleRate { get; init; } = 24000;

    /// <summary>Speech rate, [-50, 100]. 0 is the default pace, 100 is 2.0x.</summary>
    public int SpeechRate { get; init; } = 0;

    /// <summary>Loudness, [-50, 100]. 0 is the default volume, 100 is 2.0x.</summary>
    public int LoudnessRate { get; init; } = 0;

    /// <summary>Optional emotion hint (e.g. happy/sad/angry). Only supported by some voices.</summary>
    public string? Emotion { get; init; }

    /// <summary>Emotion intensity 1..5 (used together with <see cref="Emotion" />).</summary>
    public int EmotionScale { get; init; } = 4;

    /// <summary>
    ///     Request word-level timestamps for TTS 1.0 voices (enable_timestamp).
    ///     Auto-applied only when the selected voice is a 1.0 (BV*) voice.
    /// </summary>
    public bool EnableTimestamp { get; init; } = false;

    /// <summary>
    ///     Request word-level subtitle timestamps for TTS 2.0 voices (enable_subtitle).
    ///     Auto-applied only when the selected voice is a 2.0 (*_uranus_bigtts) voice.
    /// </summary>
    public bool EnableSubtitle { get; init; } = true;

    /// <summary>Optional explicit language hint (explicit_language), e.g. "zh-cn" or "en".</summary>
    public string? Language { get; init; }

    /// <summary>User id sent in the request for tracking (user.uid).</summary>
    public string Uid { get; init; } = "persona-engine";

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}
