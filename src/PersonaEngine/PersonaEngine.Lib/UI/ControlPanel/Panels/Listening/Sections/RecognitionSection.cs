using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Microsoft.Extensions.Options;
using PersonaEngine.Lib.ASR.Transcriber.Doubao;
using PersonaEngine.Lib.Assets;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.Health;
using PersonaEngine.Lib.UI.ControlPanel.Layout;
using PersonaEngine.Lib.UI.ControlPanel.Panels.Shared;
using PersonaEngine.Lib.UI.ControlPanel.Threading;

namespace PersonaEngine.Lib.UI.ControlPanel.Panels.Listening.Sections;

/// <summary>
///     Recognition card: spoken-language picker, Whisper decoder preset
///     (Fast/Balanced/Accurate), and a chip-based Custom Vocabulary editor backed by
///     <see cref="AsrConfiguration.TtsPrompt" />.
/// </summary>
public sealed class RecognitionSection : IDisposable
{
    private const int InputBufferSize = 128;
    private const int MaxVocabulary = 30;
    private const float ChipGap = 6f;
    private const float ChipPaddingX = 10f;
    private const float ChipPaddingY = 4f;
    private const float ChipRounding = 12f;
    private const float ChipCloseSize = 14f;
    private const float ChipCloseGap = 6f;

    private static readonly (string Label, WhisperConfigTemplate Value)[] QualityOptions =
    {
        ("Fast", WhisperConfigTemplate.Performant),
        ("Balanced", WhisperConfigTemplate.Balanced),
        ("Accurate", WhisperConfigTemplate.Precise),
    };

    private static readonly (string Label, AsrProvider Value)[] ProviderOptions =
    {
        ("Local Whisper", AsrProvider.Local),
        ("Doubao API", AsrProvider.Doubao),
    };

    private static readonly (string Label, DoubaoAuthMode Value)[] AuthModeOptions =
    {
        ("API Key (new console)", DoubaoAuthMode.ApiKey),
        ("App ID + Access Token (legacy)", DoubaoAuthMode.AppIdToken),
    };

    private readonly IConfigWriter _configWriter;
    private readonly IAssetCatalog _catalog;
    private readonly IDisposable? _changeSubscription;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly IDoubaoConnectionProbe _doubaoProbe;

    // Pre-built "##vocab_{i}" ids up to MaxVocabulary so the per-chip
    // InvisibleButton doesn't interpolate a fresh string every frame.
    private static readonly string[] VocabChipIds = BuildVocabChipIds();

    private readonly record struct LanguageOption(string DisplayName, string? CultureName)
    {
        public bool IsAuto => CultureName is null;
    }

    // Curated subset of WhisperNetSupportedLanguage. ASCII labels keep the combo
    // readable with the default ImGui font (no CJK glyphs).
    private static readonly LanguageOption[] LanguageOptions =
    [
        new("Auto detect", null),
        new("English (US)", "en-US"),
        new("Chinese (Simplified)", "zh-CN"),
        new("Chinese (Cantonese)", "yue"),
        new("Japanese", "ja-JP"),
        new("Korean", "ko-KR"),
        new("Spanish", "es-ES"),
        new("French", "fr-FR"),
        new("German", "de-DE"),
        new("Russian", "ru-RU"),
        new("Portuguese (Brazil)", "pt-BR"),
        new("Italian", "it-IT"),
        new("Thai", "th-TH"),
        new("Vietnamese", "vi-VN"),
        new("Indonesian", "id-ID"),
        new("Hindi", "hi-IN"),
        new("Arabic", "ar-SA"),
    ];

