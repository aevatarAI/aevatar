namespace Aevatar.AI.ToolProviders.Binding.Models;

/// <summary>A single binding entry returned by list queries.</summary>
public sealed record ScopeBindingEntry(
    string ServiceId,
    string DisplayName,
    string ImplementationKind,
    string? RevisionId,
    string? ExpectedActorId,
    DateTimeOffset? LastUpdated);

/// <summary>Health status of a specific binding.</summary>
public sealed record ScopeBindingHealthStatus(
    string ServiceId,
    string DisplayName,
    string ImplementationKind,
    string Status,
    string? ExpectedActorId,
    string? ActiveActorId,
    string? ErrorMessage,
    DateTimeOffset? LastChecked);

/// <summary>Result of an unbind operation.</summary>
public sealed record ScopeBindingUnbindResult(
    bool Success,
    string ServiceId,
    string? Error = null);
