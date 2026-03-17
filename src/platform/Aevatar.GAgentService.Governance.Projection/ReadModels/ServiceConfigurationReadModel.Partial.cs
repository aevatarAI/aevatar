using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Governance.Projection.ReadModels;

public sealed partial class ServiceConfigurationReadModel : IProjectionReadModel<ServiceConfigurationReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceConfigurationReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceConfigurationReadModelSupport.ToTimestamp(value);
    }

    public ServiceIdentityReadModel Identity
    {
        get => IdentityValue ??= new ServiceIdentityReadModel();
        set => IdentityValue = value ?? new ServiceIdentityReadModel();
    }

    public IList<ServiceBindingReadModel> Bindings
    {
        get => BindingEntries;
        set => ServiceConfigurationReadModelSupport.ReplaceCollection(BindingEntries, value);
    }

    public IList<ServiceEndpointExposureReadModel> Endpoints
    {
        get => EndpointEntries;
        set => ServiceConfigurationReadModelSupport.ReplaceCollection(EndpointEntries, value);
    }

    public IList<ServicePolicyReadModel> Policies
    {
        get => PolicyEntries;
        set => ServiceConfigurationReadModelSupport.ReplaceCollection(PolicyEntries, value);
    }
}

public sealed partial class ServiceBindingReadModel
{
    public ServiceBindingKind BindingKind
    {
        get => (ServiceBindingKind)BindingKindValue;
        set => BindingKindValue = (int)value;
    }

    public IList<string> PolicyIds
    {
        get => PolicyIdEntries;
        set => ServiceConfigurationReadModelSupport.ReplaceCollection(PolicyIdEntries, value);
    }
}

public sealed partial class ServiceEndpointExposureReadModel
{
    public ServiceEndpointKind Kind
    {
        get => (ServiceEndpointKind)KindValue;
        set => KindValue = (int)value;
    }

    public ServiceEndpointExposureKind ExposureKind
    {
        get => (ServiceEndpointExposureKind)ExposureKindValue;
        set => ExposureKindValue = (int)value;
    }

    public IList<string> PolicyIds
    {
        get => PolicyIdEntries;
        set => ServiceConfigurationReadModelSupport.ReplaceCollection(PolicyIdEntries, value);
    }
}

public sealed partial class ServicePolicyReadModel
{
    public IList<string> ActivationRequiredBindingIds
    {
        get => ActivationRequiredBindingIdEntries;
        set => ServiceConfigurationReadModelSupport.ReplaceCollection(ActivationRequiredBindingIdEntries, value);
    }

    public IList<string> InvokeAllowedCallerServiceKeys
    {
        get => InvokeAllowedCallerServiceKeyEntries;
        set => ServiceConfigurationReadModelSupport.ReplaceCollection(InvokeAllowedCallerServiceKeyEntries, value);
    }
}

internal static class ServiceConfigurationReadModelSupport
{
    public static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    public static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();

    public static void ReplaceCollection<T>(RepeatedField<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source != null)
            target.Add(source);
    }
}