    private AsrConfiguration _asr;
    private float _elapsed;
    private string _doubaoAccessTokenBuf = string.Empty;
    private string _doubaoApiKeyBuf = string.Empty;
    private string _doubaoAppIdBuf = string.Empty;
    private DoubaoAuthMode _doubaoAuthMode = DoubaoAuthMode.ApiKey;
    private bool _doubaoEnableDdc;
    private bool _doubaoEnableItn = true;
    private bool _doubaoEnablePunctuation = true;
    private string _doubaoEndpointBuf = string.Empty;
    private int _doubaoPacketDurationMs = 200;
    private string _doubaoResourceIdBuf = string.Empty;
    private bool _doubaoShowAccessToken;
    private bool _doubaoShowKey;
    private bool _doubaoShowUtterances = true;
    private string _inputBuffer = string.Empty;
    private DateTimeOffset? _lastDoubaoProbeTime;
    private ProbeFooter.State _probeFooterState;
    private bool _probeInFlight;
    private SubsystemStatus _probeStatus = new(SubsystemHealth.Unknown, "Not tested", null);
    private List<string> _vocabulary = new();

    private static string[] BuildVocabChipIds()
    {
        var ids = new string[MaxVocabulary];
        for (var i = 0; i < MaxVocabulary; i++)
        {
            ids[i] = $"##vocab_{i}";
        }
        return ids;
    }

    public RecognitionSection(
        IOptionsMonitor<AsrConfiguration> monitor,
        IConfigWriter configWriter,
        IAssetCatalog catalog,
        IUiThreadDispatcher dispatcher,
        IDoubaoConnectionProbe doubaoProbe
    )
    {
        _configWriter = configWriter;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _doubaoProbe = doubaoProbe;
        _asr = monitor.CurrentValue;
        _vocabulary = ParseVocabulary(_asr.TtsPrompt);
        SyncDoubaoBuffers();
        _changeSubscription = monitor.OnChange(
            (updated, _) =>
            {
                _asr = updated;
                _vocabulary = ParseVocabulary(updated.TtsPrompt);
                SyncDoubaoBuffers();
            }
        );
    }

    public void Dispose() => _changeSubscription?.Dispose();

    public void Render(float dt)
    {
        _dispatcher.DrainPending();
        _elapsed += dt;

        using (Ui.Card("##recognition", padding: 12f))
        {
            RenderHeader();
            RenderProvider();
            ImGui.Spacing();
            RenderLanguage();
            ImGui.Spacing();

            if (_asr.Provider == AsrProvider.Doubao)
            {
                RenderDoubao(dt);
            }
            else
            {
                RenderQuality();
                ImGui.Spacing();
                RenderVocabulary();
            }
        }
    }

