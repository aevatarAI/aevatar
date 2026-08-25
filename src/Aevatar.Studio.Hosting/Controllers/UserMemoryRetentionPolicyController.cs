using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Authorize]
[Route("api/user-memory/retention-policy")]
public sealed class UserMemoryRetentionPolicyController : ControllerBase
{
    private readonly IUserMemoryRetentionPolicyCommandPort _commandPort;
    private readonly IAppScopeResolver _scopeResolver;

    public UserMemoryRetentionPolicyController(
        IUserMemoryRetentionPolicyCommandPort commandPort,
        IAppScopeResolver scopeResolver)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    [HttpPut]
    public async Task<ActionResult<UserMemoryRetentionPolicySaveReceiptResponse>> Replace(
        [FromBody] ReplaceUserMemoryRetentionPolicyRequest? request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { message = "Request body is required." });
        if (request.ExpectedStateVersion is null)
            return BadRequest(new { message = "expectedStateVersion is required." });

        try
        {
            var owner = UserMemoryOwnerKey.ForScope(_scopeResolver.ResolveScopeIdOrDefault());
            var receipt = await _commandPort.ReplaceAsync(
                request.ToApplication(owner),
                ct).ConfigureAwait(false);
            return Accepted(UserMemoryRetentionPolicySaveReceiptResponse.FromApplication(receipt));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { message = exception.Message });
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplaceUserMemoryRetentionPolicyRequest(
    [property: JsonPropertyName("rules")] IReadOnlyList<UserMemoryCategoryRetentionRuleRequest>? Rules,
    [property: JsonPropertyName("expectedStateVersion")] long? ExpectedStateVersion,
    [property: JsonPropertyName("mutationId")] string? MutationId)
{
    public ReplaceUserMemoryRetentionPolicy ToApplication(UserMemoryOwnerKey owner) => new(
        owner,
        (Rules ?? []).Select(static rule =>
                rule?.ToApplication() ?? throw new InvalidOperationException("Retention rule is required."))
            .ToArray(),
        ExpectedStateVersion ?? throw new InvalidOperationException("expectedStateVersion is required."),
        MutationId ?? string.Empty);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UserMemoryCategoryRetentionRuleRequest(
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("maxEntries")] int MaxEntries,
    [property: JsonPropertyName("evictionRank")] int EvictionRank)
{
    public UserMemoryCategoryRetentionRule ToApplication() => new(
        Category?.Trim().ToLowerInvariant() switch
        {
            "preference" => UserMemoryCategory.Preference,
            "instruction" => UserMemoryCategory.Instruction,
            "context" => UserMemoryCategory.Context,
            _ => UserMemoryCategory.Unspecified,
        },
        MaxEntries,
        EvictionRank);
}

public sealed record UserMemoryRetentionPolicySaveReceiptResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("ackStage")] string AckStage,
    [property: JsonPropertyName("actorId")] string ActorId,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("ackedAtUtc")] DateTimeOffset AckedAtUtc)
{
    public static UserMemoryRetentionPolicySaveReceiptResponse FromApplication(
        UserConfigSaveReceipt receipt) => new(
        receipt.Accepted,
        receipt.CommandId,
        receipt.AckStage,
        receipt.ActorId,
        receipt.CorrelationId,
        receipt.AckedAtUtc);
}
