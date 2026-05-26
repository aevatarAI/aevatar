namespace Aevatar.GAgents.ChatHistory;

/// <summary>
/// Provides ChatHistory index actor addressing without exposing runtime
/// lifecycle APIs to conversation actors.
/// </summary>
public interface IChatHistoryIndexTopologyPort
{
    string GetIndexActorId(string scopeId);
}
