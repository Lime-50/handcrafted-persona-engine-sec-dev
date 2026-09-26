using Hexa.NET.ImGui;
using Microsoft.Extensions.Options;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.UI.ControlPanel.Layout;
using PersonaEngine.Lib.UI.ControlPanel.Panels.Voice.Models;

namespace PersonaEngine.Lib.UI.ControlPanel.Panels.Voice.Sections;

/// <summary>
///     Doubao 音色 (voice) picker. Cloud voices are addressed by id 鈥?either a built-in
///     speaker such as <c>zh_female_shuangkuaisisi_uranus_bigtts</c> or the <c>S_...</c> id
///     of a voice cloned in the Volcengine console 鈥?so the field is free-form and backed
///     by a preset dropdown built from the bundled voice catalog.
///     Only rendered in <see cref="VoiceMode.Doubao" />.
/// </summary>
public sealed class DoubaoVoiceIdSection : IDisposable
{
    private const int BufferSize = 256;

    private readonly VoiceMetadataCatalog _catalog;
    private readonly IConfigWriter _configWriter;
    private readonly IDisposable? _subscription;

    private DoubaoTtsOptions _doubao;
    private string _lastCommitted = string.Empty;
    private string _voiceIdBuffer = string.Empty;

    public DoubaoVoiceIdSection(
        IOptionsMonitor<DoubaoTtsOptions> monitor,
        IConfigWriter configWriter,
        VoiceMetadataCatalog catalog
    )
    {
        _configWriter = configWriter;
        _catalog = catalog;
        _doubao = monitor.CurrentValue;
        _voiceIdBuffer = _doubao.DefaultVoice ?? string.Empty;
        _lastCommitted = _voiceIdBuffer;

        _subscription = monitor.OnChange(
            (updated, _) =>
            {
                _doubao = updated;

                // Only adopt values we didn't write ourselves, so an external change
                // (gallery tile, Advanced settings) is picked up without stealing the
                // caret while the user is typing here.
                var incoming = updated.DefaultVoice ?? string.Empty;
                if (!string.Equals(incoming, _lastCommitted, StringComparison.Ordinal))
                {
                    _lastCommitted = incoming;
                    _voiceIdBuffer = incoming;
                }
            }
        );
    }

    public void Dispose() => _subscription?.Dispose();

    public void Render(float dt, VoiceMode mode)
    {
        if (mode != VoiceMode.Doubao)
        {
            return;
        }

        using (Ui.Card("##doubao_voice", padding: 12f))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextTertiary);
            ImGui.TextUnformatted("Voice ID");
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
            ImGui.TextUnformatted("Which Doubao 音色 to speak with (sent as \"speaker\")");
            ImGui.PopStyleColor();
            ImGui.Spacing();

            RenderPresetCombo();
            RenderFreeFormField();
        }
    }

    private void RenderPresetCombo()
    {
        var presets = _catalog.List(VoiceEngine.Doubao);
        if (presets.Count == 0)
        {
            return;
        }

        ImGuiHelpers.SettingLabel(
            "Bundled",
            "Voices that ship with the app. Pick one, or type any id below."
        );

        if (ImGui.BeginCombo("##doubao_voice_preset", CurrentPresetLabel(presets)))
        {
            foreach (var preset in presets)
            {
                var selected = string.Equals(
                    preset.Id,
                    _doubao.DefaultVoice,
                    StringComparison.Ordinal
                );

                if (ImGui.Selectable($"{preset.DisplayName}  ({preset.Id})", selected))
                {
                    _voiceIdBuffer = preset.Id;
                    Commit(preset.Id);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGuiHelpers.Tooltip("Bundled Doubao speakers. Anything else goes in the field below.");
    }

    private void RenderFreeFormField()
    {
        var rowY = ImGui.GetCursorPosY();

        ImGuiHelpers.SettingLabel(
            "Custom ID",
            "Any Volcengine speaker id 鈥?e.g. zh_female_cancan_mars_bigtts, "
                + "or the S_... id of a voice you cloned in the Volcengine console."
        );

        if (ImGui.InputText("##doubao_voice_id_field", ref _voiceIdBuffer, BufferSize))
        {
            var trimmed = _voiceIdBuffer.Trim();
            if (trimmed.Length > 0)
            {
                Commit(trimmed);
            }
        }

        ImGuiHelpers.SettingEndRow(rowY);

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);

        if (string.IsNullOrWhiteSpace(_voiceIdBuffer))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
            ImGui.TextUnformatted("Voice ID cannot be empty 鈥?keep the current one or pick a preset.");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextUnformatted($"Active voice: {_doubao.DefaultVoice}");
        }

        ImGui.PopStyleColor();
    }

    private string CurrentPresetLabel(IReadOnlyList<VoiceDescriptor> presets)
    {
        foreach (var preset in presets)
        {
            if (string.Equals(preset.Id, _doubao.DefaultVoice, StringComparison.Ordinal))
            {
                return preset.DisplayName;
            }
        }

        return string.IsNullOrWhiteSpace(_doubao.DefaultVoice)
            ? "Select a voice"
            : $"Custom: {_doubao.DefaultVoice}";
    }

    private void Commit(string voiceId)
    {
        _lastCommitted = voiceId;
        _voiceIdBuffer = voiceId;
        _doubao = _doubao with { DefaultVoice = voiceId };
        _configWriter.Write(_doubao);
    }
}
