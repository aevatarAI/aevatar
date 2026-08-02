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
using Aevatar.Studio.Application.Provisioning;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScheduledDispatchEndpointsTests
{
    [Theory]
    [InlineData("unexpectedField")]
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
    public void ConfigurationRequest_ShouldNotExposeRawEnvelopeTarget()
    {
        typeof(ScheduledDispatchConfigurationHttpRequest)
            .GetProperty("Envelope")
            .Should().BeNull();
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
    public async Task Create_HttpRoute_ShouldRejectRawEnvelopeTarget()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/schedules", new
        {
            scheduleId = "schedule-alpha",
            cronExpression = "0 9 * * *",
            envelope = new
            {
                actorId = "actor-cross-owner",
                envelope = new
                {
                    payload = new
                    {
                        typeUrl = "type.googleapis.com/aevatar.workflow.WorkflowStoppedEvent",
                        value = Convert.ToBase64String(Array.Empty<byte>()),
                    },
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Schedules.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ShouldAcceptServiceInvocationTargetAndForwardConfiguration()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequest(scheduleId: "schedule-1");

        var result = await CreateAsync(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var configuration = service.Created.Should().ContainSingle().Which;
        configuration.ScheduleId.Should().Be("schedule-1");
        configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        configuration.Target.ServiceInvocation!.Identity.TenantId.Should().Be("tenant");
        configuration.Target.ServiceInvocation.EndpointId.Should().Be("run");
    }

    [Fact]
    public async Task Create_ShouldForwardTypedStudioMemberAutomationOwner()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequest(scheduleId: "sch-alpha") with
        {
            Owner = StudioMemberAutomationOwnerRequest(),
        };

        var result = await CreateAsync(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be(
            "/api/schedules/sch-alpha?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha");
        var owner = new TeamMemberAutomationOwner("scope-alpha", "m-alpha", "team-alpha");
        service.Created.Should().ContainSingle().Which.TeamAutomationOwner.Should().Be(owner);
        service.CreateContexts.Should().ContainSingle().Which!.TeamAutomationOwner.Should().Be(owner);
    }

    [Fact]
    public async Task Create_ShouldRejectTypedOwnerWhenAuthenticatedScopeDiffers()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequest(scheduleId: "sch-alpha") with
        {
            Owner = StudioMemberAutomationOwnerRequest(),
        };

        var result = await CreateAsync(
            request,
            service,
            CreateHttpContext(scopeId: "scope-beta", authenticationEnabled: true));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.Created.Should().BeEmpty();
        service.CreateContexts.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_HttpRoute_ShouldBindTypedOwnerAndReturnOwnerAwareLocation()
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
            scheduleId = "sch-alpha",
            displayName = "Daily",
            cronExpression = "0 9 * * *",
            timezone = "UTC",
            enabled = true,
            owner = new
            {
                kind = ScheduledDispatchOwnerKinds.StudioMemberAutomation,
                scopeId = "tenant",
                teamId = "team-alpha",
                memberId = "m-alpha",
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
                payloadBase64 = Convert.ToBase64String(chat.ToByteArray()),
                revisionId = "rev-chat",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().Should().Be(
            "/api/schedules/sch-alpha?ownerKind=studio_member_automation&ownerScopeId=tenant&ownerTeamId=team-alpha&ownerMemberId=m-alpha");
        var owner = new TeamMemberAutomationOwner("tenant", "m-alpha", "team-alpha");
        host.Schedules.Created.Should().ContainSingle().Which.TeamAutomationOwner.Should().Be(owner);
        host.Schedules.CreateContexts.Should().ContainSingle().Which!.TeamAutomationOwner.Should().Be(owner);
    }

    [Fact]
    public async Task List_HttpRoute_ShouldBindTypedOwnerQuery()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();

        var response = await host.Client.GetAsync(
            "/api/schedules?ownerKind=studio_member_automation&ownerScopeId=tenant&ownerTeamId=team-alpha&ownerMemberId=m-alpha&take=17&cursor=next&includeTotalCount=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Schedules.LastListQuery.Should().NotBeNull();
        host.Schedules.LastListQuery!.TeamAutomationOwner.Should().Be(
            new TeamMemberAutomationOwner("tenant", "m-alpha", "team-alpha"));
        host.Schedules.LastListQuery.Take.Should().Be(17);
        host.Schedules.LastListQuery.Cursor.Should().Be("next");
        host.Schedules.LastListQuery.IncludeTotalCount.Should().BeTrue();
    }

    [Fact]
    public async Task RunNow_HttpRoute_ShouldBindTypedOwnerBodyAndReturnOwnerAwareLocation()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/schedules/sch-alpha:run-now", new
        {
            owner = new
            {
                kind = ScheduledDispatchOwnerKinds.StudioMemberAutomation,
                scopeId = "tenant",
                teamId = "team-alpha",
                memberId = "m-alpha",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().Should().Be(
            "/api/schedules/sch-alpha?ownerKind=studio_member_automation&ownerScopeId=tenant&ownerTeamId=team-alpha&ownerMemberId=m-alpha");
        host.Schedules.TeamRunNow.Should().ContainSingle().Which.Should().Be(
            ("sch-alpha", new TeamMemberAutomationOwner("tenant", "m-alpha", "team-alpha")));
    }

    [Fact]
    public async Task List_HttpRoute_ShouldRejectLegacyOwnerQueryShape()
    {
        await using var host = await ScheduleEndpointTestHost.StartAsync();

        var response = await host.Client.GetAsync(
            "/api/schedules?scopeId=tenant&teamId=team-alpha&memberId=m-alpha");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Schedules.LastListQuery.Should().BeNull();
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

        var result = await CreateAsync(CreateServiceInvocationRequest(scheduleId: "schedule-1"), service);

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

        var act = () => CreateAsync(CreateServiceInvocationRequest(scheduleId: "schedule-1"), service);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch runtime failure");
    }

    [Fact]
    public async Task Update_ShouldUseRouteScheduleIdAsFallback()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await UpdateAsync(
            "route-schedule",
            CreateServiceInvocationRequest(scheduleId: null),
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
    public async Task Create_ShouldRejectServiceInvocationTargetOutsideAuthenticatedScope()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var result = await CreateAsync(
            CreateServiceInvocationRequest("schedule-alpha", "scope-beta"),
            service,
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_ShouldRejectServiceInvocationTargetOutsideAuthenticatedScope()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var result = await UpdateAsync(
            "schedule-alpha",
            CreateServiceInvocationRequest("schedule-alpha", "scope-beta"),
            service,
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ShouldForwardServiceInvocationTargetWithinAuthenticatedScope()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var result = await CreateAsync(
            CreateServiceInvocationRequest("schedule-alpha", "scope-alpha"),
            service,
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true));

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Created.Should().ContainSingle()
            .Which.Target.ServiceInvocation!.Identity.TenantId.Should().Be("scope-alpha");
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
    public async Task Create_WithCallerAuthorityInHttpAuth_ShouldReturnBadRequestWithoutScheduleMutation()
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
                    callerAuthority = new
                    {
                        platform = "nyxid",
                        externalUserId = "user-42",
                        scope = "proxy",
                        bindingId = "bnd-forged",
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

        var result = await CreateAsync(CreateServiceInvocationRequest(scheduleId: "schedule-1"), service);

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
            CreateHttpContext(),
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

        var result = await ScheduledDispatchEndpoints.Enable(CreateHttpContext(), "invalid/id", null, service);

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
            CreateHttpContext(),
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

        var result = await ScheduledDispatchEndpoints.Disable(CreateHttpContext(), "schedule-1", null, service);

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
            CreateHttpContext(),
            "schedule-1",
            "cleanup",
            null,
            acceptedService);
        var notFound = await ScheduledDispatchEndpoints.Delete(
            CreateHttpContext(),
            "missing",
            null,
            new ScheduledDispatchDeleteHttpRequest { Reason = "body" },
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
    public async Task Delete_WithStableLifecycleIdentity_ShouldUseStudioLifecyclePort()
    {
        var genericSchedules =
            new RecordingScheduledDispatchApplicationService
            {
                DeleteException = new InvalidOperationException(
                    "team_automation_delete_requires_revocation_context"),
            };
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort();
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        bindingQuery.Bindings[
            SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";
        var requestHttp = CreateLifecycleDeleteHttpContext(
            lifecycleSchedules,
            bindingQuery);

        var result = await ScheduledDispatchEndpoints.Delete(
            requestHttp,
            "sch-alpha",
            null,
            new ScheduledDispatchDeleteHttpRequest
            {
                Reason = "scheduled_agent_key_canary_cleanup",
                OperationId = "delete-operation-alpha",
                IdempotencyKey = "delete-idempotency-alpha",
                Owner = StudioMemberAutomationOwnerRequest(),
            },
            genericSchedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status202Accepted);
        json.GetProperty("status").GetString().Should().Be("pending");
        json.GetProperty("operationId").GetString()
            .Should().Be("delete-operation-alpha");
        AssertNoCredentialMaterial(json);
        genericSchedules.Deleted.Should().BeEmpty();
        genericSchedules.TeamDeleted.Should().BeEmpty();
        lifecycleSchedules.LastDelete!.Reason.Should().Be(
            "scheduled_agent_key_canary_cleanup");
        lifecycleSchedules.LastDelete.AuthenticatedOwner!
            .Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        lifecycleSchedules.LastDelete.AuthenticatedOwner
            .VerifiedBindingId.Should().Be("binding-alpha");
        lifecycleSchedules.LastDelete.ProvisioningBearerToken.Should().Be(
            "fresh-owner-bearer");
    }

    [Fact]
    public async Task Delete_WithUnsupportedOwnerKind_ShouldReturnSanitizedBadRequest()
    {
        const string secretOwnerKind =
            "raw-owner-secret api-key-alpha vault-ref-alpha";
        var schedules = new RecordingScheduledDispatchApplicationService();
        var result = await ScheduledDispatchEndpoints.Delete(
            CreateHttpContext(
                scopeId: "scope-alpha",
                authenticationEnabled: true),
            "sch-alpha",
            null,
            LifecycleDeleteRequest() with
            {
                Owner = StudioMemberAutomationOwnerRequest() with
                {
                    Kind = secretOwnerKind,
                },
            },
            schedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        json.GetProperty("code").GetString()
            .Should().Be("INVALID_TEAM_AUTOMATION_REQUEST");
        json.GetProperty("message").GetString().Should().Be(
            "Team automation owner is invalid.");
        json.GetRawText().Should().NotContain(secretOwnerKind);
        json.GetRawText().Should().NotContain("Parameter");
        AssertNoCredentialMaterial(json);
        schedules.Deleted.Should().BeEmpty();
        schedules.TeamDeleted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("delete-operation-alpha", null)]
    [InlineData(null, "delete-idempotency-alpha")]
    [InlineData("   ", "delete-idempotency-alpha")]
    [InlineData("delete-operation-alpha", "   ")]
    public async Task Delete_WithPartialLifecycleIdentity_ShouldRejectBeforeDispatch(
        string? operationId,
        string? idempotencyKey)
    {
        var genericSchedules =
            new RecordingScheduledDispatchApplicationService();
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort();
        var requestHttp = CreateLifecycleDeleteHttpContext(
            lifecycleSchedules,
            new FakeExternalIdentityBindingQueryPort());

        var result = await ScheduledDispatchEndpoints.Delete(
            requestHttp,
            "sch-alpha",
            null,
            new ScheduledDispatchDeleteHttpRequest
            {
                Reason = "cleanup",
                OperationId = operationId,
                IdempotencyKey = idempotencyKey,
                Owner = StudioMemberAutomationOwnerRequest(),
            },
            genericSchedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        json.GetProperty("code").GetString()
            .Should().Be("INVALID_TEAM_AUTOMATION_REQUEST");
        AssertNoCredentialMaterial(json);
        genericSchedules.Deleted.Should().BeEmpty();
        genericSchedules.TeamDeleted.Should().BeEmpty();
        lifecycleSchedules.LastDelete.Should().BeNull();
    }

    [Fact]
    public async Task Delete_WithLifecycleIdentityButNoOwner_ShouldRejectBeforeDispatch()
    {
        var schedules = new RecordingScheduledDispatchApplicationService();
        var result = await ScheduledDispatchEndpoints.Delete(
            CreateLifecycleDeleteHttpContext(null, null),
            "sch-alpha",
            null,
            new ScheduledDispatchDeleteHttpRequest
            {
                Reason = "cleanup",
                OperationId = "delete-operation-alpha",
                IdempotencyKey = "delete-idempotency-alpha",
            },
            schedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        json.GetProperty("code").GetString()
            .Should().Be("INVALID_TEAM_AUTOMATION_REQUEST");
        AssertNoCredentialMaterial(json);
        schedules.Deleted.Should().BeEmpty();
        schedules.TeamDeleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_WithLifecycleIdentityAndMissingStudioCapability_ShouldReturnUnavailable()
    {
        var schedules = new RecordingScheduledDispatchApplicationService();
        var result = await ScheduledDispatchEndpoints.Delete(
            CreateLifecycleDeleteHttpContext(null, null),
            "sch-alpha",
            null,
            new ScheduledDispatchDeleteHttpRequest
            {
                Reason = "cleanup",
                OperationId = "delete-operation-alpha",
                IdempotencyKey = "delete-idempotency-alpha",
                Owner = StudioMemberAutomationOwnerRequest(),
            },
            schedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        json.GetProperty("code").GetString()
            .Should().Be("TEAM_AUTOMATION_LIFECYCLE_UNAVAILABLE");
        AssertNoCredentialMaterial(json);
        schedules.Deleted.Should().BeEmpty();
        schedules.TeamDeleted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Delete_WithMissingSubjectOrBinding_ShouldReturnSanitizedUnauthorized(
        bool hasSubject,
        bool hasBinding)
    {
        var genericSchedules =
            new RecordingScheduledDispatchApplicationService();
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort();
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        if (hasBinding)
        {
            bindingQuery.Bindings[
                SubjectKey(OwnerSubject("nyx-owner-alpha"))] =
                "binding-alpha";
        }

        var requestHttp = CreateLifecycleDeleteHttpContext(
            lifecycleSchedules,
            bindingQuery,
            ownerSubject: hasSubject ? "nyx-owner-alpha" : null);
        var result = await ScheduledDispatchEndpoints.Delete(
            requestHttp,
            "sch-alpha",
            null,
            LifecycleDeleteRequest(),
            genericSchedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status401Unauthorized);
        json.GetProperty("code").GetString()
            .Should().Be("TEAM_AUTOMATION_UNAUTHORIZED");
        json.GetProperty("message").GetString().Should().Be(
            "Authenticated Team automation authority is required.");
        AssertNoCredentialMaterial(json);
        genericSchedules.Deleted.Should().BeEmpty();
        genericSchedules.TeamDeleted.Should().BeEmpty();
        lifecycleSchedules.LastDelete.Should().BeNull();
    }

    [Fact]
    public async Task Delete_WithMalformedBearer_ShouldReturnSanitizedUnauthorized()
    {
        var genericSchedules =
            new RecordingScheduledDispatchApplicationService();
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort();
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        bindingQuery.Bindings[
            SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";
        var requestHttp = CreateLifecycleDeleteHttpContext(
            lifecycleSchedules,
            bindingQuery,
            authorizationHeader:
                "Bearer raw-owner-secret, backup-owner-secret");

        var result = await ScheduledDispatchEndpoints.Delete(
            requestHttp,
            "sch-alpha",
            null,
            LifecycleDeleteRequest(),
            genericSchedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status401Unauthorized);
        json.GetProperty("code").GetString()
            .Should().Be("TEAM_AUTOMATION_UNAUTHORIZED");
        AssertNoCredentialMaterial(json);
        json.GetRawText().Should().NotContain("raw-owner-secret");
        json.GetRawText().Should().NotContain("backup-owner-secret");
        genericSchedules.Deleted.Should().BeEmpty();
        genericSchedules.TeamDeleted.Should().BeEmpty();
        lifecycleSchedules.LastDelete.Should().BeNull();
    }

    [Fact]
    public async Task Delete_WithExactOwnerScheduleMissing_ShouldReturnSanitizedNotFound()
    {
        var schedules = new RecordingScheduledDispatchApplicationService
        {
            DeleteException = new ScheduledDispatchNotFoundException(
                "hidden-schedule backend-delete-secret api-key-alpha vault-ref-alpha"),
        };

        var result = await ScheduledDispatchEndpoints.Delete(
            CreateHttpContext(
                scopeId: "scope-alpha",
                authenticationEnabled: true),
            "sch-alpha",
            null,
            new ScheduledDispatchDeleteHttpRequest
            {
                Reason = "cleanup",
                Owner = StudioMemberAutomationOwnerRequest(),
            },
            schedules);

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status404NotFound);
        json.GetProperty("code").GetString()
            .Should().Be("TEAM_AUTOMATION_NOT_FOUND");
        json.GetProperty("message").GetString().Should().Be(
            "Team automation resource was not found.");
        AssertNoCredentialMaterial(json);
        schedules.TeamDeleted.Should().ContainSingle();
        schedules.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_WithReasonOperationConflict_ShouldReturnSanitizedConflict()
    {
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort
            {
                DeleteException = new ScheduledDispatchConflictException(
                    "sch-alpha",
                    "backend-delete-secret binding-alpha nyx-owner-alpha " +
                    "api-key-alpha vault-ref-alpha"),
            };
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        bindingQuery.Bindings[
            SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";

        var result = await ScheduledDispatchEndpoints.Delete(
            CreateLifecycleDeleteHttpContext(
                lifecycleSchedules,
                bindingQuery),
            "sch-alpha",
            null,
            LifecycleDeleteRequest(),
            new RecordingScheduledDispatchApplicationService());

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status409Conflict);
        json.GetProperty("code").GetString()
            .Should().Be("TEAM_AUTOMATION_CONFLICT");
        json.GetProperty("message").GetString().Should().Be(
            "The Team automation delete conflicts with its active operation.");
        AssertNoCredentialMaterial(json);
    }

    [Theory]
    [InlineData("team_member_is_not_workflow")]
    [InlineData("team_automation_delete_requires_revocation_context")]
    [InlineData("team_automation_owner_required")]
    public async Task Delete_WithKnownInvalidLifecycleRequest_ShouldReturnSanitizedBadRequest(
        string stableCode)
    {
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort
            {
                DeleteException = new InvalidOperationException(
                    stableCode),
            };
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        bindingQuery.Bindings[
            SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";

        var result = await ScheduledDispatchEndpoints.Delete(
            CreateLifecycleDeleteHttpContext(
                lifecycleSchedules,
                bindingQuery),
            "sch-alpha",
            null,
            LifecycleDeleteRequest(),
            new RecordingScheduledDispatchApplicationService());

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        json.GetProperty("code").GetString()
            .Should().Be("INVALID_TEAM_AUTOMATION_REQUEST");
        json.GetProperty("message").GetString().Should().Be(
            "Team automation delete request is invalid.");
        AssertNoCredentialMaterial(json);
    }

    [Theory]
    [InlineData("team_automation_commit_observation_unavailable")]
    [InlineData("team_automation_dispatch_rejected")]
    [InlineData("team_automation_commit_observation_ended")]
    public async Task Delete_WithLifecycleAvailabilityFailure_ShouldReturnSanitizedUnavailable(
        string stableCode)
    {
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort
            {
                DeleteException = new InvalidOperationException(
                    stableCode),
            };
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        bindingQuery.Bindings[
            SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";

        var result = await ScheduledDispatchEndpoints.Delete(
            CreateLifecycleDeleteHttpContext(
                lifecycleSchedules,
                bindingQuery),
            "sch-alpha",
            null,
            LifecycleDeleteRequest(),
            new RecordingScheduledDispatchApplicationService());

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        json.GetProperty("code").GetString()
            .Should().Be("TEAM_AUTOMATION_LIFECYCLE_UNAVAILABLE");
        json.GetProperty("message").GetString().Should().Be(
            "Team automation lifecycle capability is unavailable.");
        json.GetRawText().Should().NotContain(stableCode);
        AssertNoCredentialMaterial(json);
    }

    [Theory]
    [InlineData("team_automation_observation_status_invalid")]
    [InlineData("team_automation_revocation_completion_not_committed")]
    [InlineData(
        "team_automation_backend_delete_secret api-key-alpha vault-ref-alpha")]
    public async Task Delete_WithLifecycleInvariantOrUnknownFailure_ShouldReturnSanitizedInternalError(
        string backendMessage)
    {
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort
            {
                DeleteException = new InvalidOperationException(
                    backendMessage),
            };
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        bindingQuery.Bindings[
            SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";

        var result = await ScheduledDispatchEndpoints.Delete(
            CreateLifecycleDeleteHttpContext(
                lifecycleSchedules,
                bindingQuery),
            "sch-alpha",
            null,
            LifecycleDeleteRequest(),
            new RecordingScheduledDispatchApplicationService());

        var (statusCode, json) = await ExecuteJsonResultAsync(result);

        statusCode.Should().Be(
            StatusCodes.Status500InternalServerError);
        json.GetProperty("code").GetString()
            .Should().Be("TEAM_AUTOMATION_DELETE_FAILED");
        json.GetProperty("message").GetString().Should().Be(
            "Team automation delete could not be completed.");
        json.GetRawText().Should().NotContain(backendMessage);
        AssertNoCredentialMaterial(json);
    }

    [Fact]
    public async Task Delete_WithLifecycleIdentityAndScopeMismatch_ShouldRejectBeforeDispatch()
    {
        var genericSchedules =
            new RecordingScheduledDispatchApplicationService();
        var lifecycleSchedules =
            new RecordingStudioMemberWorkflowSchedulePort();
        var bindingQuery = new FakeExternalIdentityBindingQueryPort();
        bindingQuery.Bindings[
            SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";

        var result = await ScheduledDispatchEndpoints.Delete(
            CreateLifecycleDeleteHttpContext(
                lifecycleSchedules,
                bindingQuery,
                claimedScopeId: "scope-beta"),
            "sch-alpha",
            null,
            LifecycleDeleteRequest(),
            genericSchedules);

        var responseHttp = CreateHttpContext();
        await result.ExecuteAsync(responseHttp);

        responseHttp.Response.StatusCode.Should().Be(
            StatusCodes.Status403Forbidden);
        genericSchedules.Deleted.Should().BeEmpty();
        genericSchedules.TeamDeleted.Should().BeEmpty();
        lifecycleSchedules.LastDelete.Should().BeNull();
    }

    [Theory]
    [InlineData("authenticatedOwner")]
    [InlineData("authority")]
    [InlineData("provisioningBearerToken")]
    [InlineData("bearerToken")]
    [InlineData("verifiedBindingId")]
    [InlineData("credentialId")]
    [InlineData("apiKeyId")]
    [InlineData("vaultReference")]
    public void DeleteRequest_ShouldRejectAuthorityAndCredentialFields(
        string propertyName)
    {
        var json = $$"""
            {
              "reason": "cleanup",
              "{{propertyName}}": "forged"
            }
            """;

        var act = () => JsonSerializer.Deserialize<ScheduledDispatchDeleteHttpRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task OwnerActions_ShouldUseTypedOwnerSpecificApplicationPath()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var owner = StudioMemberAutomationOwnerRequest();
        var expectedOwner = new TeamMemberAutomationOwner("scope-alpha", "m-alpha", "team-alpha");

        var enable = await ScheduledDispatchEndpoints.Enable(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            "sch-alpha",
            new ScheduledDispatchStateChangeHttpRequest { Reason = "resume", Owner = owner },
            service);
        var disable = await ScheduledDispatchEndpoints.Disable(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            "sch-alpha",
            new ScheduledDispatchStateChangeHttpRequest { Reason = "pause", Owner = owner },
            service);
        var delete = await ScheduledDispatchEndpoints.Delete(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            "sch-alpha",
            null,
            new ScheduledDispatchDeleteHttpRequest { Reason = "cleanup", Owner = owner },
            service);
        var runNow = await ScheduledDispatchEndpoints.RunNow(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            "sch-alpha",
            new ScheduledDispatchRunNowHttpRequest { Owner = owner },
            service);

        var enableHttp = CreateHttpContext();
        await enable.ExecuteAsync(enableHttp);
        var disableHttp = CreateHttpContext();
        await disable.ExecuteAsync(disableHttp);
        var deleteHttp = CreateHttpContext();
        await delete.ExecuteAsync(deleteHttp);
        var runNowHttp = CreateHttpContext();
        await runNow.ExecuteAsync(runNowHttp);

        enableHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        disableHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        deleteHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        runNowHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        runNowHttp.Response.Headers.Location.ToString().Should().Be(
            "/api/schedules/sch-alpha?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha");
        service.TeamEnabled.Should().ContainSingle().Which.Should().Be(("sch-alpha", expectedOwner, "resume"));
        service.TeamDisabled.Should().ContainSingle().Which.Should().Be(("sch-alpha", expectedOwner, "pause"));
        service.TeamDeleted.Should().ContainSingle().Which.Should().Be(("sch-alpha", expectedOwner, "cleanup"));
        service.TeamRunNow.Should().ContainSingle().Which.Should().Be(("sch-alpha", expectedOwner));
        service.Enabled.Should().BeEmpty();
        service.Disabled.Should().BeEmpty();
        service.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerAction_ShouldRejectTypedOwnerWhenAuthenticatedScopeDiffers()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.RunNow(
            CreateHttpContext(scopeId: "scope-beta", authenticationEnabled: true),
            "sch-alpha",
            new ScheduledDispatchRunNowHttpRequest { Owner = StudioMemberAutomationOwnerRequest() },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.TeamRunNow.Should().BeEmpty();
    }

    [Fact]
    public async Task RunNow_WithIncompleteBodyOwnerTuple_ShouldRejectBeforeDispatch()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.RunNow(
            CreateHttpContext(),
            "sch-alpha",
            new ScheduledDispatchRunNowHttpRequest
            {
                Owner = StudioMemberAutomationOwnerRequest() with
                {
                    MemberId = null!,
                },
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.TeamRunNow.Should().BeEmpty();
    }

    [Fact]
    public async Task Enable_WithIncompleteBodyOwnerTuple_ShouldRejectBeforeDispatch()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.Enable(
            CreateHttpContext(),
            "sch-alpha",
            new ScheduledDispatchStateChangeHttpRequest
            {
                Reason = "resume",
                Owner = StudioMemberAutomationOwnerRequest() with
                {
                    MemberId = null!,
                },
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.TeamEnabled.Should().BeEmpty();
        service.Enabled.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ShouldForwardTypedOwnerAndPageQueryParameters()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.List(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            service,
            ownerKind: ScheduledDispatchOwnerKinds.StudioMemberAutomation,
            ownerScopeId: "scope-alpha",
            ownerTeamId: "team-alpha",
            ownerMemberId: "m-alpha",
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
            TeamAutomationOwner: new TeamMemberAutomationOwner("scope-alpha", "m-alpha", "team-alpha")));
    }

    [Fact]
    public async Task List_WhenOwnerMemberIdMissing_ShouldForwardTeamWideOwnerQuery()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.List(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            service,
            ownerKind: ScheduledDispatchOwnerKinds.StudioMemberAutomation,
            ownerScopeId: " scope-alpha ",
            ownerTeamId: " team-alpha ",
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
            TeamAutomationMemberId: null,
            ExcludeCompletedTeamAutomationDeletions: true));
    }

    [Theory]
    [InlineData(null, "scope-alpha", "team-alpha")]
    [InlineData(ScheduledDispatchOwnerKinds.StudioMemberAutomation, null, "team-alpha")]
    [InlineData(ScheduledDispatchOwnerKinds.StudioMemberAutomation, "scope-alpha", null)]
    [InlineData("unsupported_owner", "scope-alpha", "team-alpha")]
    public async Task List_WhenOwnerQueryIsPartialOrUnsupported_ShouldReject(
        string? ownerKind,
        string? ownerScopeId,
        string? ownerTeamId)
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.List(
            CreateHttpContext(),
            service,
            ownerKind: ownerKind,
            ownerScopeId: ownerScopeId,
            ownerTeamId: ownerTeamId);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.LastListQuery.Should().BeNull();
    }

    [Fact]
    public async Task List_WhenScopeIdMissing_ShouldUseGenericListPath()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.List(
            CreateHttpContext(),
            service,
            scopeId: null,
            take: 25,
            cursor: "cursor-1",
            includeTotalCount: true);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastListQuery.Should().Be(new ScheduledDispatchListQuery(
            Take: 25,
            Cursor: "cursor-1",
            IncludeTotalCount: true));
    }

    [Fact]
    public async Task ListAndGet_ShouldRejectLegacyOwnerQueryParameters()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var list = await ScheduledDispatchEndpoints.List(
            CreateHttpContext(),
            service,
            scopeId: "scope-alpha",
            teamId: "team-alpha",
            memberId: "m-alpha");
        var get = await ScheduledDispatchEndpoints.Get(
            CreateHttpContext(),
            "sch-alpha",
            service,
            scopeId: "scope-alpha",
            teamId: "team-alpha",
            memberId: "m-alpha");

        var listHttp = CreateHttpContext();
        await list.ExecuteAsync(listHttp);
        var getHttp = CreateHttpContext();
        await get.ExecuteAsync(getHttp);

        listHttp.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        getHttp.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.LastListQuery.Should().BeNull();
        service.LastScheduleGet.Should().BeNull();
        service.LastTeamAutomationGet.Should().BeNull();
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
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            "schedule-1",
            service,
            ownerKind: ScheduledDispatchOwnerKinds.StudioMemberAutomation,
            ownerScopeId: "scope-alpha",
            ownerTeamId: "team-alpha",
            ownerMemberId: "m-alpha");
        var notFound = await ScheduledDispatchEndpoints.Get(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            "missing",
            notFoundService,
            ownerKind: ScheduledDispatchOwnerKinds.StudioMemberAutomation,
            ownerScopeId: "scope-alpha",
            ownerTeamId: "team-alpha",
            ownerMemberId: "m-alpha");

        var okHttp = CreateHttpContext();
        await ok.ExecuteAsync(okHttp);
        var notFoundHttp = CreateHttpContext();
        await notFound.ExecuteAsync(notFoundHttp);

        okHttp.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        notFoundHttp.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        service.LastTeamAutomationGet.Should().Be((
            "schedule-1",
            new TeamMemberAutomationOwner("scope-alpha", "m-alpha", "team-alpha")));
        notFoundService.LastTeamAutomationGet.Should().Be((
            "missing",
            new TeamMemberAutomationOwner("scope-alpha", "m-alpha", "team-alpha")));
    }

    [Fact]
    public async Task Get_WhenOwnerMemberIdMissing_ShouldRejectTypedOwnerQuery()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.Get(
            CreateHttpContext(),
            "schedule-1",
            service,
            ownerKind: ScheduledDispatchOwnerKinds.StudioMemberAutomation,
            ownerScopeId: "scope-alpha",
            ownerTeamId: "team-alpha");

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.LastScheduleGet.Should().BeNull();
        service.LastTeamAutomationGet.Should().BeNull();
    }

    [Fact]
    public async Task Get_WhenScopeIdMissing_ShouldUseGenericGetPath()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            Detail = CreateDetail("schedule-1"),
        };

        var result = await ScheduledDispatchEndpoints.Get(CreateHttpContext(), "schedule-1", service, scopeId: null);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastScheduleGet.Should().Be("schedule-1");
        service.LastTeamScheduleGet.Should().BeNull();
    }

    [Fact]
    public async Task Get_ShouldSerializeOwnerLLMRuntimeEvidenceWithoutSensitiveAuthorityMaterial()
    {
        var detail = CreateDetail("schedule-owner-llm");
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMRouteKind", "nyx_id_user_service");
        SetRequiredStringProperty(
            detail.Schedule,
            "OwnerLLMRoute",
            "/api/v1/proxy/s/chrono-llm-public");
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMUserServiceId", "us-chrono");
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMServiceSlug", "chrono-llm-public");
        SetRequiredStringProperty(detail.Schedule, "OwnerLLMModel", "gpt-5.5");
        SetRequiredStringProperty(detail.Schedule, "NyxIdRevocationStatus", "nyx-track-terminal");
        SetRequiredStringProperty(detail.Schedule, "VaultRevocationStatus", "vault-track-terminal");
        var service = new RecordingScheduledDispatchApplicationService
        {
            Detail = detail,
        };

        var result = await ScheduledDispatchEndpoints.Get(CreateHttpContext(), "schedule-owner-llm", service);
        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(http.Response.Body);

        var payload = document.RootElement.GetProperty("schedule");
        payload.GetProperty("ownerLLMRouteKind").GetString().Should().Be("nyx_id_user_service");
        payload.GetProperty("ownerLLMRoute").GetString().Should()
            .Be("/api/v1/proxy/s/chrono-llm-public");
        payload.GetProperty("ownerLLMUserServiceId").GetString().Should().Be("us-chrono");
        payload.GetProperty("ownerLLMServiceSlug").GetString().Should().Be("chrono-llm-public");
        payload.GetProperty("ownerLLMModel").GetString().Should().Be("gpt-5.5");
        payload.GetProperty("nyxIdRevocationStatus").GetString().Should().Be("nyx-track-terminal");
        payload.GetProperty("vaultRevocationStatus").GetString().Should().Be("vault-track-terminal");
        var json = document.RootElement.GetRawText();
        json.Should().NotContain("callerAuthority")
            .And.NotContain("bindingId")
            .And.NotContain("bearerToken")
            .And.NotContain("refreshToken")
            .And.NotContain("secretReference")
            .And.NotContain("vaultRef")
            .And.NotContain("full_key")
            .And.NotContain("ciphertext");
    }

    [Fact]
    public async Task Get_ShouldMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            GetException = new ArgumentException("invalid id"),
        };

        var result = await ScheduledDispatchEndpoints.Get(
            CreateHttpContext(scopeId: "scope-alpha", authenticationEnabled: true),
            "invalid/id",
            service,
            ownerKind: ScheduledDispatchOwnerKinds.StudioMemberAutomation,
            ownerScopeId: "scope-alpha",
            ownerTeamId: "team-alpha",
            ownerMemberId: "m-alpha");

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
            CreateHttpContext(),
            "schedule-1",
            null,
            new RecordingScheduledDispatchApplicationService());
        var notFound = await ScheduledDispatchEndpoints.RunNow(
            CreateHttpContext(),
            "missing",
            null,
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

        var result = await ScheduledDispatchEndpoints.RunNow(CreateHttpContext(), "schedule-1", null, service);

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
    public async Task Create_WithNoTargetOrRawEnvelopeTarget_ShouldReturnBadRequest()
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

    private static ScheduledDispatchConfigurationHttpRequest CreateServiceInvocationRequest(
        string? scheduleId,
        string scopeId = "tenant") =>
        new()
        {
            ScheduleId = scheduleId,
            DisplayName = "Daily",
            CronExpression = "0 9 * * *",
            Timezone = "UTC",
            Enabled = true,
            Headers = new Dictionary<string, string> { ["trace"] = "1" },
            ServiceInvocation = new ScheduledDispatchServiceInvocationTargetHttpRequest
            {
                Identity = new ServiceIdentity
                {
                    TenantId = scopeId,
                    AppId = "app",
                    Namespace = "default",
                    ServiceId = "svc",
                },
                EndpointId = "run",
                PayloadTypeUrl = Any.Pack(new StringValue()).TypeUrl,
                PayloadBase64 = Convert.ToBase64String(new StringValue { Value = "run" }.ToByteArray()),
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

    private static ScheduledDispatchOwnerHttpRequest StudioMemberAutomationOwnerRequest() =>
        new()
        {
            Kind = ScheduledDispatchOwnerKinds.StudioMemberAutomation,
            ScopeId = "scope-alpha",
            TeamId = "team-alpha",
            MemberId = "m-alpha",
        };

    private static ScheduledDispatchDeleteHttpRequest LifecycleDeleteRequest() =>
        new()
        {
            Reason = "cleanup",
            OperationId = "delete-operation-alpha",
            IdempotencyKey = "delete-idempotency-alpha",
            Owner = StudioMemberAutomationOwnerRequest(),
        };

    private static DefaultHttpContext CreateLifecycleDeleteHttpContext(
        IStudioMemberWorkflowSchedulePort? lifecycleSchedules,
        IExternalIdentityBindingQueryPort? bindingQuery,
        string? ownerSubject = "nyx-owner-alpha",
        string claimedScopeId = "scope-alpha",
        string authorizationHeader = "Bearer fresh-owner-bearer")
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            })
            .Build());
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        if (lifecycleSchedules != null)
            services.AddSingleton(lifecycleSchedules);
        if (bindingQuery != null)
            services.AddSingleton(bindingQuery);

        var claims = new List<Claim>
        {
            new("scope_id", claimedScopeId),
        };
        if (!string.IsNullOrWhiteSpace(ownerSubject))
        {
            claims.Add(new Claim(
                ClaimTypes.NameIdentifier,
                ownerSubject));
        }

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                "test")),
        };
        http.Request.Headers.Authorization = authorizationHeader;
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static async Task<(int StatusCode, JsonElement Body)>
        ExecuteJsonResultAsync(IResult result)
    {
        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(http.Response.Body);
        return (http.Response.StatusCode, json.RootElement.Clone());
    }

    private static void AssertNoCredentialMaterial(JsonElement response)
    {
        var normalized = response.GetRawText().ToLowerInvariant();
        normalized.Should().NotContain("fresh-owner-bearer");
        normalized.Should().NotContain("raw-owner-secret");
        normalized.Should().NotContain("backup-owner-secret");
        normalized.Should().NotContain("binding-alpha");
        normalized.Should().NotContain("nyx-owner-alpha");
        normalized.Should().NotContain("api-key-alpha");
        normalized.Should().NotContain("vault-ref-alpha");
        normalized.Should().NotContain("backend-delete-secret");
        normalized.Should().NotContain("provisioningbearertoken");
        normalized.Should().NotContain("verifiedbindingid");
        normalized.Should().NotContain("credentialid");
        normalized.Should().NotContain("vaultreference");
    }

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

    private static void SetRequiredStringProperty(object target, string propertyName, string value)
    {
        var property = target.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"{propertyName} is part of the runtime evidence contract");
        property!.SetValue(target, value);
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
        string? userId = null,
        bool authenticationEnabled = false)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = authenticationEnabled ? "true" : "false",
            })
            .Build());
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = nameof(ScheduledDispatchEndpointsTests);

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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
        public List<(string ScheduleId, TeamMemberAutomationOwner Owner, string Reason)> TeamEnabled { get; } = [];
        public List<(string ScheduleId, TeamMemberAutomationOwner Owner, string Reason)> TeamDisabled { get; } = [];
        public List<(string ScheduleId, TeamMemberAutomationOwner Owner, string Reason)> TeamDeleted { get; } = [];
        public List<(string ScheduleId, TeamMemberAutomationOwner Owner)> TeamRunNow { get; } = [];
        public int? LastListTake { get; private set; }
        public string? LastListCursor { get; private set; }
        public bool? LastListIncludeTotalCount { get; private set; }
        public ScheduledDispatchListQuery? LastListQuery { get; private set; }
        public string? LastScheduleGet { get; private set; }
        public (string ScheduleId, string ScopeId, string? TeamId, string? MemberId)? LastTeamScheduleGet { get; private set; }
        public (string ScheduleId, TeamMemberAutomationOwner Owner)? LastTeamAutomationGet { get; private set; }
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

        public Task<ScheduledDispatchMutationReceipt> EnableTeamAutomationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string reason,
            CancellationToken ct = default)
        {
            TeamEnabled.Add((scheduleId, owner, reason));
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

        public Task<ScheduledDispatchMutationReceipt> DisableTeamAutomationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string reason,
            CancellationToken ct = default)
        {
            TeamDisabled.Add((scheduleId, owner, reason));
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

        public Task<ScheduledDispatchMutationReceipt> DeleteTeamAutomationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            string reason,
            CancellationToken ct = default)
        {
            TeamDeleted.Add((scheduleId, owner, reason));
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
            LastScheduleGet = scheduleId;
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

        public Task<ScheduledDispatchDetail?> GetTeamAutomationAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            CancellationToken ct = default)
        {
            LastTeamAutomationGet = (scheduleId, owner);
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

        public Task<ScheduledDispatchRunNowReceipt> RunTeamAutomationNowAsync(
            string scheduleId,
            TeamMemberAutomationOwner owner,
            CancellationToken ct = default)
        {
            TeamRunNow.Add((scheduleId, owner));
            if (RunNowException != null)
                throw RunNowException;

            return Task.FromResult(new ScheduledDispatchRunNowReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                DateTimeOffset.UtcNow,
                "backend-owned-run-now",
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

    private sealed class RecordingStudioMemberWorkflowSchedulePort :
        IStudioMemberWorkflowSchedulePort
    {
        public StudioMemberAutomationActionCommand? LastDelete { get; private set; }

        public Exception? DeleteException { get; init; }

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightForWriteAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberWorkflowScheduleResult> CreateAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
            StudioMemberAutomationUpdateCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> PauseAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default)
        {
            LastDelete = command;
            if (DeleteException != null)
                throw DeleteException;

            return Task.FromResult(
                new StudioMemberAutomationMutationReceipt(
                    true,
                    "pending",
                    command.ScheduleId,
                    command.OperationId,
                    "cmd-delete-alpha"));
        }

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string? memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
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
