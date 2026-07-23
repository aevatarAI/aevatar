using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScheduledDispatchEndpointsTests
{
    [Theory]
    [InlineData("unexpectedField")]
    [InlineData("owner")]
    [InlineData("teamAutomationOwner")]
    [InlineData("permissionDigest")]
    [InlineData("credentialProvisioningKind")]
    [InlineData("provisioningStatus")]
    [InlineData("teamAutomationLifecycleStatus")]
    public void ConfigurationRequest_ShouldRejectUnmappedOrTrustedLifecycleFields(string propertyName)
    {
        var json = $$"""
            {
              "cronExpression": "0 9 * * *",
              "{{propertyName}}": "forged"
            }
            """;

        var act = () => JsonSerializer.Deserialize<ScheduledDispatchConfigurationHttpRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ServiceInvocationRequest_ShouldRejectForgedAuthorizationFact()
    {
        const string json = """
            {
              "authorizationFact": {
                "permissionDigest": "forged"
              }
            }
            """;

        var act = () => JsonSerializer.Deserialize<ScheduledDispatchServiceInvocationTargetHttpRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task Create_ShouldAcceptEnvelopeTargetAndForwardConfiguration()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateEnvelopeRequest(scheduleId: "schedule-1");

        var result = await CreateAsync(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Created.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                "Daily",
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.Envelope,
                    ActorId: "actor-1",
                    Envelope: request.Envelope!.Envelope),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string> { ["trace"] = "1" })
            {
                CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKind.Envelope,
            });
    }

    [Fact]
    public async Task Create_ShouldRejectRequestsWithoutExactlyOneTarget()
    {
        var result = await CreateAsync(
            new ScheduledDispatchConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                CronExpression = "0 9 * * *",
            },
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldMapConflict()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            CreateException = new ScheduledDispatchConflictException("schedule-1", "Schedule target cannot be prepared."),
        };

        var result = await CreateAsync(CreateEnvelopeRequest(scheduleId: "schedule-1"), service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Create_ShouldNotMapServiceInvalidOperationAsPayloadBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            CreateException = new InvalidOperationException("dispatch runtime failure"),
        };

        var act = () => CreateAsync(CreateEnvelopeRequest(scheduleId: "schedule-1"), service);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch runtime failure");
    }

    [Fact]
    public async Task Update_ShouldUseRouteScheduleIdAsFallback()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await UpdateAsync(
            "route-schedule",
            CreateEnvelopeRequest(scheduleId: null),
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Updated.Should().ContainSingle()
            .Which.Configuration.ScheduleId.Should().Be("route-schedule");
    }

    [Fact]
    public async Task Update_ShouldAcceptServiceInvocationTarget()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var payload = Any.Pack(new StringValue { Value = "run" });
        var request = new ScheduledDispatchConfigurationHttpRequest
        {
            DisplayName = "Run service",
            CronExpression = "0 10 * * *",
            Timezone = "UTC",
            Enabled = false,
            ServiceInvocation = new ScheduledDispatchServiceInvocationTargetHttpRequest
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "tenant",
                    AppId = "app",
                    Namespace = "default",
                    ServiceId = "svc",
                },
                EndpointId = "run",
                PayloadTypeUrl = payload.TypeUrl,
                PayloadBase64 = Convert.ToBase64String(payload.Value.ToByteArray()),
                RevisionId = "rev-1",
                Caller = new ServiceInvocationCaller
                {
                    ServiceKey = "caller-service",
                    TenantId = "tenant",
                    AppId = "app",
                },
            },
        };

        var result = await UpdateAsync("schedule-1", request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var configuration = service.Updated.Should().ContainSingle().Which.Configuration;
        configuration.ScheduleId.Should().Be("schedule-1");
        configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        configuration.Target.ServiceInvocation.Should().NotBeNull();
        configuration.Target.ServiceInvocation!.Identity.ServiceId.Should().Be("svc");
        configuration.Target.ServiceInvocation.EndpointId.Should().Be("run");
        configuration.Target.ServiceInvocation.Payload.Should().Be(payload);
        configuration.Target.ServiceInvocation.RevisionId.Should().Be("rev-1");
        configuration.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenServiceInvocationAuthIsEmpty()
    {
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest());

        var result = await CreateAsync(
            request,
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldPassScopeOwnerMutationContextFromAuthenticatedUser()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
            {
                Scope = " proxy ",
            },
        });
        var result = await CreateAsync(
            request,
            service,
            CreateHttpContext(scopeId: "scope-1", uid: "owner-user-1", sub: "owner-user-subject"));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var auth = service.Created.Should().ContainSingle().Which.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().BeNull();
        auth.ScopeOwnerNyxId.Should().NotBeNull();
        auth.ScopeOwnerNyxId!.Scope.Should().Be("proxy");
        auth.ScopeOwnerNyxId.OwnerSubject.Should().BeEquivalentTo(new ScheduledServiceInvocationNyxIdSubjectRef(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            "owner-user-1"));
        service.CreateContexts.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScheduledDispatchMutationContext(
                "scope-1",
                new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-user-1")));
    }

    [Fact]
    public async Task Create_ShouldMapScopeOwnerMissingBindingFromApplication()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            CreateException = new ArgumentException(
                "Authenticated NyxID owner binding is required for scope owner schedule auth; complete or refresh NyxID login before creating a scope owner schedule."),
        };
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
            {
                Scope = "proxy",
            },
        });

        var result = await CreateAsync(
            request,
            service,
            CreateHttpContext(uid: "owner-user-1"));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ShouldMapScopeOwnerScopeMismatchFromApplicationWithoutIssuingToken()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            CreateException = new ArgumentException(
                "Service invocation target scope must match the authenticated scope for scope owner schedule auth."),
        };
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
            {
                Scope = "schedule:workflow",
            },
        });

        var result = await CreateAsync(
            request,
            service,
            CreateHttpContext(uid: "owner-user-1"));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_ShouldPassScopeOwnerMutationContextFromAuthenticatedUser()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
            {
                Scope = "proxy",
            },
        });
        var result = await UpdateAsync(
            "schedule-owner",
            request,
            service,
            CreateHttpContext(scopeId: "scope-1", uid: "owner-user-1"));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var auth = service.Updated.Should().ContainSingle().Which.Configuration.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.ScopeOwnerNyxId.Should().NotBeNull();
        auth.ScopeOwnerNyxId!.OwnerSubject.Should().BeEquivalentTo(new ScheduledServiceInvocationNyxIdSubjectRef(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            "owner-user-1"));
        service.UpdateContexts.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScheduledDispatchMutationContext(
                "scope-1",
                new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-user-1")));
    }

    [Fact]
    public async Task Create_ShouldRejectDurableSenderBearerToken()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            DurableSenderBearerToken = " durable-sender-token ",
        });

        var result = await CreateAsync(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithScheduledInvocationAgentKeyInHttpAuth_ShouldReturnBadRequest()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));
        var chat = new ChatRequestEvent { Prompt = "run workflow" };

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            scheduleKind = "Workflow",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
                auth = new
                {
                    senderNyxId = new
                    {
                        subject = new
                        {
                            platform = "nyxid",
                            externalUserId = "user-42",
                        },
                        scope = "proxy",
                    },
                    scheduledInvocationAgentKey = new
                    {
                        apiKeyId = "key-schedule",
                    },
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Schedules.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ShouldDefaultMissingScheduleKindToGeneric()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await CreateAsync(CreateEnvelopeRequest(scheduleId: "schedule-1"), service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Created.Should().ContainSingle().Which.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Generic);
    }

    [Fact]
    public async Task Update_ShouldMapScopeOwnerMissingBindingFromApplication()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            UpdateException = new ArgumentException(
                "Authenticated NyxID owner binding is required for scope owner schedule auth; complete or refresh NyxID login before creating a scope owner schedule."),
        };
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
            {
                Scope = "proxy",
            },
        });

        var result = await UpdateAsync(
            "schedule-owner",
            request,
            service,
            CreateHttpContext(uid: "owner-user-1"));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ShouldRejectServiceInvocationAuthWithMultipleCredentialSources()
    {
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
            {
                Scope = "proxy",
            },
            DurableSenderBearerToken = "durable-sender-token",
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefHttpRequest
                {
                    Platform = "lark",
                    ExternalUserId = "ou-user-1",
                },
                Scope = "proxy",
            },
        });

        var result = await CreateAsync(
            request,
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldAcceptTenantlessServiceInvocationNyxIdSubject()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefHttpRequest
                {
                    Platform = "GitHub",
                    ExternalUserId = "ou-user-1",
                },
                Scope = " proxy ",
            },
        });

        var result = await CreateAsync(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var auth = service.Created.Should().ContainSingle().Which.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        var subject = auth.SenderNyxId.Subject;
        subject.Platform.Should().Be("github");
        subject.Tenant.Should().BeEmpty();
        subject.ExternalUserId.Should().Be("ou-user-1");
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenServiceInvocationAuthSubjectIsNull()
    {
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
            {
                Subject = null!,
                Scope = "proxy",
            },
        });

        var result = await CreateAsync(
            request,
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenServiceInvocationAuthFieldsAreBlank()
    {
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefHttpRequest
                {
                    Platform = " ",
                    Tenant = "tenant-1",
                    ExternalUserId = "ou-user-1",
                },
                Scope = "",
            },
        });

        var result = await CreateAsync(
            request,
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Enable_ShouldMapNotFound()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            EnableException = new ScheduledDispatchNotFoundException("missing"),
        };

        var result = await ScheduledDispatchEndpoints.Enable(
            "missing",
            new ScheduledDispatchStateChangeHttpRequest { Reason = "resume" },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        service.Enabled.Should().ContainSingle().Which.Should().Be(("missing", "resume"));
    }

    [Fact]
    public async Task Enable_ShouldDefaultEmptyReasonAndMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            EnableException = new ArgumentException("invalid id"),
        };

        var result = await ScheduledDispatchEndpoints.Enable("invalid/id", null, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Enabled.Should().ContainSingle().Which.Should().Be(("invalid/id", string.Empty));
    }

    [Fact]
    public async Task Disable_ShouldMapConflict()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            DisableException = new ScheduledDispatchConflictException("schedule-1", "Schedule cannot be disabled."),
        };

        var result = await ScheduledDispatchEndpoints.Disable(
            "schedule-1",
            new ScheduledDispatchStateChangeHttpRequest { Reason = "pause" },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        service.Disabled.Should().ContainSingle().Which.Should().Be(("schedule-1", "pause"));
    }

    [Fact]
    public async Task Disable_ShouldAccept()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.Disable("schedule-1", null, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Disabled.Should().ContainSingle().Which.Should().Be(("schedule-1", string.Empty));
    }

    [Fact]
    public async Task Delete_ShouldAcceptReasonFromQueryAndMapNotFound()
    {
        var acceptedService = new RecordingScheduledDispatchApplicationService();
        var notFoundService = new RecordingScheduledDispatchApplicationService
        {
            DeleteException = new ScheduledDispatchNotFoundException("missing"),
        };

        var accepted = await ScheduledDispatchEndpoints.Delete(
            "schedule-1",
            "cleanup",
            null,
            acceptedService);
        var notFound = await ScheduledDispatchEndpoints.Delete(
            "missing",
            null,
            new ScheduledDispatchStateChangeHttpRequest { Reason = "body" },
            notFoundService);

        var acceptedHttp = CreateHttpContext();
        await accepted.ExecuteAsync(acceptedHttp);
        var notFoundHttp = CreateHttpContext();
        await notFound.ExecuteAsync(notFoundHttp);

        acceptedHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        notFoundHttp.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        acceptedService.Deleted.Should().ContainSingle().Which.Should().Be(("schedule-1", "cleanup"));
        notFoundService.Deleted.Should().ContainSingle().Which.Should().Be(("missing", "body"));
    }

    [Fact]
    public async Task List_ShouldForwardScopeAndPageQueryParameters()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.List(
            service,
            scopeId: "scope-alpha",
            teamId: "team-alpha",
            memberId: "member-alpha",
            take: 25,
            cursor: "cursor-1",
            includeTotalCount: true);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastListQuery.Should().Be(new ScheduledDispatchListQuery(
            Take: 25,
            Cursor: "cursor-1",
            IncludeTotalCount: true,
            TeamAutomationScopeId: "scope-alpha",
            TeamAutomationTeamId: "team-alpha",
            TeamAutomationMemberId: "member-alpha"));
    }

    [Fact]
    public async Task List_WhenScopeIdMissing_ShouldReturnBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.List(service, scopeId: null);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.LastListQuery.Should().BeNull();
    }

    [Fact]
    public async Task Get_ShouldReturnOkAndNotFound()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            Detail = CreateDetail("schedule-1"),
        };

        var notFoundService = new RecordingScheduledDispatchApplicationService();
        var ok = await ScheduledDispatchEndpoints.Get(
            "schedule-1",
            service,
            scopeId: "scope-alpha",
            teamId: "team-alpha",
            memberId: "member-alpha");
        var notFound = await ScheduledDispatchEndpoints.Get(
            "missing",
            notFoundService,
            scopeId: "scope-alpha");

        var okHttp = CreateHttpContext();
        await ok.ExecuteAsync(okHttp);
        var notFoundHttp = CreateHttpContext();
        await notFound.ExecuteAsync(notFoundHttp);

        okHttp.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        notFoundHttp.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        service.LastTeamScheduleGet.Should().Be(("schedule-1", "scope-alpha", "team-alpha", "member-alpha"));
        notFoundService.LastTeamScheduleGet.Should().NotBeNull();
        notFoundService.LastTeamScheduleGet!.Value.ScheduleId.Should().Be("missing");
        notFoundService.LastTeamScheduleGet.Value.ScopeId.Should().Be("scope-alpha");
        notFoundService.LastTeamScheduleGet.Value.TeamId.Should().BeNull();
        notFoundService.LastTeamScheduleGet.Value.MemberId.Should().BeNull();
    }

    [Fact]
    public async Task Get_WhenScopeIdMissing_ShouldReturnBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.Get("schedule-1", service, scopeId: null);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.LastTeamScheduleGet.Should().BeNull();
    }

    [Fact]
    public async Task Get_ShouldMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            GetException = new ArgumentException("invalid id"),
        };

        var result = await ScheduledDispatchEndpoints.Get("invalid/id", service, scopeId: "scope-alpha");

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Preview_ShouldForwardDefaultsAndMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            PreviewException = new ArgumentException("invalid cron"),
        };

        var result = await ScheduledDispatchEndpoints.Preview(
            new ScheduledDispatchPreviewHttpRequest
            {
                CronExpression = "invalid",
                Timezone = "UTC",
                Count = 0,
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.LastPreviewCount.Should().Be(5);
    }

    [Fact]
    public async Task Preview_ShouldReturnOccurrences()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var fromUtc = new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero);

        var result = await ScheduledDispatchEndpoints.Preview(
            new ScheduledDispatchPreviewHttpRequest
            {
                CronExpression = "0 9 * * *",
                Timezone = "UTC",
                Count = 2,
                FromUtc = fromUtc,
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastPreviewCount.Should().Be(2);
        service.LastPreviewFromUtc.Should().Be(fromUtc);
    }

    [Fact]
    public async Task RunNow_ShouldAcceptAndMapNotFound()
    {
        var accepted = await ScheduledDispatchEndpoints.RunNow(
            "schedule-1",
            new RecordingScheduledDispatchApplicationService());
        var notFound = await ScheduledDispatchEndpoints.RunNow(
            "missing",
            new RecordingScheduledDispatchApplicationService
            {
                RunNowException = new ScheduledDispatchNotFoundException("missing"),
            });

        var acceptedHttp = CreateHttpContext();
        await accepted.ExecuteAsync(acceptedHttp);
        var notFoundHttp = CreateHttpContext();
        await notFound.ExecuteAsync(notFoundHttp);

        acceptedHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        notFoundHttp.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task RunNow_ShouldMapConflict()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            RunNowException = new ScheduledDispatchConflictException("schedule-1", "Schedule is disabled."),
        };

        var result = await ScheduledDispatchEndpoints.RunNow("schedule-1", service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Create_WithServiceInvocationPayloadBase64Json_ShouldBindAndPackPayload()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));
        var chat = new ChatRequestEvent { Prompt = "summarize status" };

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
                auth = SenderNyxIdAuth(),
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var configuration = host.Schedules.Created.Should().ContainSingle().Which;
        configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        configuration.Target.ServiceInvocation.Should().NotBeNull();
        var invocation = configuration.Target.ServiceInvocation!;
        invocation.EndpointId.Should().Be("chat");
        invocation.Payload.TypeUrl.Should().Be("type.googleapis.com/aevatar.ai.ChatRequestEvent");
        invocation.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("summarize status");
        invocation.RevisionId.Should().Be("rev-chat");
        invocation.Auth.Should().NotBeNull();
        invocation.Auth!.SenderNyxId.Should().NotBeNull();
        invocation.Auth.ScopeOwnerNyxId.Should().BeNull();
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        host.CredentialExchange.ScopeOwnerSources.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithWorkflowServiceInvocationAndOmittedAuth_ShouldDefaultScopeOwnerAuth()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));
        var chat = new ChatRequestEvent { Prompt = "run workflow" };

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var invocation = host.Schedules.Created.Should().ContainSingle().Which.Target.ServiceInvocation;
        invocation.Should().NotBeNull();
        invocation!.Auth.Should().NotBeNull();
        invocation.Auth!.SenderNyxId.Should().BeNull();
        invocation.Auth.ScopeOwnerNyxId.Should().NotBeNull();
        invocation.Auth.ScopeOwnerNyxId!.Scope.Should().Be("proxy");
    }

    [Fact]
    public async Task Create_WithWorkflowScheduleKindAndSenderNyxId_ShouldForwardScheduleKind()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));
        var chat = new ChatRequestEvent { Prompt = "run workflow" };

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            scheduleKind = "Workflow",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
                auth = new
                {
                    senderNyxId = new
                    {
                        subject = new
                        {
                            platform = "nyxid",
                            tenant = "tenant-1",
                            externalUserId = "user-42",
                        },
                        scope = "proxy",
                    },
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var configuration = host.Schedules.Created.Should().ContainSingle().Which;
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        var auth = configuration.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        auth.Durable.Should().BeNull();
    }

    [Fact]
    public async Task Create_WithWorkflowScheduleKindAndEnvelopeTarget_ShouldReturnBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateEnvelopeRequest(scheduleId: "schedule-1") with
        {
            ScheduleKind = ScheduledDispatchScheduleKind.Workflow,
        };

        var result = await CreateAsync(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithStaticServiceInvocationAndOmittedAuth_ShouldNotDefaultAuth()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor, ServiceImplementationKind.Static));
        var chat = new ChatRequestEvent { Prompt = "run static" };

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Static chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var configuration = host.Schedules.Created.Should().ContainSingle().Which;
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Generic);
        configuration.Target.ServiceInvocation!.Auth.Should().BeNull();
    }

    [Fact]
    public async Task Create_WithWorkflowScheduleKindAndStaticServiceInvocation_ShouldReturnBadRequest()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor, ServiceImplementationKind.Static));
        var chat = new ChatRequestEvent { Prompt = "run static" };

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Static chat",
            scheduleKind = "Workflow",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Schedules.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_WithWorkflowScheduleKindAndScriptingServiceInvocation_ShouldReturnBadRequest()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor, ServiceImplementationKind.Scripting));
        var chat = new ChatRequestEvent { Prompt = "run script" };

        var response = await host.Client.PutAsJsonAsync("/api/schedules/schedule-chat", new
        {
            displayName = "Script chat",
            scheduleKind = "Workflow",
            cronExpression = "0 10 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Schedules.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_WithWorkflowServiceInvocationAndOmittedAuth_ShouldDefaultScopeOwnerAuth()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));
        var chat = new ChatRequestEvent { Prompt = "refresh standup" };

        var response = await host.Client.PutAsJsonAsync("/api/schedules/schedule-chat", new
        {
            displayName = "Workflow chat",
            cronExpression = "0 10 * * *",
            timezone = "UTC",
            enabled = false,
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var invocation = host.Schedules.Updated.Should().ContainSingle().Which.Configuration.Target.ServiceInvocation;
        invocation.Should().NotBeNull();
        invocation!.Auth.Should().NotBeNull();
        invocation.Auth!.SenderNyxId.Should().BeNull();
        invocation.Auth.ScopeOwnerNyxId.Should().NotBeNull();
        invocation.Auth.ScopeOwnerNyxId!.Scope.Should().Be("proxy");
    }

    [Fact]
    public async Task Update_WithServiceInvocationPayloadBase64Json_ShouldBindAndPackPayload()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));
        var chat = new ChatRequestEvent { Prompt = "refresh standup" };

        var response = await host.Client.PutAsJsonAsync("/api/schedules/schedule-chat", new
        {
            displayName = "Workflow chat",
            cronExpression = "0 10 * * *",
            timezone = "UTC",
            enabled = false,
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
                auth = new
                {
                    senderNyxId = new
                    {
                        subject = new
                        {
                            platform = "nyxid",
                            tenant = "tenant-1",
                            externalUserId = "user-42",
                        },
                        scope = "proxy",
                    },
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var configuration = host.Schedules.Updated.Should().ContainSingle().Which.Configuration;
        configuration.ScheduleId.Should().Be("schedule-chat");
        configuration.Enabled.Should().BeFalse();
        configuration.Target.ServiceInvocation.Should().NotBeNull();
        var invocation = configuration.Target.ServiceInvocation!;
        invocation.EndpointId.Should().Be("chat");
        invocation.Payload.TypeUrl.Should().Be("type.googleapis.com/aevatar.ai.ChatRequestEvent");
        invocation.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("refresh standup");
        invocation.Auth.Should().NotBeNull();
        invocation.Auth!.SenderNyxId.Should().NotBeNull();
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
    }

    [Fact]
    public async Task Create_WithInvalidServiceInvocationPayloadBase64_ShouldReturnStructuredBadRequest()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = "not-base64",
            },
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].ToString().Should().Be("INVALID_SCHEDULED_DISPATCH_REQUEST");
        body["message"].ToString().Should().Contain("payloadBase64");
        host.Schedules.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithServiceInvocationPayloadJson_ShouldResolveActiveRevisionAndPackPayload()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-active");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-active",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadJson = """{"prompt":"json prompt"}""",
                auth = SenderNyxIdAuth(),
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var configuration = host.Schedules.Created.Should().ContainSingle().Which;
        var serviceInvocation = configuration.Target.ServiceInvocation;
        serviceInvocation.Should().NotBeNull();
        var invocation = serviceInvocation!;
        invocation.RevisionId.Should().Be("rev-active");
        invocation.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("json prompt");
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
    }

    [Fact]
    public async Task Create_WithServiceInvocationPayloadJson_ShouldInferKindFromResolvedActiveRevision()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(
            activeRevisionId: "rev-active-workflow",
            defaultServingRevisionId: "rev-default-static");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-default-static",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor, ServiceImplementationKind.Static));
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-active-workflow",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadJson = """{"prompt":"json prompt"}""",
                auth = SenderNyxIdAuth(),
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var configuration = host.Schedules.Created.Should().ContainSingle().Which;
        configuration.Target.ServiceInvocation!.RevisionId.Should().Be("rev-active-workflow");
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
    }

    [Fact]
    public async Task Create_WithExplicitGenericScheduleKindAndWorkflowServiceInvocation_ShouldInferWorkflow()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();
        host.CatalogReader.Service = CreateServiceCatalog(activeRevisionId: "rev-chat");
        host.RevisionCatalog.UpsertRevision(
            "tenant:app:default:workflow",
            "rev-chat",
            BuildPreparedArtifact(ChatRequestEvent.Descriptor));
        var chat = new ChatRequestEvent { Prompt = "run generic" };

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            scheduleKind = "Generic",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
                auth = SenderNyxIdAuth(),
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.Schedules.Created.Should().ContainSingle()
            .Which.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
    }

    [Fact]
    public async Task Create_WithServiceInvocationPayloadJsonWithoutRevision_ShouldReturnBadRequest()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadJson = """{"prompt":"json prompt"}""",
                auth = SenderNyxIdAuth(),
            },
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SCHEDULED_DISPATCH_REQUEST");
        body["message"].Should().Contain("revisionId");
        host.Schedules.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithNoOrMultipleTargets_ShouldKeepExactlyOneTargetValidation()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();

        var none = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
        });
        var both = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-chat",
            displayName = "Workflow chat",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            envelope = new
            {
                actorId = "actor-1",
                envelope = new
                {
                    id = "template",
                    payload = new
                    {
                        typeUrl = Any.Pack(new StringValue()).TypeUrl,
                        value = Convert.ToBase64String(new StringValue { Value = "run" }.ToByteArray()),
                    },
                },
            },
            serviceInvocation = new
            {
                identity = new
                {
                    tenantId = "tenant",
                    appId = "app",
                    @namespace = "default",
                    serviceId = "workflow",
                },
                endpointId = "chat",
                payloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                payloadBase64 = Convert.ToBase64String(new ChatRequestEvent { Prompt = "run" }.ToByteArray()),
            },
        });

        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Schedules.Created.Should().BeEmpty();
    }

    private static ScheduledDispatchConfigurationHttpRequest CreateEnvelopeRequest(string? scheduleId) =>
        new()
        {
            ScheduleId = scheduleId,
            DisplayName = "Daily",
            CronExpression = "0 9 * * *",
            Timezone = "UTC",
            Enabled = true,
            Headers = new Dictionary<string, string> { ["trace"] = "1" },
            Envelope = new ScheduledDispatchEnvelopeTargetHttpRequest
            {
                ActorId = "actor-1",
                Envelope = new EventEnvelope
                {
                    Id = "template",
                    Payload = Any.Pack(new StringValue { Value = "run" }),
                },
            },
        };

    private static ScheduledDispatchConfigurationHttpRequest CreateServiceInvocationRequestWithAuth(
        ScheduledServiceInvocationAuthHttpRequest auth) =>
        new()
        {
            ScheduleId = "schedule-1",
            DisplayName = "Run service",
            CronExpression = "0 10 * * *",
            Timezone = "UTC",
            Enabled = false,
            ServiceInvocation = new ScheduledDispatchServiceInvocationTargetHttpRequest
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "tenant",
                    AppId = "app",
                    Namespace = "default",
                    ServiceId = "svc",
                },
                EndpointId = "run",
                PayloadTypeUrl = Any.Pack(new StringValue()).TypeUrl,
                PayloadBase64 = Convert.ToBase64String(new StringValue { Value = "run" }.ToByteArray()),
                Auth = auth,
            },
        };

    private static object SenderNyxIdAuth() => new
    {
        senderNyxId = new
        {
            subject = new
            {
                platform = "nyxid",
                tenant = "tenant-1",
                externalUserId = "user-42",
            },
            scope = "proxy",
        },
    };

    private static Task<IResult> CreateAsync(
        ScheduledDispatchConfigurationHttpRequest request,
        RecordingScheduledDispatchApplicationService service,
        HttpContext? http = null) =>
        ScheduledDispatchEndpoints.Create(
            http ?? CreateHttpContext(),
            request,
            service,
            new FakeServiceCatalogQueryReader(),
            new FakeServiceRevisionCatalogQueryReader());

    private static Task<IResult> UpdateAsync(
        string scheduleId,
        ScheduledDispatchConfigurationHttpRequest request,
        RecordingScheduledDispatchApplicationService service,
        HttpContext? http = null) =>
        ScheduledDispatchEndpoints.Update(
            http ?? CreateHttpContext(),
            scheduleId,
            request,
            service,
            new FakeServiceCatalogQueryReader(),
            new FakeServiceRevisionCatalogQueryReader());

    private static ServiceCatalogSnapshot CreateServiceCatalog(
        string activeRevisionId,
        string defaultServingRevisionId = "") =>
        new(
            "tenant:app:default:workflow",
            "tenant",
            "app",
            "default",
            "workflow",
            "Workflow",
            defaultServingRevisionId,
            activeRevisionId,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            DateTimeOffset.UtcNow);

    private static PreparedServiceRevisionArtifact BuildPreparedArtifact(
        MessageDescriptor descriptor,
        ServiceImplementationKind implementationKind = ServiceImplementationKind.Workflow,
        string endpointId = "chat") =>
        new()
        {
            ImplementationKind = implementationKind,
            ProtocolDescriptorSet = BuildProtocolDescriptorSetFor(descriptor),
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = endpointId,
                    DisplayName = endpointId,
                    Kind = ServiceEndpointKind.Command,
                    RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                },
            },
        };

    private static ByteString BuildProtocolDescriptorSetFor(MessageDescriptor descriptor)
    {
        var fds = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectFileProto(descriptor.File, fds, seen);
        return fds.ToByteString();
    }

    private static void CollectFileProto(FileDescriptor file, FileDescriptorSet fds, ISet<string> seen)
    {
        if (!seen.Add(file.Name))
            return;

        foreach (var dependency in file.Dependencies)
            CollectFileProto(dependency, fds, seen);

        fds.File.Add(FileDescriptorProto.Parser.ParseFrom(file.SerializedData));
    }

    private static ScheduledDispatchDetail CreateDetail(string scheduleId) =>
        new(
            new ScheduledDispatchSummary(
                scheduleId,
                "Daily",
                ScheduledDispatchTargetKind.Envelope,
                "actor-1",
                Any.Pack(new StringValue { Value = "run" }).TypeUrl,
                string.Empty,
                string.Empty,
                string.Empty,
                "0 9 * * *",
                "UTC",
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                new Dictionary<string, string>(),
                "actor:schedule-1",
                string.Empty),
            []);

    private static DefaultHttpContext CreateHttpContext(
        string? scopeId = null,
        string? uid = null,
        string? sub = null,
        string? nameIdentifier = null,
        string? userId = null)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        var claims = new List<Claim>();
        if (!string.IsNullOrWhiteSpace(scopeId))
            claims.Add(new Claim("scope_id", scopeId));
        if (!string.IsNullOrWhiteSpace(uid))
            claims.Add(new Claim("uid", uid));
        if (!string.IsNullOrWhiteSpace(sub))
            claims.Add(new Claim("sub", sub));
        if (!string.IsNullOrWhiteSpace(nameIdentifier))
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        if (!string.IsNullOrWhiteSpace(userId))
            claims.Add(new Claim("user_id", userId));

        if (claims.Count > 0)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                "test"));
        }

        http.Response.Body = new MemoryStream();
        return http;
    }

    private sealed class ScheduleEndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScheduleEndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingScheduledDispatchApplicationService schedules,
            FakeServiceCatalogQueryReader catalogReader,
            FakeServiceRevisionCatalogQueryReader revisionCatalog,
            FakeScheduledServiceInvocationCredentialExchangePort credentialExchange)
        {
            _app = app;
            Client = client;
            Schedules = schedules;
            CatalogReader = catalogReader;
            RevisionCatalog = revisionCatalog;
            CredentialExchange = credentialExchange;
        }

        public HttpClient Client { get; }

        public RecordingScheduledDispatchApplicationService Schedules { get; }

        public FakeServiceCatalogQueryReader CatalogReader { get; }

        public FakeServiceRevisionCatalogQueryReader RevisionCatalog { get; }

        public FakeScheduledServiceInvocationCredentialExchangePort CredentialExchange { get; }

        public static async Task<ScheduleEndpointTestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var schedules = new RecordingScheduledDispatchApplicationService();
            var catalogReader = new FakeServiceCatalogQueryReader();
            var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
            var bindingQuery = new FakeExternalIdentityBindingQueryPort();
            bindingQuery.Bindings[SubjectKey(OwnerSubject("owner-user-1"))] = "bnd-owner-1";
            var credentialExchange = new FakeScheduledServiceInvocationCredentialExchangePort();
            builder.Services.AddSingleton<IScheduledDispatchApplicationService>(schedules);
            builder.Services.AddSingleton<IServiceCatalogQueryReader>(catalogReader);
            builder.Services.AddSingleton<IServiceRevisionCatalogQueryReader>(revisionCatalog);
            builder.Services.AddSingleton<IExternalIdentityBindingQueryPort>(bindingQuery);
            builder.Services.AddSingleton<IScheduledServiceInvocationCredentialExchangePort>(credentialExchange);

            var app = builder.Build();
            app.Use(static (context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("scope_id", "tenant"), new Claim("uid", "owner-user-1")],
                    "test"));
                return next(context);
            });
            ScheduledDispatchEndpoints.Map(app.MapGroup("/api"));
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new ScheduleEndpointTestHost(app, client, schedules, catalogReader, revisionCatalog, credentialExchange);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class FakeServiceCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? Service { get; set; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Service);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(
            int take = 1000,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Service == null ? [] : [Service]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Service == null ? [] : [Service]);
    }

    private sealed class FakeServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        private readonly Dictionary<string, PreparedServiceRevisionArtifact> _revisionCatalog = new(StringComparer.Ordinal);

        public void UpsertRevision(string serviceKey, string revisionId, PreparedServiceRevisionArtifact artifact)
        {
            var clone = artifact.Clone();
            clone.RevisionId = revisionId;
            _revisionCatalog[$"{serviceKey}:{revisionId}"] = clone;
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            var serviceKey = $"{identity.TenantId}:{identity.AppId}:{identity.Namespace}:{identity.ServiceId}";
            var revisions = _revisionCatalog
                .Where(x => x.Key.StartsWith(serviceKey + ":", StringComparison.Ordinal))
                .Select(x => x.Value)
                .Select(artifact => new ServiceRevisionSnapshot(
                    artifact.RevisionId,
                    artifact.ImplementationKind.ToString(),
                    ServiceRevisionStatus.Prepared.ToString(),
                    artifact.ArtifactHash,
                    string.Empty,
                    artifact.Endpoints.Select(endpoint => new ServiceEndpointSnapshot(
                        endpoint.EndpointId,
                        endpoint.DisplayName,
                        endpoint.Kind.ToString(),
                        endpoint.RequestTypeUrl,
                        endpoint.ResponseTypeUrl,
                        endpoint.Description)).ToList(),
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null,
                    artifact.Clone()))
                .ToList();

            return Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
                serviceKey,
                revisions,
                DateTimeOffset.UtcNow,
                revisions.Count,
                string.Empty));
        }
    }

    private sealed class RecordingScheduledDispatchApplicationService : IScheduledDispatchApplicationService
    {
        public List<ScheduledDispatchConfiguration> Created { get; } = [];
        public List<ScheduledDispatchMutationContext?> CreateContexts { get; } = [];
        public List<ScheduledDispatchConfiguration> Ensured { get; } = [];
        public List<ScheduledDispatchMutationContext?> EnsureContexts { get; } = [];
        public List<(string ScheduleId, ScheduledDispatchConfiguration Configuration)> Updated { get; } = [];
        public List<ScheduledDispatchMutationContext?> UpdateContexts { get; } = [];
        public List<(string ScheduleId, string Reason)> Enabled { get; } = [];
        public List<(string ScheduleId, string Reason)> Disabled { get; } = [];
        public List<(string ScheduleId, string Reason)> Deleted { get; } = [];
        public int? LastListTake { get; private set; }
        public string? LastListCursor { get; private set; }
        public bool? LastListIncludeTotalCount { get; private set; }
        public ScheduledDispatchListQuery? LastListQuery { get; private set; }
        public (string ScheduleId, string ScopeId, string? TeamId, string? MemberId)? LastTeamScheduleGet { get; private set; }
        public int? LastPreviewCount { get; private set; }
        public DateTimeOffset? LastPreviewFromUtc { get; private set; }
        public ScheduledDispatchDetail? Detail { get; set; }
        public Exception? CreateException { get; set; }
        public Exception? UpdateException { get; set; }
        public Exception? EnableException { get; set; }
        public Exception? DisableException { get; set; }
        public Exception? DeleteException { get; set; }
        public Exception? GetException { get; set; }
        public Exception? PreviewException { get; set; }
        public Exception? RunNowException { get; set; }

        public Task<ScheduledDispatchMutationReceipt> CreateAsync(
            ScheduledDispatchConfiguration configuration,
            ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
        {
            if (CreateException != null)
                throw CreateException;

            AdmitCredentialRequirement(configuration, ScheduledDispatchCredentialRequirementOperation.Create);
            Created.Add(configuration);
            CreateContexts.Add(context);
            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                configuration.ScheduleId,
                $"actor:{configuration.ScheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
            ScheduledDispatchConfiguration configuration,
            ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
        {
            AdmitCredentialRequirement(configuration, ScheduledDispatchCredentialRequirementOperation.Ensure);
            Ensured.Add(configuration);
            EnsureContexts.Add(context);
            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                configuration.ScheduleId,
                $"actor:{configuration.ScheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> UpdateAsync(
            string scheduleId,
            ScheduledDispatchConfiguration configuration,
            ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
        {
            if (UpdateException != null)
                throw UpdateException;

            AdmitCredentialRequirement(configuration, ScheduledDispatchCredentialRequirementOperation.Update);
            Updated.Add((scheduleId, configuration));
            UpdateContexts.Add(context);
            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> EnableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default)
        {
            Enabled.Add((scheduleId, reason));
            if (EnableException != null)
                throw EnableException;

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> DisableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default)
        {
            Disabled.Add((scheduleId, reason));
            if (DisableException != null)
                throw DisableException;

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> DeleteAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default)
        {
            Deleted.Add((scheduleId, reason));
            if (DeleteException != null)
                throw DeleteException;

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
        {
            if (GetException != null)
                throw GetException;

            return Task.FromResult(Detail?.Schedule.ScheduleId == scheduleId ? Detail : null);
        }

        public Task<ScheduledDispatchDetail?> GetTeamScheduleAsync(
            string scheduleId,
            string scopeId,
            string? teamId = null,
            string? memberId = null,
            CancellationToken ct = default)
        {
            LastTeamScheduleGet = (scheduleId, scopeId, teamId, memberId);
            if (GetException != null)
                throw GetException;

            return Task.FromResult(Detail?.Schedule.ScheduleId == scheduleId ? Detail : null);
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            LastListTake = take;
            LastListCursor = cursor;
            LastListIncludeTotalCount = includeTotalCount;
            return Task.FromResult(new ScheduledDispatchListResult([], null, includeTotalCount ? 0 : null));
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query,
            CancellationToken ct = default)
        {
            LastListTake = query.Take;
            LastListCursor = query.Cursor;
            LastListIncludeTotalCount = query.IncludeTotalCount;
            LastListQuery = query;
            return Task.FromResult(new ScheduledDispatchListResult([], null, query.IncludeTotalCount ? 0 : null));
        }

        public Task<ScheduledDispatchPreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default)
        {
            LastPreviewCount = count;
            LastPreviewFromUtc = fromUtc;
            if (PreviewException != null)
                throw PreviewException;

            return Task.FromResult(new ScheduledDispatchPreview(
                cronExpression,
                timezone ?? "UTC",
                [new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)]));
        }

        public Task<ScheduledDispatchRunNowReceipt> RunNowAsync(string scheduleId, CancellationToken ct = default)
        {
            if (RunNowException != null)
                throw RunNowException;

            return Task.FromResult(new ScheduledDispatchRunNowReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                DateTimeOffset.UtcNow,
                "run-now:schedule-1",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        private static void AdmitCredentialRequirement(
            ScheduledDispatchConfiguration configuration,
            ScheduledDispatchCredentialRequirementOperation operation)
        {
            var request = ScheduledDispatchCredentialRequirementRequests.FromConfiguration(configuration, operation);
            var decision = DefaultScheduledDispatchCredentialRequirementPolicy.Instance.Evaluate(request);
            if (!decision.Allowed)
                throw new ArgumentException(decision.Message, nameof(configuration));
        }
    }

    private static ExternalSubjectRef OwnerSubject(string externalUserId) =>
        new()
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = externalUserId,
        };

    private static string SubjectKey(ExternalSubjectRef subject) =>
        $"{subject.Platform}:{subject.Tenant}:{subject.ExternalUserId}";

    private sealed class FakeExternalIdentityBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal);

        public Task<BindingId?> ResolveAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default)
        {
            return Task.FromResult(Bindings.TryGetValue(SubjectKey(externalSubject), out var bindingId)
                ? new BindingId { Value = bindingId }
                : null);
        }
    }

    private sealed class FakeScheduledServiceInvocationCredentialExchangePort : IScheduledServiceInvocationCredentialExchangePort
    {
        public ScheduledServiceInvocationCredentialExchangeResult ScopeOwnerExchangeResult { get; init; } =
            ScheduledServiceInvocationCredentialExchangeResult.Success(
                "owner-token",
                DateTimeOffset.UtcNow.AddMinutes(5));

        public List<ScheduledServiceInvocationNyxIdCredentialSource> ScopeOwnerSources { get; } = [];

        public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueNyxIdAsync(
            ScheduledServiceInvocationNyxIdCredentialSource source,
            CancellationToken ct = default)
        {
            if (source.Role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner)
            {
                ScopeOwnerSources.Add(source);
                return Task.FromResult(ScopeOwnerExchangeResult);
            }

            return Task.FromResult(ScheduledServiceInvocationCredentialExchangeResult.Success(
                "sender-token",
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }
}
