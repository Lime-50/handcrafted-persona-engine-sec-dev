using Hexa.NET.ImGui;
using Microsoft.Extensions.Options;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.Health;
using PersonaEngine.Lib.TTS.Synthesis.Doubao;
using PersonaEngine.Lib.UI.ControlPanel.Panels.Shared;
using PersonaEngine.Lib.UI.ControlPanel.Threading;

namespace PersonaEngine.Lib.UI.ControlPanel.Panels.Voice.Sections;

/// <summary>
///     All voice tuning knobs, collapsed by default. Contents vary by active mode.
///     Clear exposes Kokoro phoneme/speed knobs; Expressive exposes Qwen3 sampling knobs.
///     Both expose RVC processing quality and the raw engine id.
/// </summary>
public sealed class AdvancedSection : IDisposable
{
    private readonly IOptionsMonitor<TtsConfiguration> _ttsOptions;
    private readonly IOptionsMonitor<RVCFilterOptions> _rvcOptions;
    private readonly IConfigWriter _configWriter;
    private readonly IDoubaoTtsConnectionProbe _doubaoProbe;
    private readonly IUiThreadDispatcher _dispatcher;

    private KokoroVoiceOptions _kokoro;
    private Qwen3TtsOptions _qwen3;
    private DoubaoTtsOptions _doubao;
    private RVCFilterOptions _rvc;
    private readonly IDisposable? _ttsSubscription;
    private readonly IDisposable? _rvcSubscription;

    private DoubaoTtsAuthMode _doubaoAuthMode = DoubaoTtsAuthMode.ApiKey;
    private string _doubaoAccessKeyBuffer = string.Empty;
    private string _doubaoApiKeyBuffer = string.Empty;
    private string _doubaoAppIdBuffer = string.Empty;
    private string _doubaoEndpointBuffer = string.Empty;
    private bool _doubaoProbeInFlight;
    private string _doubaoResourceIdBuffer = string.Empty;
    private string _doubaoVoiceIdBuffer = string.Empty;
    private DateTimeOffset? _lastDoubaoProbeTime;
    private bool _doubaoShowAccessKey;
    private bool _doubaoShowKey;
    private ProbeFooter.State _doubaoProbeFooterState;
    private SubsystemStatus _doubaoProbeStatus = new(SubsystemHealth.Unknown, "Not tested", null);
    private float _elapsed;

    private static readonly (string Label, DoubaoTtsAuthMode Value)[] DoubaoAuthModes =
    [
        ("API Key (new console)", DoubaoTtsAuthMode.ApiKey),
        ("App ID + Access Key (legacy)", DoubaoTtsAuthMode.AppIdAccessKey),
    ];

    private static readonly (string Label, string Value)[] DoubaoResourceIds =
    [
        ("TTS 2.0 characters", "seed-tts-2.0"),
        ("TTS 1.0 characters", "seed-tts-1.0"),
        ("TTS 1.0 concurrent", "seed-tts-1.0-concurr"),
        ("Voice clone 2.0", "seed-icl-2.0"),
        ("Voice clone 1.0", "seed-icl-1.0"),
        ("Voice clone 1.0 concurrent", "seed-icl-1.0-concurr"),
    ];

    private static readonly string[] DoubaoEmotions =
    [
        "None",
        "happy",
        "sad",
        "angry",
        "scare",
        "surprise",
        "sorry",
        "pleased",
        "tear",
        "narrator",
        "storytelling",
    ];

    private AnimatedFloat _britishKnob;
    private AnimatedFloat _trimKnob;
    private AnimatedFloat _greedyKnob;
    private AnimatedFloat _silencePenaltyKnob;
    private bool _initialized;
    private readonly ImGuiHelpers.CollapsibleState _collapseState = new();

