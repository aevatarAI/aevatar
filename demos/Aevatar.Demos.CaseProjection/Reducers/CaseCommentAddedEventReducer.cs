namespace Aevatar.Demos.CaseProjection.Reducers;

public sealed class CaseCommentAddedEventReducer : CaseProjectionEventReducerBase<CaseCommentAddedEvent>
{
    protected override bool Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        CaseCommentAddedEvent evt,
        DateTimeOffset now)
    {
        readModel.Comments.Add(new CaseProjectionComment
        {
            Timestamp = now,
            AuthorId = evt.AuthorId,
            Content = evt.Content,
        });

        CaseProjectionMutations.AddTimeline(
            readModel,
            now,
            "case.comment.added",
            $"author={evt.AuthorId}, chars={(evt.Content ?? string.Empty).Length}",
            envelope.Payload?.TypeUrl ?? "");

        return true;
    }
}
