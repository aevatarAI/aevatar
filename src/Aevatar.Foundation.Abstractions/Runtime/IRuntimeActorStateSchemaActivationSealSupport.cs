namespace Aevatar.Foundation.Abstractions.Runtime;

/// <summary>
/// Runtime composition marker proving that admitted migrations run before agent construction and
/// that an active older-schema actor turns over before handling another envelope once its migration
/// becomes admitted. It does not require an unadmitted bootstrap migration to block activation.
/// Capability advertisers which require those semantics must depend on this service so a newer
/// module cannot advertise support beside an older runtime.
/// </summary>
public interface IRuntimeActorStateSchemaActivationSealSupport
{
}
