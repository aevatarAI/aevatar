namespace Aevatar.Capabilities;

/// <summary>Declares whether a discovered HTTP operation can mutate business resources.</summary>
public sealed record AevatarToolMetadata(bool ReadOnly);