    public AdvancedSection(
        IOptionsMonitor<TtsConfiguration> ttsOptions,
        IOptionsMonitor<RVCFilterOptions> rvcOptions,
        IConfigWriter configWriter,
        IDoubaoTtsConnectionProbe doubaoProbe,
        IUiThreadDispatcher dispatcher
    )
    {
        _ttsOptions = ttsOptions;
        _rvcOptions = rvcOptions;
        _configWriter = configWriter;
        _doubaoProbe = doubaoProbe;
        _dispatcher = dispatcher;

        var current = ttsOptions.CurrentValue;
        _kokoro = current.Kokoro;
        _qwen3 = current.Qwen3;
        _doubao = current.Doubao;
        _rvc = rvcOptions.CurrentValue;
        SyncDoubaoTtsBuffers();

        _ttsSubscription = ttsOptions.OnChange(
            (updated, _) =>
            {
                _kokoro = updated.Kokoro;
                _qwen3 = updated.Qwen3;
                _doubao = updated.Doubao;
                SyncDoubaoTtsBuffers();
            }
        );
        _rvcSubscription = rvcOptions.OnChange((updated, _) => _rvc = updated);
    }

    public void Dispose()
    {
        _ttsSubscription?.Dispose();
        _rvcSubscription?.Dispose();
    }

    public void Render(float dt, VoiceMode mode)
    {
        _dispatcher.DrainPending();
        _elapsed += dt;

        if (!_initialized)
        {
            _britishKnob = new AnimatedFloat(_kokoro.UseBritishEnglish ? 1f : 0f);
            _trimKnob = new AnimatedFloat(_kokoro.TrimSilence ? 1f : 0f);
            _greedyKnob = new AnimatedFloat(_qwen3.CodePredictorGreedy ? 1f : 0f);
            _silencePenaltyKnob = new AnimatedFloat(_qwen3.SilencePenaltyEnabled ? 1f : 0f);
            _initialized = true;
        }

        ImGuiHelpers.CollapsibleSection(
            "Advanced",
            subtitle: null,
            defaultOpen: false,
            () => RenderBody(dt, mode),
            animState: _collapseState,
            dt: dt
        );
    }

    private void RenderBody(float dt, VoiceMode mode)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
        ImGui.TextUnformatted(
            "These settings can degrade voice quality. Change only if you know what you're doing."
        );
        ImGui.PopStyleColor();
        ImGui.Spacing();

        switch (mode)
        {
            case VoiceMode.Clear:
                RenderKokoroSettings(dt);
                break;

            case VoiceMode.Expressive:
                RenderQwen3Settings(dt);
                break;

            default:
                RenderDoubaoSettings(dt);
                break;
        }

        RenderRvcSettings();

