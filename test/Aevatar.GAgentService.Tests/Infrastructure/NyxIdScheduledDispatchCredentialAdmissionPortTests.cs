using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class NyxIdScheduledDispatchCredentialAdmissionPortTests
{
    [Fact]
    public async Task AdmitAsync_ShouldReturnMissingBindingWithoutQuery_WhenOwnerSubjectIsMissing()
    {
        var queryPort = new RecordingExternalIdentityBindingQueryPort();
        var port = new NyxIdScheduledDispatchCredentialAdmissionPort(queryPort);

        var result = await port.AdmitAsync(CreateRequest(ownerSubject: null));

        result.Status.Should().Be(ScheduledDispatchCredentialAdmissionStatus.MissingBinding);
        result.Error.Should().Contain("owner subject is required");
        queryPort.Subjects.Should().BeEmpty();
    }

    [Fact]
    public async Task AdmitAsync_ShouldReturnMissingBinding_WhenReadModelHasNoBinding()
    {
        var queryPort = new RecordingExternalIdentityBindingQueryPort();
        var port = new NyxIdScheduledDispatchCredentialAdmissionPort(queryPort);

        var result = await port.AdmitAsync(CreateRequest(OwnerSubject()));

        result.Status.Should().Be(ScheduledDispatchCredentialAdmissionStatus.MissingBinding);
        result.Error.Should().Contain("owner binding is required");
        AssertQueriedOwnerSubject(queryPort);
    }

    [Fact]
    public async Task AdmitAsync_ShouldReturnAllowed_WhenReadModelHasBinding()
    {
        var queryPort = new RecordingExternalIdentityBindingQueryPort
        {
            Binding = new BindingId { Value = "bnd-owner-1" },
        };
        var port = new NyxIdScheduledDispatchCredentialAdmissionPort(queryPort);

        var result = await port.AdmitAsync(CreateRequest(OwnerSubject()));

        result.Status.Should().Be(ScheduledDispatchCredentialAdmissionStatus.Allowed);
        result.Error.Should().BeNull();
        AssertQueriedOwnerSubject(queryPort);
    }

    private static ScheduledDispatchCredentialAdmissionRequest CreateRequest(
        ScheduledServiceInvocationNyxIdSubjectRef? ownerSubject) =>
        new(
            new ScheduledDispatchMutationContext("scope-1", ownerSubject),
            new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource("owner-proxy", ownerSubject),
            new ServiceIdentity
            {
                TenantId = "scope-1",
                AppId = "app",
                Namespace = "default",
                ServiceId = "svc",
            });

    private static ScheduledServiceInvocationNyxIdSubjectRef OwnerSubject() =>
        new("nyxid", "tenant-1", "owner-user-1");

    private static void AssertQueriedOwnerSubject(RecordingExternalIdentityBindingQueryPort queryPort)
    {
        var subject = queryPort.Subjects.Should().ContainSingle().Which;
        subject.Platform.Should().Be("nyxid");
        subject.Tenant.Should().Be("tenant-1");
        subject.ExternalUserId.Should().Be("owner-user-1");
    }

    private sealed class RecordingExternalIdentityBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public List<ExternalSubjectRef> Subjects { get; } = [];

        public BindingId? Binding { get; init; }

        public Task<BindingId?> ResolveAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Subjects.Add(new ExternalSubjectRef
            {
                Platform = externalSubject.Platform,
                Tenant = externalSubject.Tenant,
                ExternalUserId = externalSubject.ExternalUserId,
            });
            return Task.FromResult(Binding);
        }
    }
}
