namespace Aevatar.Workflow.Abstractions;

public static class WorkflowFileItemResultProjectionContract
{
    // A bounded first/last sample keeps failures from both ends of large file batches observable.
    public const int MaxRetainedResults = 32;

    // File batches multiply the per-item cost, so each output and error has a tighter bound than
    // the top-level failure and vote evidence fields.
    public const int MaxEvidenceUtf8Bytes = 8 * 1024;
}
