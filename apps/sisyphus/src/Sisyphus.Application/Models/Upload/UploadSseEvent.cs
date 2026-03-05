namespace Sisyphus.Application.Models.Upload;

public enum UploadSseEventType
{
    PHASE_START,
    BATCH_DONE,
    PHASE_DONE,
    NODE_PURGED,
    NODE_FAILED,
    EDGE_PURGED,
    EDGE_FAILED,
    UPLOAD_DONE,
    VALIDATION_ERROR,
    STREAM_END,
}

/// <summary>
/// Static helpers to create SSE payload objects matching the spec's JSON format.
/// </summary>
public static class UploadSsePayloads
{
    public static object PhaseStart(int phase, string message) =>
        new { phase, message };

    public static object BatchDone(int phase, int batch, int totalBatches) =>
        new { phase, batch, total_batches = totalBatches };

    public static object PhaseDone(int phase, int? redNodes = null, int? redEdges = null) =>
        new { phase, red_nodes = redNodes, red_edges = redEdges };

    public static object NodePurged(int phase, int index, int total, string kgId, int blueCount) =>
        new { phase, index, total, kg_id = kgId, blue_count = blueCount };

    public static object NodeFailed(int phase, int index, string kgId, string reason) =>
        new { phase, index, kg_id = kgId, reason };

    public static object EdgePurged(int phase, int index, int total) =>
        new { phase, index, total };

    public static object EdgeFailed(int phase, int index, string reason) =>
        new { phase, index, reason };

    public static object UploadDone(int redNodes, int blueNodes, int blueEdges, int failures, string duration) =>
        new { red_nodes = redNodes, blue_nodes = blueNodes, blue_edges = blueEdges, failures, duration };

    public static object ValidationError(string message, List<string> unresolved, int unresolvedCount) =>
        new { message, unresolved, unresolved_count = unresolvedCount };

    public static object StreamEnd() => new { };
}
