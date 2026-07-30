using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfileDeterminism
{
    private const int Sha256Length = 32;

    public static string CreateProfileId(AgentProfileOwner owner, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        if (normalizedKey.Length == 0)
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));

        var ownerIdentity = owner.OwnerCase switch
        {
            AgentProfileOwner.OwnerOneofCase.Scope when !string.IsNullOrWhiteSpace(owner.Scope.ScopeId) =>
                $"scope:{owner.Scope.ScopeId}",
            AgentProfileOwner.OwnerOneofCase.System when owner.System.PlatformId == AgentProfileOwners.PlatformId =>
                $"system:{owner.System.PlatformId}",
            _ => throw new ArgumentException("A valid Agent Profile owner is required.", nameof(owner)),
        };

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"aevatar.agent-profile.profile-id.v1\n{ownerIdentity}\n{normalizedKey}"));
        return $"prof_{Base64Url(bytes.AsSpan(0, 18))}";
    }

    public static string CreateOperationId(AgentProfileOwner owner, string idempotencyKey) =>
        CreateStableId("operation-id", "op_", owner, idempotencyKey);

    public static byte[] Sha256Utf8(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));

    public static ByteString ComputeDraftDigest(AgentProfileDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return ByteString.CopyFrom(SHA256.HashData(SerializeDeterministically(NormalizeDraft(draft))));
    }

    public static AgentProfilePublishedSnapshot BuildPublishedSnapshot(
        AgentProfileIdentity identity,
        AgentProfileDraft draft,
        long draftRevision,
        long publishedRevision,
        DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var normalized = NormalizeDraft(draft);
        var runtime = normalized.RuntimeProfile?.Clone() ?? new AgentProfileSnapshot();
        runtime.ProfileId = identity.ProfileId;
        runtime.ProfileVersion = publishedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        runtime.PolicyRevision = normalized.RuntimeProfile?.PolicyRevision ?? string.Empty;
        runtime.Instructions = normalized.Instructions;
        runtime.PublishedRevision = publishedRevision;
        runtime.DeterministicPolicySha256 = ByteString.Empty;
        runtime.PublishedSnapshotSha256 = ByteString.Empty;
        runtime.DeterministicPolicySha256 = ByteString.CopyFrom(
            SHA256.HashData(SerializeDeterministically(runtime)));

        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = identity.Clone(),
            DisplayName = normalized.DisplayName,
            Purpose = normalized.Purpose,
            Instructions = normalized.Instructions,
            RuntimeProfile = runtime,
            DraftRevision = draftRevision,
            DraftSha256 = ComputeDraftDigest(normalized),
            PublishedRevision = publishedRevision,
            PublishedAt = Timestamp.FromDateTimeOffset(publishedAt),
        };
        snapshot.SnapshotSha256 = ComputePublishedSnapshotDigest(snapshot);
        snapshot.RuntimeProfile.PublishedSnapshotSha256 = snapshot.SnapshotSha256;
        snapshot.SnapshotSha256 = ComputePublishedSnapshotDigest(snapshot);
        snapshot.RuntimeProfile.PublishedSnapshotSha256 = snapshot.SnapshotSha256;
        return snapshot;
    }

    public static bool VerifyPublishedSnapshot(AgentProfilePublishedSnapshot? snapshot)
    {
        if (snapshot?.Identity is null || snapshot.RuntimeProfile is null ||
            snapshot.SnapshotSha256.Length != Sha256Length ||
            snapshot.DraftSha256.Length != Sha256Length)
        {
            return false;
        }

        var expected = ComputePublishedSnapshotDigest(snapshot);
        return CryptographicOperations.FixedTimeEquals(expected.Span, snapshot.SnapshotSha256.Span) &&
               snapshot.RuntimeProfile.PublishedRevision == snapshot.PublishedRevision &&
               snapshot.RuntimeProfile.PublishedSnapshotSha256.Equals(snapshot.SnapshotSha256) &&
               string.Equals(snapshot.RuntimeProfile.Instructions, snapshot.Instructions, StringComparison.Ordinal);
    }

    public static AgentProfileDraft NormalizeDraft(AgentProfileDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalized = draft.Clone();
        normalized.DisplayName = NormalizeText(normalized.DisplayName).Trim();
        normalized.Purpose = NormalizeText(normalized.Purpose).Trim();
        normalized.Instructions = NormalizeText(normalized.Instructions).Trim();
        if (normalized.RuntimeProfile is not null)
        {
            normalized.RuntimeProfile.Instructions = normalized.Instructions;
            SortDistinct(normalized.RuntimeProfile.MaximumToolPolicy?.ToolNames);
            SortDistinct(normalized.RuntimeProfile.MaximumToolPolicy?.ToolSetRefs);
            SortDistinct(normalized.RuntimeProfile.RecoveryToolPolicy?.ToolNames);
            SortDistinct(normalized.RuntimeProfile.RecoveryToolPolicy?.ToolSetRefs);
            var members = normalized.RuntimeProfile.Members
                .OrderBy(static x => x.IntentId, StringComparer.Ordinal)
                .Select(static x => x.Clone())
                .ToArray();
            normalized.RuntimeProfile.Members.Clear();
            normalized.RuntimeProfile.Members.Add(members);
            foreach (var member in normalized.RuntimeProfile.Members)
            {
                SortDistinct(member.ExplicitTriggerAliases);
                SortDistinct(member.TaskToolPolicy?.ToolNames);
                SortDistinct(member.TaskToolPolicy?.ToolSetRefs);
            }
            normalized.RuntimeProfile.DeterministicPolicySha256 = ByteString.Empty;
            normalized.RuntimeProfile.PublishedSnapshotSha256 = ByteString.Empty;
        }
        return normalized;
    }

    public static bool SameOwner(AgentProfileOwner? left, AgentProfileOwner? right) =>
        left is not null && right is not null && left.Equals(right);

    private static ByteString ComputePublishedSnapshotDigest(AgentProfilePublishedSnapshot snapshot)
    {
        var input = snapshot.Clone();
        input.SnapshotSha256 = ByteString.Empty;
        if (input.RuntimeProfile is not null)
            input.RuntimeProfile.PublishedSnapshotSha256 = ByteString.Empty;
        return ByteString.CopyFrom(SHA256.HashData(SerializeDeterministically(input)));
    }

    private static string CreateStableId(
        string purpose,
        string prefix,
        AgentProfileOwner owner,
        string idempotencyKey)
    {
        var profileId = CreateProfileId(owner, idempotencyKey);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"aevatar.agent-profile.{purpose}.v1\n{profileId}"));
        return $"{prefix}{Base64Url(bytes.AsSpan(0, 18))}";
    }

    private static string NormalizeText(string? value) =>
        (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);

    private static void SortDistinct(Google.Protobuf.Collections.RepeatedField<string>? values)
    {
        if (values is null)
            return;
        var normalized = values.Select(static x => x?.Trim() ?? string.Empty)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
        values.Clear();
        values.Add(normalized);
    }

    private static byte[] SerializeDeterministically(IMessage message)
    {
        using var stream = new MemoryStream(message.CalculateSize());
        using var output = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true };
        message.WriteTo(output);
        output.Flush();
        return stream.ToArray();
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
