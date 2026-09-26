using System.Text.RegularExpressions;
using Hexa.NET.ImGui;
using OpenAI.Chat;
using PersonaEngine.Lib.Core.Conversation.Abstractions.Context;
using PersonaEngine.Lib.Core.Conversation.Abstractions.Session;
using PersonaEngine.Lib.UI.ControlPanel.Layout;
using ConversationChatMessage = PersonaEngine.Lib.Core.Conversation.Abstractions.Context.ChatMessage;

namespace PersonaEngine.Lib.UI.ControlPanel.Panels.Dashboard.Sections;

/// <summary>
///     Scrolling conversation transcript showing history and pending (in-progress) turns.
/// </summary>
public sealed partial class TranscriptSection(IConversationOrchestrator orchestrator)
{
    public void Render(float dt)
    {
        ImGuiHelpers.SectionHeader("Conversation");

        using (Ui.FillChild("##Messages", ImGuiChildFlags.Borders, padding: 8f))
        {
            if (!orchestrator.TryGetFirstActiveSession(out var session))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
                ImGui.TextUnformatted("No active conversation.");
                ImGui.PopStyleColor();
            }
            else
            {
                RenderHistory(session.Context);
            }

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20f)
                ImGui.SetScrollHereY(1f);
        }
    }

    private static void RenderHistory(IConversationContext context)
    {
        var history = context.History;

        foreach (var turn in history)
        {
            foreach (var message in turn.Messages)
                RenderMessage(message);
        }

        var pending = context.PendingTurn;

        if (pending is not null)
        {
            foreach (var message in pending.Messages)
                RenderMessage(message);
        }
    }

    private static void RenderMessage(ConversationChatMessage message)
    {
        var nameColor =
            message.Role == ChatMessageRole.User ? Theme.AccentPrimary : Theme.AccentSecondary;

        ImGui.PushStyleColor(ImGuiCol.Text, nameColor);
        ImGui.TextUnformatted(message.ParticipantName);
        ImGui.PopStyleColor();

        ImGui.SameLine(0f, 6f);

        // Control tags such as "[Aria]" / "[EMOTION:😄]" drive speech and Live2D but are
        // never spoken, so they are hidden from the readable transcript.
        var displayText =
            message.Role == ChatMessageRole.Assistant
                ? StripControlTags(message.Text)
                : message.Text;

        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(displayText);
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
    }

    private static string StripControlTags(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('['))
        {
            return text;
        }

        var stripped = BracketTagRegex().Replace(text, string.Empty);
        stripped = SpaceRunRegex().Replace(stripped, " ");

        return stripped.Trim();
    }

    [GeneratedRegex(@"\[[^\[\]\r\n]{1,64}\]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRunRegex();
}
