namespace PersonaEngine.Lib.UI.ControlPanel.Panels.Voice;

/// <summary>
///     UI-layer mode over the underlying TTS engine id. Purely an affordance — the
///     persisted state is <c>TtsConfiguration.ActiveEngine</c>.
/// </summary>
public enum VoiceMode
{
    /// <summary>Kokoro — phoneme-accurate, light on GPU, emotionally flat.</summary>
    Clear,

    /// <summary>Qwen3 — context-aware emotion and intonation, heavy on GPU.</summary>
    Expressive,

    /// <summary>Doubao — Volcengine cloud TTS, natural Chinese/English, needs an API key.</summary>
    Doubao,
}

public static class VoiceModeMapping
{
    public static VoiceMode FromEngineId(string engineId) =>
        engineId?.ToLowerInvariant() switch
        {
            "qwen3" => VoiceMode.Expressive,
            "doubao" => VoiceMode.Doubao,
            _ => VoiceMode.Clear,
        };

    public static string ToEngineId(VoiceMode mode) =>
        mode switch
        {
            VoiceMode.Expressive => "qwen3",
            VoiceMode.Doubao => "doubao",
            _ => "kokoro",
        };
}
