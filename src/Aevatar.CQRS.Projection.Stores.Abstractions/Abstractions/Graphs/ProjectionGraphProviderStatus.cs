namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed record ProjectionGraphProviderStatus(
    string ProviderName,
    bool Enabled);
