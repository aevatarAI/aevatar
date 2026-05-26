namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>
/// Marks actor types whose event replay can reconverge after store-version drift.
/// </summary>
public interface IEventSourcingVersionDriftRecoverableActor;
