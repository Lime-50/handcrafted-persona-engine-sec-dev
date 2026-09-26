using Hexa.NET.ImGui;
using Microsoft.Extensions.Options;
using PersonaEngine.Lib.Assets;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.UI.ControlPanel.Layout;
using PersonaEngine.Lib.UI.ControlPanel.Panels.Shared;
using PersonaEngine.Lib.UI.ControlPanel.Threading;

namespace PersonaEngine.Lib.UI.ControlPanel.Panels.Avatar.Sections;

/// <summary>
///     Live2D model picker card. Sources the available characters from
///     <see cref="IAssetCatalog.GetUserAssets" /> for <see cref="UserAssetType.Live2DModel" />,
///     so bundled defaults and user-added models surface uniformly. Subscribes to
///     <see cref="IAssetCatalog.Changed" /> to refresh the picker live when the user
///     drops a new model into the live2d folder. The saved model is always shown
///     selected, even if it's not on disk — in that case a muted warning appears so
///     the user is never silently moved off their character.
/// </summary>
public sealed class ModelSection : IDisposable
{
    private readonly IConfigWriter _configWriter;
    private readonly IAssetCatalog _catalog;
    private readonly IUiThreadDispatcher _uiDispatcher;
    private readonly IDisposable? _changeSubscription;
    private readonly ScannedNamePicker _picker;

    private Live2DOptions _live2d;
    private bool _initialized;

