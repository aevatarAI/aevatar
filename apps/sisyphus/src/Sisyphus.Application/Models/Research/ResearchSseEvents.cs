namespace Sisyphus.Application.Models.Research;

public enum ResearchSseEventType
{
    LOOP_STARTED,
    ROUND_START,
    GRAPH_READ,
    LLM_CALL_START,
    LLM_CALL_DONE,
    VALIDATION_FAILED,
    GRAPH_WRITE_DONE,
    ROUND_DONE,
    LOOP_STOPPED,
    LOOP_ERROR,
}

public static class ResearchSsePayloads
{
    public static object LoopStarted(int round) =>
        new { round };

    public static object RoundStart(int round) =>
        new { round };

    public static object GraphRead(int round, int blueNodeCount) =>
        new { round, blue_node_count = blueNodeCount };

    public static object LlmCallStart(int round) =>
        new { round };

    public static object LlmCallDone(int round, int newNodes, int newEdges) =>
        new { round, new_nodes = newNodes, new_edges = newEdges };

    public static object ValidationFailed(int round, int attempt, List<string> errors) =>
        new { round, attempt, errors };

    public static object GraphWriteDone(int round, int nodesWritten, int edgesWritten) =>
        new { round, nodes_written = nodesWritten, edges_written = edgesWritten };

    public static object RoundDone(int round, int totalBlueNodes) =>
        new { round, total_blue_nodes = totalBlueNodes };

    public static object LoopStopped(int round, string reason) =>
        new { round, reason };

    public static object LoopError(int round, string error) =>
        new { round, error };
}
