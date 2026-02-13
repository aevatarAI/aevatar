// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → End
// 3. Logs prompt and full LLM response for observability
// ─────────────────────────────────────────────────────────────

using System.Text;
using Aevatar.Attributes;
using Aevatar;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI;

/// <summary>
/// Role-based AI GAgent. Receives ChatRequestEvent and streams LLM response.
/// </summary>
public class RoleGAgent : AIGAgentBase<RoleGAgentState>
{
    /// <summary>Role name.</summary>
    public string RoleName { get; private set; } = "";

    /// <summary>Sets role name.</summary>
    public void SetRoleName(string name) => RoleName = name;

    /// <summary>Returns agent description.</summary>
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"RoleGAgent[{RoleName}]:{Id}");

    /// <summary>
    /// Handles ChatRequestEvent via streaming LLM call.
    /// Publishes AG-UI three-phase events and logs the interaction.
    /// </summary>
    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        var promptPreview = request.Prompt.Length > 200
            ? request.Prompt[..200] + "..."
            : request.Prompt;
        Logger.LogInformation("[{Role}] LLM request: {Preview}", RoleName, promptPreview);

        // ─── AG-UI: TEXT_MESSAGE_START ───
        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, EventDirection.Up);

        // ─── AG-UI: TEXT_MESSAGE_CONTENT — streaming chunks ───
        var fullContent = new StringBuilder();
        await foreach (var chunk in ChatStreamAsync(request.Prompt))
        {
            fullContent.Append(chunk);
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = chunk,
                SessionId = request.SessionId,
            }, EventDirection.Up);
        }

        var response = fullContent.ToString();
        var responsePreview = response.Length > 300
            ? response[..300] + "..."
            : response;
        Logger.LogInformation("[{Role}] LLM response ({Len} chars): {Preview}",
            RoleName, response.Length, responsePreview);

        // ─── AG-UI: TEXT_MESSAGE_END ───
        await PublishAsync(new TextMessageEndEvent
        {
            Content = response,
            SessionId = request.SessionId,
        }, EventDirection.Up);
    }
}