        // Raw engine id — useful for scripting / bug reports
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextTertiary);
        ImGui.TextUnformatted($"Engine: {_ttsOptions.CurrentValue.ActiveEngine}");
        ImGui.PopStyleColor();
    }

    // ── Clear mode (Kokoro) ─────────────────────────────────────────────────

    private void RenderKokoroSettings(float dt)
    {
        float rowY;

        // Pace
        rowY = ImGui.GetCursorPosY();
        var speed = _kokoro.DefaultSpeed;
        ImGuiHelpers.SettingLabel("Pace", "Speaking speed multiplier (1.0 = normal).");
        if (ImGuiHelpers.LabeledSlider("##pace", ref speed, 0.5f, 2.0f, "Slow", "Fast", "%.2f", dt))
        {
            _kokoro = _kokoro with { DefaultSpeed = speed };
            _configWriter.Write(_kokoro);
        }
        ImGuiHelpers.SettingEndRow(rowY);

        // British English
        rowY = ImGui.GetCursorPosY();
        var british = _kokoro.UseBritishEnglish;
        ImGuiHelpers.SettingLabel("British English", "Use British English phoneme variants.");
        if (ImGuiHelpers.ToggleSwitch("##british", ref british, ref _britishKnob, dt))
        {
            _kokoro = _kokoro with { UseBritishEnglish = british };
            _configWriter.Write(_kokoro);
        }
        ImGuiHelpers.SettingEndRow(rowY);

        // Trim Silence
        rowY = ImGui.GetCursorPosY();
        var trim = _kokoro.TrimSilence;
        ImGuiHelpers.SettingLabel(
            "Trim Silence",
            "Strip leading/trailing silence from each audio segment."
        );
        if (ImGuiHelpers.ToggleSwitch("##trim", ref trim, ref _trimKnob, dt))
        {
            _kokoro = _kokoro with { TrimSilence = trim };
            _configWriter.Write(_kokoro);
        }
        ImGuiHelpers.SettingEndRow(rowY);

        // Max Segment Length
        rowY = ImGui.GetCursorPosY();
        var maxLen = _kokoro.MaxPhonemeLength;
        ImGuiHelpers.SettingLabel(
            "Max Segment Length",
            "Maximum phoneme length per synthesis segment (100\u2013510)."
        );
        if (ImGui.SliderInt("##maxlen", ref maxLen, 100, 510))
        {
            _kokoro = _kokoro with { MaxPhonemeLength = maxLen };
            _configWriter.Write(_kokoro);
        }
        ImGuiHelpers.SliderGlow(dt);
        ImGuiHelpers.SettingEndRow(rowY);
    }

    // ── Expressive mode (Qwen3) ─────────────────────────────────────────────

    private void RenderQwen3Settings(float dt)
    {
        float rowY;

        // Expressiveness (Temperature)
        rowY = ImGui.GetCursorPosY();
        var temperature = _qwen3.Temperature;
        ImGuiHelpers.SettingLabel(
            "Expressiveness",
            "Sampling temperature \u2014 higher values produce more varied, expressive output."
        );
        if (
            ImGuiHelpers.LabeledSlider(
                "##expressiveness",
                ref temperature,
                0.1f,
                1.5f,
                "Flat",
                "Theatrical",
                "%.2f",
                dt
            )
        )
        {
            _qwen3 = _qwen3 with { Temperature = temperature };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SettingEndRow(rowY);

        // Top-K
        rowY = ImGui.GetCursorPosY();
        var topK = _qwen3.TopK;
        ImGuiHelpers.SettingLabel("Top-K", "Number of highest-probability tokens to sample from.");
        if (ImGui.SliderInt("##topk", ref topK, 1, 100))
        {
            _qwen3 = _qwen3 with { TopK = topK };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SliderGlow(dt);
        ImGuiHelpers.SettingEndRow(rowY);

        // Top-P
        rowY = ImGui.GetCursorPosY();
        var topP = _qwen3.TopP;
        ImGuiHelpers.SettingLabel("Top-P", "Nucleus sampling threshold (0.0\u20131.0).");
        if (ImGui.SliderFloat("##topp", ref topP, 0.0f, 1.0f, "%.2f"))
        {
            _qwen3 = _qwen3 with { TopP = topP };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SliderGlow(dt);
        ImGuiHelpers.SettingEndRow(rowY);

        // Repetition Penalty
        rowY = ImGui.GetCursorPosY();
        var rep = _qwen3.RepetitionPenalty;
        ImGuiHelpers.SettingLabel(
            "Repetition Penalty",
            "Penalises repeated tokens to reduce stuttering (1.0\u20131.5)."
        );
        if (ImGui.SliderFloat("##rep", ref rep, 1.0f, 1.5f, "%.2f"))
        {
            _qwen3 = _qwen3 with { RepetitionPenalty = rep };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SliderGlow(dt);
        ImGuiHelpers.SettingEndRow(rowY);

        // Max Length
        rowY = ImGui.GetCursorPosY();
        var maxTok = _qwen3.MaxNewTokens;
        ImGuiHelpers.SettingLabel("Max Length", "Maximum tokens per utterance (256\u20134096).");
        if (ImGui.SliderInt("##maxtok", ref maxTok, 256, 4096))
        {
            _qwen3 = _qwen3 with { MaxNewTokens = maxTok };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SliderGlow(dt);
        ImGuiHelpers.SettingEndRow(rowY);

        // Streaming Granularity
        rowY = ImGui.GetCursorPosY();
        var emit = _qwen3.EmitEveryFrames;
        ImGuiHelpers.SettingLabel(
            "Stream Granularity",
            "Emit audio every N frames \u2014 lower = faster first audio, higher = smoother."
        );
        if (ImGui.SliderInt("##emit", ref emit, 1, 32))
        {
            _qwen3 = _qwen3 with { EmitEveryFrames = emit };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SliderGlow(dt);
        ImGuiHelpers.SettingEndRow(rowY);

        // Greedy Code Predictor
        rowY = ImGui.GetCursorPosY();
        var greedy = _qwen3.CodePredictorGreedy;
        ImGuiHelpers.SettingLabel(
            "Greedy Predictor",
            "Use argmax instead of sampling for spectral detail codes. May sound cleaner."
        );
        if (ImGuiHelpers.ToggleSwitch("##greedy", ref greedy, ref _greedyKnob, dt))
        {
            _qwen3 = _qwen3 with { CodePredictorGreedy = greedy };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SettingEndRow(rowY);

        // Silence Penalty
        rowY = ImGui.GetCursorPosY();
        var silPen = _qwen3.SilencePenaltyEnabled;
        ImGuiHelpers.SettingLabel(
            "Silence Penalty",
            "Penalise long silent tails to prevent dead air at the end of utterances."
        );
        if (ImGuiHelpers.ToggleSwitch("##silpen", ref silPen, ref _silencePenaltyKnob, dt))
        {
            _qwen3 = _qwen3 with { SilencePenaltyEnabled = silPen };
            _configWriter.Write(_qwen3);
        }
        ImGuiHelpers.SettingEndRow(rowY);
    }

    // ── RVC (both modes) ────────────────────────────────────────────────────

    private void RenderDoubaoSettings(float dt)
    {
        float rowY;

        // API key — the one thing that gates the whole cloud engine.
        // Auth mode
        rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Auth mode",
            "Select the credential type from the Volcengine speech console."
        );
        var authModeLabel = _doubaoAuthMode == DoubaoTtsAuthMode.ApiKey
            ? DoubaoAuthModes[0].Label
            : DoubaoAuthModes[1].Label;
        if (ImGui.BeginCombo("##doubao_tts_auth_mode", authModeLabel))
        {
            foreach (var (label, value) in DoubaoAuthModes)
            {
                if (ImGui.Selectable(label, _doubaoAuthMode == value))
                {
                    _doubaoAuthMode = value;
                    CommitDoubao();
                }
            }

            ImGui.EndCombo();
        }

        ImGuiHelpers.SettingEndRow(rowY);

        if (_doubaoAuthMode == DoubaoTtsAuthMode.ApiKey)
        {
            rowY = ImGui.GetCursorPosY();
            ImGuiHelpers.SettingLabel(
                "API Key",
                "New-console API key sent as X-Api-Key."
            );
            ApiKeyRow.Render(
                "DoubaoTtsApiKey",
                ref _doubaoApiKeyBuffer,
                ref _doubaoShowKey,
                _doubaoEndpointBuffer,
                out var nextKey
            );
            if (nextKey is not null)
            {
                CommitDoubao();
            }

            ImGuiHelpers.SettingEndRow(rowY);
        }
        else
        {
            rowY = ImGui.GetCursorPosY();
            ImGuiHelpers.SettingLabel(
                "App ID",
                "Legacy-console App ID sent as X-Api-App-Id."
            );
            if (ImGui.InputText("##doubao_tts_app_id", ref _doubaoAppIdBuffer, 256))
            {
                CommitDoubao();
            }

            ImGuiHelpers.SettingEndRow(rowY);

            rowY = ImGui.GetCursorPosY();
            ImGuiHelpers.SettingLabel(
                "Access Key",
                "Legacy-console Access Key sent as X-Api-Access-Key."
            );
            ApiKeyRow.Render(
                "DoubaoTtsAccessKey",
                ref _doubaoAccessKeyBuffer,
                ref _doubaoShowAccessKey,
                _doubaoEndpointBuffer,
                out var nextAccessKey
            );
            if (nextAccessKey is not null)
            {
                CommitDoubao();
            }

            ImGuiHelpers.SettingEndRow(rowY);
        }

        // Resource ID
        rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Resource ID",
            "Purchased TTS product. It must match the selected voice family."
        );
        var currentResource = string.IsNullOrWhiteSpace(_doubaoResourceIdBuffer)
            ? "seed-tts-2.0"
            : _doubaoResourceIdBuffer;
        var resourceLabel = currentResource;
        foreach (var (label, value) in DoubaoResourceIds)
        {
            if (string.Equals(value, currentResource, StringComparison.OrdinalIgnoreCase))
            {
                resourceLabel = label;
                break;
            }
        }

        if (ImGui.BeginCombo("##doubao_tts_resource_id", resourceLabel))
        {
            foreach (var (label, value) in DoubaoResourceIds)
            {
                if (
                    ImGui.Selectable(
                        $"{label} ({value})",
                        string.Equals(value, currentResource, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    _doubaoResourceIdBuffer = value;
                    CommitDoubao();
                }
            }

            ImGui.EndCombo();
        }

        ImGuiHelpers.SettingEndRow(rowY);

        // Voice ID (speaker) 鈥?free-form so purchased / cloned voices can be used even
        // when they are not in the bundled voice catalog. The Voice panel gallery writes
        // this same field, so the two stay in sync.
        rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Voice ID",
            "音色 ID sent as \"speaker\" — e.g. zh_female_shuangkuaisisi_uranus_bigtts, "
                + "or the S_... id of a cloned voice from the Volcengine console."
        );
        if (ImGui.InputText("##doubao_tts_voice_id", ref _doubaoVoiceIdBuffer, 256))
        {
            CommitDoubao();
        }

        if (string.IsNullOrWhiteSpace(_doubaoVoiceIdBuffer))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
            ImGui.TextUnformatted($"Blank — using the gallery voice: {_doubao.DefaultVoice}");
            ImGui.PopStyleColor();
        }

        ImGuiHelpers.SettingEndRow(rowY);

        // Endpoint
        rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Endpoint",
            "TTS SSE endpoint. Override only when using a proxy or gateway."
        );
        if (ImGui.InputText("##doubao_tts_endpoint", ref _doubaoEndpointBuffer, 512))
        {
            CommitDoubao();
        }

        ImGuiHelpers.SettingEndRow(rowY);

        // Speech rate
        rowY = ImGui.GetCursorPosY();
        var speechRate = _doubao.SpeechRate;
        ImGuiHelpers.SettingLabel(
            "Speech Rate",
            "Pace adjustment (-50 = half speed, 0 = default, 100 = double speed)."
        );
        if (
            ImGuiHelpers.LabeledSlider(
                "##doubao_speech_rate",
                ref speechRate,
                -50,
                100,
                "Slow",
                "Fast",
                dt
            )
        )
        {
            _doubao = _doubao with { SpeechRate = speechRate };
            _configWriter.Write(_doubao);
        }
        ImGuiHelpers.SettingEndRow(rowY);

        // Loudness
        rowY = ImGui.GetCursorPosY();
        var loudness = _doubao.LoudnessRate;
        ImGuiHelpers.SettingLabel(
            "Loudness",
            "Volume adjustment (-50 = half volume, 0 = default, 100 = double volume)."
        );
        if (
            ImGuiHelpers.LabeledSlider(
                "##doubao_loudness",
                ref loudness,
                -50,
                100,
                "Quiet",
                "Loud",
                dt
            )
        )
        {
            _doubao = _doubao with { LoudnessRate = loudness };
            _configWriter.Write(_doubao);
        }
        ImGuiHelpers.SettingEndRow(rowY);

        // Emotion
        rowY = ImGui.GetCursorPosY();
        var currentEmotion = _doubao.Emotion;
        var emotionIndex = currentEmotion is null
            ? 0
            : Array.IndexOf(DoubaoEmotions, currentEmotion);
        if (emotionIndex < 0)
        {
            emotionIndex = 0;
        }

        ImGuiHelpers.SettingLabel("Emotion", "Emotional tone (only supported by some voices).");
        if (ImGui.Combo("##doubao_emotion", ref emotionIndex, DoubaoEmotions, DoubaoEmotions.Length))
        {
            _doubao = _doubao with
            {
                Emotion = emotionIndex == 0 ? null : DoubaoEmotions[emotionIndex],
            };
            _configWriter.Write(_doubao);
        }
        ImGuiHelpers.HandCursorOnHover();
        ImGuiHelpers.SettingEndRow(rowY);

        ImGui.Spacing();
        RenderDoubaoProbe(dt);
    }

    private void RenderDoubaoProbe(float dt)
    {
        var rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Connection",
            "Sends a short test phrase to validate credentials, resource ID, and voice."
        );
        SubsystemStatusChip.Render(_doubaoProbeStatus, _elapsed);
        ImGuiHelpers.SettingEndRow(rowY);

        if (
            _doubaoProbeStatus.Health == SubsystemHealth.Failed
            && !string.IsNullOrWhiteSpace(_doubaoProbeStatus.Detail)
        )
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
            ImGui.TextWrapped(_doubaoProbeStatus.Detail);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ProbeFooter.Render(
            ref _doubaoProbeFooterState,
            _lastDoubaoProbeTime,
            _doubaoProbeInFlight,
            dt,
            () => _ = RunDoubaoProbeAsync()
        );
    }

    private async Task RunDoubaoProbeAsync()
    {
        if (_doubaoProbeInFlight)
        {
            return;
        }

        _doubaoProbeInFlight = true;
        _doubaoProbeStatus = new SubsystemStatus(
            SubsystemHealth.Degraded,
            "Testing...",
            null
        );

        var snapshot = BuildDoubaoSettings();

        try
        {
            var result = await _doubaoProbe.ProbeAsync(snapshot);
            _dispatcher.Post(
                () =>
                {
                    _doubaoProbeStatus = result;
                    _lastDoubaoProbeTime = DateTimeOffset.UtcNow;
                    _doubaoProbeInFlight = false;
                }
            );
        }
        catch (Exception ex)
        {
            _dispatcher.Post(
                () =>
                {
                    _doubaoProbeStatus = new SubsystemStatus(
                        SubsystemHealth.Failed,
                        "Probe failed",
                        ex.Message
                    );
                    _lastDoubaoProbeTime = DateTimeOffset.UtcNow;
                    _doubaoProbeInFlight = false;
                }
            );
        }
    }

    private void CommitDoubao()
    {
        _doubao = BuildDoubaoSettings();
        _configWriter.Write(_doubao);
    }

    private DoubaoTtsOptions BuildDoubaoSettings() =>
        _doubao with
        {
            AuthMode = _doubaoAuthMode,
            ApiKey = _doubaoApiKeyBuffer,
            AppId = _doubaoAppIdBuffer,
            AccessKey = _doubaoAccessKeyBuffer,
            ResourceId = _doubaoResourceIdBuffer,
            Endpoint = _doubaoEndpointBuffer,
            // A blank field keeps the previously selected voice (gallery selection)
            // instead of sending an empty speaker id to the API.
            DefaultVoice = string.IsNullOrWhiteSpace(_doubaoVoiceIdBuffer)
                ? _doubao.DefaultVoice
                : _doubaoVoiceIdBuffer.Trim(),
        };

    private void SyncDoubaoTtsBuffers()
    {
        _doubaoAuthMode = _doubao.AuthMode;
        _doubaoApiKeyBuffer = _doubao.ApiKey ?? string.Empty;
        _doubaoAppIdBuffer = _doubao.AppId ?? string.Empty;
        _doubaoAccessKeyBuffer = _doubao.AccessKey ?? string.Empty;
        _doubaoResourceIdBuffer = _doubao.ResourceId ?? string.Empty;
        _doubaoVoiceIdBuffer = _doubao.DefaultVoice ?? string.Empty;
        _doubaoEndpointBuffer = _doubao.Endpoint ?? string.Empty;
    }

    private void RenderRvcSettings()
    {
        var rowY = ImGui.GetCursorPosY();
        var hop = _rvc.HopSize;
        ImGuiHelpers.SettingLabel(
            "RVC Quality",
            "Hop size \u2014 smaller values are higher quality but slower (32\u2013256)."
        );
        if (ImGui.SliderInt("##hop", ref hop, 32, 256))
        {
            _rvc = _rvc with { HopSize = hop };
            _configWriter.Write(_rvc);
        }
        ImGuiHelpers.SettingEndRow(rowY);
    }
}