    private static void RenderHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextTertiary);
        ImGui.TextUnformatted("Recognition");
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
        ImGui.TextUnformatted("How we turn your speech into text for the avatar");
        ImGui.PopStyleColor();

        ImGui.Spacing();
    }

    private void RenderLanguage()
    {
        var isDoubao = _asr.Provider == AsrProvider.Doubao;
        var languageName = isDoubao ? _asr.Doubao.Language : _asr.Language;
        var autoDetect = isDoubao
            ? string.IsNullOrWhiteSpace(languageName)
            : _asr.LanguageAutoDetect;

        ImGuiHelpers.SettingLabel(
            "Language",
            "What the avatar listens for. Auto detect lets the engine decide per "
                + "utterance; a fixed language is more consistent."
        );

        var preview = autoDetect
            ? LanguageOptions[0].DisplayName
            : GetLanguageDisplayName(languageName);

        if (ImGui.BeginCombo("##asr_language", preview))
        {
            foreach (var option in LanguageOptions)
            {
                var isSelected = option.IsAuto
                    ? autoDetect
                    : !autoDetect
                        && string.Equals(
                            option.CultureName,
                            languageName,
                            StringComparison.OrdinalIgnoreCase
                        );

                if (ImGui.Selectable(option.DisplayName, isSelected))
                {
                    var updated = option.IsAuto
                        ? isDoubao
                            ? _asr with
                            {
                                Doubao = _asr.Doubao with { Language = string.Empty },
                            }
                            : _asr with { LanguageAutoDetect = true }
                        : isDoubao
                            ? _asr with
                            {
                                Doubao = _asr.Doubao with
                                {
                                    Language = option.CultureName!,
                                },
                            }
                            : _asr with
                            {
                                Language = option.CultureName!,
                                LanguageAutoDetect = false,
                            };

                    if (updated != _asr)
                    {
                        _asr = updated;
                        _configWriter.Write(_asr);
                    }
                }
            }

            ImGui.EndCombo();
        }

        ImGuiHelpers.HandCursorOnHover();
        RenderLanguageModelWarning();
    }

    private void RenderLanguageModelWarning()
    {
        if (_asr.Provider != AsrProvider.Local)
        {
            return;
        }

        // The default profile ships Whisper Tiny EN, which only transcribes English.
        // Auto-detect is useless on it too: Whisper still forces English output.
        if (_catalog.IsFeatureEnabled(FeatureIds.AsrAccurate))
        {
            return;
        }

        var needsMultilingual = _asr.LanguageAutoDetect || !IsEnglishOnlyCulture(_asr.Language);
        if (!needsMultilingual)
        {
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
        ImGui.TextWrapped(
            "The installed Whisper model is English-only (Tiny EN). Install the BuildWithIt "
                + "profile to download the multilingual Whisper Turbo model, otherwise this "
                + "language won't transcribe correctly."
        );
        ImGui.PopStyleColor();
    }

    private void RenderProvider()
    {
        ImGuiHelpers.SettingLabel(
            "Engine",
            "Choose whether speech is transcribed locally with Whisper or through "
                + "the Volcengine/Doubao streaming ASR API."
        );

        for (var i = 0; i < ProviderOptions.Length; i++)
        {
            var (label, value) = ProviderOptions[i];
            if (ImGuiHelpers.Chip(label, _asr.Provider == value))
            {
                _asr = _asr with { Provider = value };
                _configWriter.Write(_asr);
            }

            if (i < ProviderOptions.Length - 1)
                ImGui.SameLine(0f, ChipGap);
        }
    }

    private void RenderDoubao(float dt)
    {
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextTertiary);
        ImGui.TextUnformatted("Doubao API");
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
        ImGui.TextUnformatted(
            "Volcengine big-model streaming ASR credentials and request options."
        );
        ImGui.PopStyleColor();

        ImGui.Spacing();

        var rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Endpoint",
            "WebSocket endpoint for the Doubao big-model ASR API."
        );
        if (ImGui.InputText("##doubao_endpoint", ref _doubaoEndpointBuf, 512))
        {
            CommitDoubao();
        }

        ImGuiHelpers.SettingEndRow(rowY);

        rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Auth mode",
            "Select the credential type from the Volcengine speech console."
        );
        var authModeLabel = _doubaoAuthMode == DoubaoAuthMode.ApiKey
            ? AuthModeOptions[0].Label
            : AuthModeOptions[1].Label;
        if (ImGui.BeginCombo("##doubao_auth_mode", authModeLabel))
        {
            foreach (var (label, value) in AuthModeOptions)
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

        if (_doubaoAuthMode == DoubaoAuthMode.ApiKey)
        {
            rowY = ImGui.GetCursorPosY();
            ImGuiHelpers.SettingLabel(
                "API Key",
                "New-console API key sent as X-Api-Key."
            );
            ApiKeyRow.Render(
                "DoubaoAsrKey",
                ref _doubaoApiKeyBuf,
                ref _doubaoShowKey,
                _doubaoEndpointBuf,
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
                "Legacy-console App ID sent as X-Api-App-Key."
            );
            if (ImGui.InputText("##doubao_app_id", ref _doubaoAppIdBuf, 256))
            {
                CommitDoubao();
            }

            ImGuiHelpers.SettingEndRow(rowY);

            rowY = ImGui.GetCursorPosY();
            ImGuiHelpers.SettingLabel(
                "Access Token",
                "Legacy-console Access Token sent as X-Api-Access-Key."
            );
            ApiKeyRow.Render(
                "DoubaoAsrAccessToken",
                ref _doubaoAccessTokenBuf,
                ref _doubaoShowAccessToken,
                _doubaoEndpointBuf,
                out var nextAccessToken
            );
            if (nextAccessToken is not null)
            {
                CommitDoubao();
            }

            ImGuiHelpers.SettingEndRow(rowY);
        }

        rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Resource ID",
            "Purchased Volcengine ASR resource ID. Doubao Streaming ASR 2.0 "
                + "hour-metered uses volc.seedasr.sauc.duration."
        );
        if (ImGui.InputText("##doubao_resource_id", ref _doubaoResourceIdBuf, 256))
        {
            CommitDoubao();
        }

        ImGuiHelpers.SettingEndRow(rowY);

        rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Packet",
            "Audio duration sent to the WebSocket per binary frame."
        );
        if (ImGui.SliderInt("##doubao_packet", ref _doubaoPacketDurationMs, 50, 1000, "%d ms"))
        {
            CommitDoubao();
        }

        ImGuiHelpers.SettingEndRow(rowY);

        RenderDoubaoCheckbox("Show utterances", "Emit per-utterance results when available.", ref _doubaoShowUtterances);
        RenderDoubaoCheckbox("Punctuation", "Enable punctuation prediction.", ref _doubaoEnablePunctuation);
        RenderDoubaoCheckbox("ITN", "Enable inverse text normalization.", ref _doubaoEnableItn);
        RenderDoubaoCheckbox("DDC", "Enable deep context decoder.", ref _doubaoEnableDdc);

        ImGui.Spacing();
        RenderDoubaoProbe(dt);
    }

    private void RenderDoubaoProbe(float dt)
    {
        var rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(
            "Connection",
            "Tests whether the configured Doubao WebSocket endpoint and credentials are reachable."
        );
        SubsystemStatusChip.Render(_probeStatus, _elapsed);
        ImGuiHelpers.SettingEndRow(rowY);

        if (
            _probeStatus.Health == SubsystemHealth.Failed
            && !string.IsNullOrWhiteSpace(_probeStatus.Detail)
        )
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
            ImGui.TextWrapped(_probeStatus.Detail);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ProbeFooter.Render(
            ref _probeFooterState,
            _lastDoubaoProbeTime,
            _probeInFlight,
            dt,
            () => _ = RunDoubaoProbeAsync()
        );
    }

    private async Task RunDoubaoProbeAsync()
    {
        if (_probeInFlight)
        {
            return;
        }

        _probeInFlight = true;
        _probeStatus = new SubsystemStatus(SubsystemHealth.Degraded, "Testing...", null);

        var snapshot = _asr.Doubao;

        try
        {
            var result = await _doubaoProbe.ProbeAsync(snapshot);
            _dispatcher.Post(
                () =>
                {
                    _probeStatus = result;
                    _lastDoubaoProbeTime = DateTimeOffset.UtcNow;
                    _probeInFlight = false;
                }
            );
        }
        catch (Exception ex)
        {
            _dispatcher.Post(
                () =>
                {
                    _probeStatus = new SubsystemStatus(
                        SubsystemHealth.Failed,
                        "Probe failed",
                        ex.Message
                    );
                    _lastDoubaoProbeTime = DateTimeOffset.UtcNow;
                    _probeInFlight = false;
                }
            );
        }
    }

    private void RenderDoubaoCheckbox(
        string label,
        string tooltip,
        ref bool value
    )
    {
        var rowY = ImGui.GetCursorPosY();
        ImGuiHelpers.SettingLabel(label, tooltip);
        if (ImGui.Checkbox($"##doubao_{label.Replace(" ", "_")}", ref value))
        {
            CommitDoubao();
        }

        ImGuiHelpers.SettingEndRow(rowY);
    }

    private void CommitDoubao()
    {
        _asr = _asr with
        {
            Doubao = _asr.Doubao with
            {
                Endpoint = _doubaoEndpointBuf,
                AuthMode = _doubaoAuthMode,
                ApiKey = _doubaoApiKeyBuf,
                AppId = _doubaoAppIdBuf,
                AccessToken = _doubaoAccessTokenBuf,
                ResourceId = _doubaoResourceIdBuf,
                PacketDurationMs = Math.Clamp(_doubaoPacketDurationMs, 50, 1000),
                ShowUtterances = _doubaoShowUtterances,
                EnablePunctuation = _doubaoEnablePunctuation,
                EnableItn = _doubaoEnableItn,
                EnableDdc = _doubaoEnableDdc,
            },
        };

        _configWriter.Write(_asr);
    }

    private void SyncDoubaoBuffers()
    {
        _doubaoEndpointBuf = _asr.Doubao.Endpoint ?? string.Empty;
        _doubaoAuthMode = _asr.Doubao.AuthMode;
        _doubaoApiKeyBuf = _asr.Doubao.ApiKey ?? string.Empty;
        _doubaoAppIdBuf = _asr.Doubao.AppId ?? string.Empty;
        _doubaoAccessTokenBuf = _asr.Doubao.AccessToken ?? string.Empty;
        _doubaoResourceIdBuf = _asr.Doubao.ResourceId ?? string.Empty;
        _doubaoPacketDurationMs = _asr.Doubao.PacketDurationMs;
        _doubaoShowUtterances = _asr.Doubao.ShowUtterances;
        _doubaoEnablePunctuation = _asr.Doubao.EnablePunctuation;
        _doubaoEnableItn = _asr.Doubao.EnableItn;
        _doubaoEnableDdc = _asr.Doubao.EnableDdc;
    }

    private static string GetLanguageDisplayName(string cultureName)
    {
        foreach (var option in LanguageOptions)
        {
            if (
                option.CultureName is not null
                && string.Equals(
                    option.CultureName,
                    cultureName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return option.DisplayName;
            }
        }

        try
        {
            return CultureInfo.GetCultureInfo(cultureName).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return cultureName;
        }
    }

    private static bool IsEnglishOnlyCulture(string cultureName) =>
        string.Equals(cultureName, "en-US", StringComparison.OrdinalIgnoreCase)
        || string.Equals(cultureName, "en-GB", StringComparison.OrdinalIgnoreCase);

    private void RenderQuality()
    {
        ImGuiHelpers.SettingLabel(
            "Quality",
            "Trade-off between speed and accuracy of transcription."
        );

        for (var i = 0; i < QualityOptions.Length; i++)
        {
            var (label, value) = QualityOptions[i];
            var selected = _asr.TtsMode == value;
            if (ImGuiHelpers.Chip(label, selected))
            {
                _asr = _asr with { TtsMode = value };
                _configWriter.Write(_asr);
            }

            if (i < QualityOptions.Length - 1)
                ImGui.SameLine(0f, ChipGap);
        }
    }

    private void RenderVocabulary()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextTertiary);
        ImGui.TextUnformatted("Custom Vocabulary");
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
        ImGui.TextUnformatted(
            "Words we should recognize correctly — your avatar's name, show name, recurring topics."
        );
        ImGui.PopStyleColor();

        ImGui.Spacing();

        RenderChips();
        RenderAddInput();
    }

    private void RenderChips()
    {
        if (_vocabulary.Count == 0)
            return;

        int? removeIndex = null;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var currentLineWidth = 0f;
        var isFirstOnLine = true;

        for (var i = 0; i < _vocabulary.Count; i++)
        {
            var label = _vocabulary[i];
            var chipWidth = MeasureRemovableChipWidth(label);

            if (!isFirstOnLine && currentLineWidth + ChipGap + chipWidth > availableWidth)
            {
                currentLineWidth = 0f;
                isFirstOnLine = true;
            }

            if (!isFirstOnLine)
            {
                ImGui.SameLine(0f, ChipGap);
                currentLineWidth += ChipGap;
            }

            if (RenderRemovableChip(label, i))
            {
                removeIndex = i;
            }

            currentLineWidth += chipWidth;
            isFirstOnLine = false;
        }

        if (removeIndex.HasValue)
        {
            _vocabulary.RemoveAt(removeIndex.Value);
            CommitVocabulary();
        }

        ImGui.Spacing();
    }

    private void RenderAddInput()
    {
        var buttonWidth = ImGui.CalcTextSize("Add").X + ImGui.GetStyle().FramePadding.X * 2f;
        var gap = 6f;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - gap);
        ImGui.InputTextWithHint("##addvoc", "Add a word...", ref _inputBuffer, InputBufferSize);
        var enterPressed = ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Enter);

        ImGui.SameLine(0f, gap);
        var hasInput = _inputBuffer.Trim().Length > 0;
        if (!hasInput)
            ImGui.BeginDisabled();
        var buttonPressed = ImGui.Button("Add##add_vocab_btn");
        ImGuiHelpers.HandCursorOnHover();
        if (!hasInput)
            ImGui.EndDisabled();

        if ((enterPressed || buttonPressed) && _vocabulary.Count < MaxVocabulary)
        {
            var trimmed = _inputBuffer.Trim();
            if (
                trimmed.Length > 0
                && !_vocabulary.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            )
            {
                _vocabulary.Add(trimmed);
                CommitVocabulary();
            }

            _inputBuffer = string.Empty;
        }
    }

    private void CommitVocabulary()
    {
        _asr = _asr with { TtsPrompt = string.Join(", ", _vocabulary) };
        _configWriter.Write(_asr);
    }

    private static List<string> ParseVocabulary(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new List<string>();

        var parts = prompt.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return new List<string>(parts);
    }

    /// <summary>
    ///     Renders a chip with an integrated close (x) icon. Returns true if the user
    ///     clicked the close area.
    /// </summary>
    private static bool RenderRemovableChip(string label, int index)
    {
        var textSize = ImGui.CalcTextSize(label);
        var totalWidth = ChipPaddingX + textSize.X + ChipCloseGap + ChipCloseSize + ChipPaddingX;
        var totalHeight = textSize.Y + ChipPaddingY * 2f;
        var size = new Vector2(totalWidth, totalHeight);

        var cursor = ImGui.GetCursorScreenPos();
        // Chip limit is enforced at the add-input gate; defensively clamp here
        // so a future refactor that lifts MaxVocabulary can't index past the
        // cached id table.
        var chipId = index < VocabChipIds.Length ? VocabChipIds[index] : $"##vocab_{index}";
        var clicked = ImGui.InvisibleButton(chipId, size);
        ImGuiHelpers.HandCursorOnHover();
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var min = cursor;
        var max = cursor + size;

        // Background
        Vector4 fill = hovered ? Theme.SurfaceHover : Theme.Surface2;
        ImGui.AddRectFilled(drawList, min, max, ImGui.ColorConvertFloat4ToU32(fill), ChipRounding);

        // Border
        ImGui.AddRect(
            drawList,
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(Theme.AccentPrimary with { W = 0.3f }),
            ChipRounding,
            0,
            1f
        );

        // Label text
        var textPos = new Vector2(min.X + ChipPaddingX, min.Y + ChipPaddingY);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(Theme.TextPrimary), label);

        // Close icon (x)
        var closeCenter = new Vector2(
            max.X - ChipPaddingX - ChipCloseSize * 0.5f,
            min.Y + totalHeight * 0.5f
        );
        var crossHalf = 4f;
        var closeColor = ImGui.ColorConvertFloat4ToU32(
            hovered ? Theme.TextPrimary : Theme.TextSecondary
        );
        drawList.AddLine(
            closeCenter - new Vector2(crossHalf, crossHalf),
            closeCenter + new Vector2(crossHalf, crossHalf),
            closeColor,
            1.5f
        );
        drawList.AddLine(
            closeCenter + new Vector2(-crossHalf, crossHalf),
            closeCenter + new Vector2(crossHalf, -crossHalf),
            closeColor,
            1.5f
        );

        return clicked;
    }

    private static float MeasureRemovableChipWidth(string label)
    {
        var textWidth = ImGui.CalcTextSize(label).X;
        return ChipPaddingX + textWidth + ChipCloseGap + ChipCloseSize + ChipPaddingX;
    }
}
