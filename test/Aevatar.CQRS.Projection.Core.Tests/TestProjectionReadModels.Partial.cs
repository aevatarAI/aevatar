using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed partial class TestStoreReadModel : IProjectionReadModel<TestStoreReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class TestDynamicStoreReadModel : IProjectionReadModel<TestDynamicStoreReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class TestProviderStoreSmokeReadModel : IProjectionReadModel<TestProviderStoreSmokeReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAtEpochMs);
        set => UpdatedAtEpochMs = value.ToUnixTimeMilliseconds();
    }
}