    public ModelSection(
        IOptionsMonitor<Live2DOptions> monitor,
        IConfigWriter configWriter,
        IAssetCatalog catalog,
        IUiThreadDispatcher uiDispatcher
    )
    {
        _configWriter = configWriter;
        _catalog = catalog;
        _uiDispatcher = uiDispatcher;
        _live2d = monitor.CurrentValue;
        _picker = new ScannedNamePicker(ScanModels);

        _changeSubscription = monitor.OnChange(
            (updated, _) =>
            {
                _live2d = updated;
                if (!_initialized)
                    return;
                _picker.RecomputeMissing(_live2d.ModelName);
            }
        );

        // Live refresh: catalog watches the live2d directory and fires Changed
        // when content changes, so newly-dropped models surface without UI restart.
        _catalog.Changed += OnCatalogChanged;
    }

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _changeSubscription?.Dispose();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) =>
        // AssetCatalog.Changed fires from a thread-pool thread (UserContentWatcher
        // debounce). Marshal the picker refresh onto the UI thread so it doesn't
        // race the ImGui render loop reading _picker state.
        _uiDispatcher.Post(() =>
        {
            if (_initialized)
            {
                _picker.Refresh(_live2d.ModelName);
            }
        });

    public void Render(float dt)
    {
        if (!_initialized)
        {
            _picker.Refresh(_live2d.ModelName);
            _initialized = true;
        }

        using (Ui.Card("##model", padding: 12f))
        {
            // Header
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextTertiary);
            ImGui.TextUnformatted("Live2D Model");
            ImGui.PopStyleColor();

            // Description
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
            ImGui.TextUnformatted("Which character to render, and at what resolution");
            ImGui.PopStyleColor();
            ImGui.Spacing();

            RenderCharacterRow();
            RenderResolutionRow();
            RenderFramingRows(dt);
            RenderIdleMotionRows(dt);
        }
    }

    // ── Character row ─────────────────────────────────────────────────────────

    private void RenderCharacterRow()
    {
        ImGuiHelpers.SettingLabel("Character", "The Live2D model to load from your models folder.");

        if (
            ImGuiHelpers.ScannedCombo(
                "ModelCombo",
                _picker,
                _live2d.ModelName,
                out var picked,
                onRefresh: () => _picker.Refresh(_live2d.ModelName),
                refreshTooltip: "Re-scan the models folder for available Live2D characters."
            )
        )
        {
            _live2d = _live2d with { ModelName = picked };
            _configWriter.Write(_live2d);
            _picker.RecomputeMissing(_live2d.ModelName);
        }

        if (_picker.IsMissing)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
            ImGui.TextUnformatted($"'{_live2d.ModelName}' not found on disk");
            ImGui.PopStyleColor();
        }
    }

    // ── Resolution row ────────────────────────────────────────────────────────

    /// <summary>
    ///     Zoom + pan applied on top of the automatic fit. Lets a full-body model be
    ///     framed down to the upper body, or an off-centre canvas be re-centred.
    ///     Applies live: the Live2D renderer re-reads these values on every config change,
    ///     so the avatar updates while a slider is dragged (no restart needed).
    /// </summary>
    private void RenderFramingRows(float dt)
    {
        ImGuiHelpers.SettingLabel(
            "Zoom",
            "1.0 fits the whole model in frame. Raise it to crop in (e.g. upper body only)."
        );

        var zoom = (float)_live2d.ModelZoom;
        if (
            ImGuiHelpers.LabeledSlider(
                "##model_zoom",
                ref zoom,
                0.5f,
                3.0f,
                "Wide",
                "Close",
                "%.2f",
                dt
            )
        )
        {
            _live2d = _live2d with { ModelZoom = MathF.Round(zoom, 3) };
            _configWriter.Write(_live2d);
        }

        ImGuiHelpers.SettingLabel(
            "Vertical",
            "Moves the model up/down. Negative values bring the head down into frame after zooming."
        );

        var offsetY = (float)_live2d.ModelOffsetY;
        if (
            ImGuiHelpers.LabeledSlider(
                "##model_y",
                ref offsetY,
                -1.5f,
                1.5f,
                "Down",
                "Up",
                "%.2f",
                dt
            )
        )
        {
            _live2d = _live2d with { ModelOffsetY = MathF.Round(offsetY, 3) };
            _configWriter.Write(_live2d);
        }

        ImGuiHelpers.SettingLabel("Horizontal", "Moves the model left/right.");

        var offsetX = (float)_live2d.ModelOffsetX;
        if (
            ImGuiHelpers.LabeledSlider(
                "##model_x",
                ref offsetX,
                -1.5f,
                1.5f,
                "Left",
                "Right",
                "%.2f",
                dt
            )
        )
        {
            _live2d = _live2d with { ModelOffsetX = MathF.Round(offsetX, 3) };
            _configWriter.Write(_live2d);
        }

        if (ImGuiHelpers.SubtleButton("Reset Framing", IsFramingModified()))
        {
            _live2d = _live2d with { ModelZoom = 1.0, ModelOffsetX = 0.0, ModelOffsetY = 0.0 };
            _configWriter.Write(_live2d);
        }

        ImGuiHelpers.Tooltip("Back to the automatic fit (zoom 1.0, centred).");
    }

    private bool IsFramingModified() =>
        Math.Abs(_live2d.ModelZoom - 1.0) > 0.001
        || Math.Abs(_live2d.ModelOffsetX) > 0.001
        || Math.Abs(_live2d.ModelOffsetY) > 0.001;

    /// <summary>
    ///     Amplitude/speed controls for the idle motion (head sway, body sway, chest
    ///     breathing). Lets a motionless VTube Studio model come alive, and lets a
    ///     twitchy one be toned down. Applied live, like the framing rows.
    /// </summary>
    private void RenderIdleMotionRows(float dt)
    {
        ImGuiHelpers.SettingLabel(
            "Head Sway",
            "How much the head keeps turning on its own. 0 keeps the head still."
        );

        var head = (float)_live2d.IdleHeadSway;
        if (
            ImGuiHelpers.LabeledSlider(
                "##idle_head",
                ref head,
                0.0f,
                3.0f,
                "Still",
                "Lively",
                "%.2f",
                dt
            )
        )
        {
            _live2d = _live2d with { IdleHeadSway = MathF.Round(head, 3) };
            _configWriter.Write(_live2d);
        }

        ImGuiHelpers.SettingLabel(
            "Body Sway",
            "How much the shoulders/body lean and bob along with the breathing."
        );

        var body = (float)_live2d.IdleBodySway;
        if (
            ImGuiHelpers.LabeledSlider(
                "##idle_body",
                ref body,
                0.0f,
                3.0f,
                "Still",
                "Lively",
                "%.2f",
                dt
            )
        )
        {
            _live2d = _live2d with { IdleBodySway = MathF.Round(body, 3) };
            _configWriter.Write(_live2d);
        }

        ImGuiHelpers.SettingLabel(
            "Breathing",
            "Chest rise and fall. Models without a ParamBreath parameter ignore this."
        );

        var breath = (float)_live2d.IdleBreathSway;
        if (
            ImGuiHelpers.LabeledSlider(
                "##idle_breath",
                ref breath,
                0.0f,
                3.0f,
                "None",
                "Deep",
                "%.2f",
                dt
            )
        )
        {
            _live2d = _live2d with { IdleBreathSway = MathF.Round(breath, 3) };
            _configWriter.Write(_live2d);
        }

        ImGuiHelpers.SettingLabel(
            "Motion Speed",
            "How quickly the idle motion cycles. Lower is calmer, higher is more animated."
        );

        var speed = (float)_live2d.IdleMotionSpeed;
        if (
            ImGuiHelpers.LabeledSlider(
                "##idle_speed",
                ref speed,
                0.2f,
                3.0f,
                "Slow",
                "Fast",
                "%.2f",
                dt
            )
        )
        {
            _live2d = _live2d with { IdleMotionSpeed = MathF.Round(speed, 3) };
            _configWriter.Write(_live2d);
        }

        if (ImGuiHelpers.SubtleButton("Reset Motion", IsIdleMotionModified()))
        {
            _live2d = _live2d with
            {
                IdleHeadSway = 1.0,
                IdleBodySway = 1.0,
                IdleBreathSway = 1.0,
                IdleMotionSpeed = 1.0,
            };
            _configWriter.Write(_live2d);
        }

        ImGuiHelpers.Tooltip("Back to the default idle motion (all 1.00).");
    }

    private bool IsIdleMotionModified() =>
        Math.Abs(_live2d.IdleHeadSway - 1.0) > 0.001
        || Math.Abs(_live2d.IdleBodySway - 1.0) > 0.001
        || Math.Abs(_live2d.IdleBreathSway - 1.0) > 0.001
        || Math.Abs(_live2d.IdleMotionSpeed - 1.0) > 0.001;

    private void RenderResolutionRow()
    {
        ImGuiHelpers.SettingLabel(
            "Resolution",
            "Canvas size for the avatar. Pick an orientation, then a preset that matches your scene in OBS."
        );

        var width = _live2d.Width;
        var height = _live2d.Height;

        if (ImGuiHelpers.ResolutionChips("Live2DRes", ref width, ref height))
        {
            _live2d = _live2d with { Width = width, Height = height };
            _configWriter.Write(_live2d);
        }
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    private IEnumerable<string> ScanModels()
    {
        // Catalog already returns alphabetised entries scoped to the Live2D root,
        // so no extra sort/filter needed here.
        return _catalog
            .GetUserAssets(UserAssetType.Live2DModel)
            .Select(a => a.DisplayName)
            .Where(n => !string.IsNullOrEmpty(n));
    }
}
