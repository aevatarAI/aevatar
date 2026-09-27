using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public enum OrnnSkillMutationFailureKind
{
    Unspecified = 0,
    Rejected = 1,
    Timeout = 2,
    Transport = 3,
}

public sealed record OrnnSkillMutationFailure(
    OrnnSkillMutationFailureKind Kind,
    string Code,
    string Message,
    int? HttpStatus,
    AgentToolFailureOutcome Outcome);

public sealed record OrnnSkillMutationResponse
{
    private OrnnSkillMutationResponse(
        bool succeeded,
        string rawResponse,
        OrnnSkillMutationFailure? failure)
    {
        Succeeded = succeeded;
        RawResponse = rawResponse;
        Failure = failure;
    }

    public bool Succeeded { get; }

    public string RawResponse { get; }

    public OrnnSkillMutationFailure? Failure { get; }

    public static OrnnSkillMutationResponse Success(string rawResponse) =>
        new(true, rawResponse, null);

    public static OrnnSkillMutationResponse Failed(
        string rawResponse,
        OrnnSkillMutationFailure failure) =>
        new(false, rawResponse, failure ?? throw new ArgumentNullException(nameof(failure)));
}
