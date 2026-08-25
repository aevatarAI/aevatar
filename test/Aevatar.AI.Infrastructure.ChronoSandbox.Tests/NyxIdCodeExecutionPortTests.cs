using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class NyxIdCodeExecutionPortTests
{
    private const string ExecutionBearerToken = "execution-bearer";
    private const string SourceReadableBearerToken = "source-readable-bearer";
    private const string CodeServiceId = "us-code-alpha";

    [Fact]
    public async Task ExecuteAsync_UsesSourceReadableCredentialForInventoryAndExecutionCredentialForProxy()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(
                Service("us-code-no-credential", "chrono-sandbox", false, false, "proxy:*"),
                Service("us-code-unscoped-delegation", "chrono-sandbox", false, true, "proxy:*"),
                Service("us-code-llm-delegation", "chrono-sandbox", false, true, "llm:proxy"),
                Service(CodeServiceId, "chrono-sandbox", true, true, "proxy:* sandbox:execute"))),
            JsonResponse(
                """
                {
                  "success": true,
                  "output": {
                    "stdout": "42\n",
                    "stderr": "",
                    "exit_code": 0,
                    "execution_time_ms": 17
                  },
                  "diagnostic_id": "diag-code-1"
                }
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(userServiceId: null, timeoutSeconds: 300));

        outcome.Failure.Should().BeNull();
        outcome.Result.Should().Be(new CodeExecutionResult("42\n", "", 0, "diag-code-1", 17));
        outcome.ResolvedRoute.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            CodeServiceId,
            CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog));
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get.Method);
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
        handler.Requests[1].Method.Should().Be(HttpMethod.Get.Method);
        handler.Requests[1].PathAndQuery.Should().Be("/api/v1/keys");
        handler.Requests[2].Method.Should().Be(HttpMethod.Post.Method);
        handler.Requests[2].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
        handler.Requests[0].Authorization.Should().Be($"Bearer {SourceReadableBearerToken}");
        handler.Requests[1].Authorization.Should().Be($"Bearer {SourceReadableBearerToken}");
        handler.Requests[2].Authorization.Should().Be($"Bearer {ExecutionBearerToken}");
        handler.Requests.Should().OnlyContain(request => request.ApiKeys.Count == 0);
        using var body = JsonDocument.Parse(handler.Requests[2].Body!);
        body.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("language", "script", "timeout_secs");
        body.RootElement.GetProperty("language").GetString().Should().Be("python");
        body.RootElement.GetProperty("script").GetString().Should().Be("print(42)");
        body.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(300);
    }

    [Fact]
    public async Task ExecuteAsync_ScheduledExactAdmissionSkipsInventoryAndUsesExecutionCredential()
    {
        var handler = new SequenceHandler(JsonResponse(
            """
            {
              "success": true,
              "output": {
                "stdout": "scheduled-ok",
                "stderr": "",
                "exit_code": 0
              }
            }
            """));
        var port = CreatePort(handler);
        var request = Request(
            CodeServiceId,
            executionCredential: "scheduled-agent-key",
            sourceReadableBearerToken: null,
            executionCredentialKind: CodeExecutionNyxIdCredentialKind.AgentKey) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                CodeServiceId,
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Failure.Should().BeNull();
        outcome.Result!.Stdout.Should().Be("scheduled-ok");
        outcome.ResolvedRoute.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            CodeServiceId,
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post.Method);
        handler.Requests[0].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
        handler.Requests[0].Authorization.Should().Be("Bearer scheduled-agent-key");
        handler.Requests[0].ApiKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ScheduledPersonalRouteUsesItsAdmittedSlug()
    {
        var handler = new SequenceHandler(JsonResponse(
            """
            {
              "success": true,
              "output": {
                "stdout": "scheduled-ok",
                "stderr": "",
                "exit_code": 0
              }
            }
            """));
        var port = CreatePort(handler);
        var request = Request(
            "us-code-aevatar",
            executionCredential: "scheduled-agent-key",
            sourceReadableBearerToken: null,
            executionCredentialKind: CodeExecutionNyxIdCredentialKind.AgentKey) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-sandbox-aevatar",
                "us-code-aevatar",
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Failure.Should().BeNull();
        outcome.ResolvedRoute.Should().Be(request.Route);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox-aevatar/execute?_nyxid_via=us-code-aevatar");
        handler.Requests[0].Authorization.Should().Be("Bearer scheduled-agent-key");
        handler.Requests[0].ApiKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenUserServicesSourceCredentialExpiresAfterExactAdmission_UsesAdmittedRoute()
    {
        var handler = new SequenceHandler(
            JsonResponse("{\"error\":\"expired\"}", HttpStatusCode.Unauthorized),
            JsonResponse(
                """
                {
                  "success": true,
                  "output": {
                    "stdout": "admitted-ok",
                    "stderr": "",
                    "exit_code": 0
                  }
                }
                """));
        var port = CreatePort(handler);
        var request = Request(CodeServiceId) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                CodeServiceId,
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Failure.Should().BeNull();
        outcome.Result!.Stdout.Should().Be("admitted-ok");
        outcome.ResolvedRoute.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            CodeServiceId,
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission));
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
        handler.Requests[0].Authorization.Should().Be($"Bearer {SourceReadableBearerToken}");
        handler.Requests[1].PathAndQuery.Should().Be("/api/v1/keys");
        handler.Requests[1].Authorization.Should().Be($"Bearer {SourceReadableBearerToken}");
        handler.Requests[2].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
        handler.Requests[2].Authorization.Should().Be($"Bearer {ExecutionBearerToken}");
    }

    [Fact]
    public async Task ExecuteAsync_WhenKeysSourceCredentialExpiresAfterExactAdmission_UsesAdmittedRoute()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                false,
                true,
                "sandbox:execute"))),
            JsonResponse(
                """
                {
                  "success": true,
                  "output": {
                    "stdout": "admitted-ok",
                    "stderr": "",
                    "exit_code": 0
                  }
                }
                """))
        {
            KeysResponse = JsonResponse(
                "{\"error\":\"expired\"}",
                HttpStatusCode.Unauthorized),
        };
        var port = CreatePort(handler);
        var request = Request(CodeServiceId) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                CodeServiceId,
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Failure.Should().BeNull();
        outcome.Result!.Stdout.Should().Be("admitted-ok");
        outcome.ResolvedRoute.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            CodeServiceId,
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission));
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
        handler.Requests[1].PathAndQuery.Should().Be("/api/v1/keys");
        handler.Requests[2].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
        handler.Requests[2].Authorization.Should().Be($"Bearer {ExecutionBearerToken}");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSourceCredentialIsUnauthorizedWithoutExactAdmission_DoesNotFallback()
    {
        var handler = new SequenceHandler(JsonResponse(
            "{\"error\":\"expired\"}",
            HttpStatusCode.Unauthorized));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.ResolvedRoute.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_route_access_denied",
        });
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().NotContain(recorded =>
            recorded.PathAndQuery.Contains("/api/v1/proxy/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WhenSourceCredentialIsForbiddenAfterExactAdmission_DoesNotFallback()
    {
        var handler = new SequenceHandler(JsonResponse(
            "{\"error\":\"forbidden\"}",
            HttpStatusCode.Forbidden));
        var port = CreatePort(handler);
        var request = Request(CodeServiceId) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                CodeServiceId,
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Result.Should().BeNull();
        outcome.ResolvedRoute.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_route_access_denied",
        });
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().NotContain(recorded =>
            recorded.PathAndQuery.Contains("/api/v1/proxy/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLiveInventoryDeniesExactAdmission_DoesNotFallBackToProxy()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(Service(
            CodeServiceId,
            "chrono-sandbox",
            false,
            true,
            "sandbox:execute",
            credentialSourceType: "org",
            allowed: false))));
        var port = CreatePort(handler);
        var request = Request(CodeServiceId) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                CodeServiceId,
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_route_access_denied",
        });
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().NotContain(request =>
            request.PathAndQuery.Contains("/api/v1/proxy/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WithoutSourceReadableCredentialOrExactAdmissionFailsBeforeNyxIdCall()
    {
        var handler = new SequenceHandler();
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(
            userServiceId: null,
            executionCredential: "scheduled-agent-key",
            sourceReadableBearerToken: null));

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_credential_unavailable",
        });
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenRouteSlugIsNotCanonical_FailsBeforeNyxIdCall()
    {
        var handler = new SequenceHandler();
        var port = CreatePort(handler);
        var request = Request(CodeServiceId) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-managed-codex",
                CodeServiceId,
                CodeExecutionRouteIdentitySource.CodeExecutionContract),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_request_invalid",
        });
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CodeExecutionLanguage.Unspecified)]
    [InlineData((CodeExecutionLanguage)999)]
    public async Task ExecuteAsync_WhenLanguageIsOutsideContract_FailsBeforeNyxIdCall(
        CodeExecutionLanguage language)
    {
        var handler = new SequenceHandler();
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId) with { Language = language });

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_request_invalid",
        });
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task ExecuteAsync_WhenTimeoutIsOutsideContract_FailsBeforeNyxIdCall(int timeoutSeconds)
    {
        var handler = new SequenceHandler();
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId) with
        {
            TimeoutSeconds = timeoutSeconds,
        });

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_request_invalid",
        });
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true, "proxy:* sandbox:execute")]
    [InlineData(true, false, "proxy:* sandbox:execute")]
    [InlineData(true, true, "proxy:*")]
    [InlineData(true, true, "sandbox:execute")]
    [InlineData(false, false, "proxy:* sandbox:execute")]
    public async Task ExecuteAsync_WhenSharedServiceDeliversNoAcceptedCredential_FailsClosedBeforeProxy(
        bool forwardAccessToken,
        bool injectDelegationToken,
        string delegationTokenScope)
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(
            Service(
                CodeServiceId,
                "chrono-sandbox",
                forwardAccessToken,
                injectDelegationToken,
                delegationTokenScope))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.ResolvedRoute.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.TargetNotConfigured,
            Code = "code_execution_route_policy_mismatch",
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
        handler.Requests[1].PathAndQuery.Should().Be("/api/v1/keys");
    }

    [Theory]
    [InlineData(true, true, "proxy:* sandbox:execute")]
    [InlineData(true, true, "sandbox:execute proxy:*")]
    [InlineData(true, true, "llm:proxy proxy:* sandbox:execute account:read")]
    public async Task ExecuteAsync_WhenSharedServiceDeliversAcceptedCredential_UsesExactRoute(
        bool forwardAccessToken,
        bool injectDelegationToken,
        string delegationTokenScope)
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                forwardAccessToken,
                injectDelegationToken,
                delegationTokenScope))),
            JsonResponse(
                """
                {
                  "success": true,
                  "output": {
                    "stdout": "ok",
                    "stderr": "",
                    "exit_code": 0
                  }
                }
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().BeNull();
        outcome.ResolvedRoute!.UserServiceId.Should().Be(CodeServiceId);
        handler.Requests.Should().HaveCount(3);
        handler.Requests[1].PathAndQuery.Should().Be("/api/v1/keys");
        handler.Requests[2].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceIsInactive_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(Service(
            CodeServiceId,
            "chrono-sandbox",
            true,
            true,
            "proxy:* sandbox:execute",
            isActive: false))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_inactive");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRouteIsConfiguredButExecutionAuthorityIsExpired_FailsBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(Service(
            CodeServiceId,
            "chrono-sandbox",
            true,
            true,
            "proxy:* sandbox:execute"))))
        {
            KeysResponse = JsonResponse(
                """
                {
                  "keys": [{
                    "id": "us-code-alpha",
                    "slug": "chrono-sandbox",
                    "catalog_service_id": "catalog-chrono-sandbox",
                    "catalog_service_slug": "chrono-sandbox",
                    "is_active": true,
                    "status": "expired",
                    "connected": true,
                    "credential_source": { "type": "personal" }
                  }]
                }
                """),
        };
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_not_ready");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanonicalOrganizationMemberServiceIsAllowed_UsesExactRoute()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                true,
                true,
                "proxy:* sandbox:execute",
                credentialSourceType: "org"))),
            JsonResponse(
                """
                {"success":true,"output":{"stdout":"ok","stderr":"","exit_code":0}}
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().BeNull();
        outcome.ResolvedRoute!.UserServiceId.Should().Be(CodeServiceId);
        handler.Requests[2].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanonicalOrganizationServiceIsDenied_ReturnsAccessDenied()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(Service(
            CodeServiceId,
            "chrono-sandbox",
            true,
            true,
            "proxy:* sandbox:execute",
            credentialSourceType: "org",
            allowed: false))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.AdmissionDenied,
            "code_execution_route_access_denied");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceOmitsForwardingPolicy_FailsClosed()
    {
        var handler = new SequenceHandler(
            JsonResponse(
                """
                {
                  "services": [{
                    "id": "us-code-alpha",
                    "slug": "chrono-sandbox",
                    "catalog_service_id": "catalog-chrono-sandbox",
                    "is_active": true,
                    "inject_delegation_token": true,
                    "delegation_token_scope": "proxy:* sandbox:execute",
                    "credential_source": { "type": "personal" }
                  }]
                }
                """),
            JsonResponse(
                """
                {"success":true,"output":{"stdout":"ok","stderr":"","exit_code":0}}
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_policy_mismatch");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceOnlyForwardsCallerBearer_FailsClosed()
    {
        var handler = new SequenceHandler(
            JsonResponse(
                """
                {
                  "services": [{
                    "id": "us-code-alpha",
                    "slug": "chrono-sandbox",
                    "catalog_service_id": "catalog-chrono-sandbox",
                    "is_active": true,
                    "forward_access_token": true,
                    "credential_source": { "type": "personal" }
                  }]
                }
                """),
            JsonResponse(
                """
                {"success":true,"output":{"stdout":"ok","stderr":"","exit_code":0}}
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_policy_mismatch");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceOmitsBothCredentialPolicies_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(
            """
            {
              "services": [{
                "id": "us-code-alpha",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-chrono-sandbox",
                "is_active": true,
                "delegation_token_scope": "sandbox:execute",
                "credential_source": { "type": "personal" }
              }]
            }
            """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_policy_mismatch");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceOmitsCredentialSource_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(
            """
            {
              "services": [{
                "id": "us-code-alpha",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-chrono-sandbox",
                "is_active": true,
                "forward_access_token": true,
                "inject_delegation_token": true,
                "delegation_token_scope": "proxy:*"
              }]
            }
            """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TransportUnavailable,
            "code_execution_route_resolution_failed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultipleSharedServicesMatch_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(
            Service("us-code-alpha", "chrono-sandbox", true, true, "proxy:* sandbox:execute"),
            Service("us-code-beta", "chrono-sandbox", true, true, "proxy:* sandbox:execute"))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(userServiceId: null));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_ambiguous");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersonalAndOrganizationRoutesAreBothUsable_FailsClosedBeforeProxy()
    {
        // Eligibility never disambiguates: two differently configured canonical routes that each
        // deliver an accepted credential stay ambiguous rather than silently picking one.
        var handler = new SequenceHandler(JsonResponse(Inventory(
            Service("us-code-personal", "chrono-sandbox", true, true, "proxy:* sandbox:execute"),
            Service(
                "us-code-org",
                "chrono-sandbox",
                true,
                true,
                "proxy:* sandbox:execute",
                credentialSourceType: "org"))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(userServiceId: null));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_ambiguous");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAmbiguousRoutesAreDisambiguatedByExactId_UsesExactRoute()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(
                Service("us-code-personal", "chrono-sandbox", true, true, "proxy:* sandbox:execute"),
                Service(
                    CodeServiceId,
                    "chrono-sandbox",
                    true,
                    true,
                    "proxy:* sandbox:execute",
                    credentialSourceType: "org"))),
            JsonResponse(
                """
                {"success":true,"output":{"stdout":"ok","stderr":"","exit_code":0}}
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().BeNull();
        outcome.ResolvedRoute!.UserServiceId.Should().Be(CodeServiceId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCustomSameSlugRouteExists_IgnoresItAndUsesCanonicalRoute()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(
                Service(
                    "us-custom-shadow",
                    "chrono-sandbox",
                    false,
                    true,
                    "sandbox:execute",
                    catalogServiceId: null),
                Service(
                    CodeServiceId,
                    "chrono-sandbox",
                    true,
                    true,
                    "proxy:* sandbox:execute"))),
            JsonResponse(
                """
                {"success":true,"output":{"stdout":"ok","stderr":"","exit_code":0}}
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(userServiceId: null));

        outcome.Failure.Should().BeNull();
        outcome.ResolvedRoute!.UserServiceId.Should().Be(CodeServiceId);
        handler.Requests[2].PathAndQuery.Should().EndWith("?_nyxid_via=us-code-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoCanonicalCatalogRouteExists_ReturnsMissing()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(Service(
            "us-custom-only",
            "chrono-sandbox",
            false,
            true,
            "sandbox:execute",
            catalogServiceId: null))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(userServiceId: null));

        AssertRouteFailure(
            outcome,
            handler,
            CodeExecutionFailureKind.TargetNotConfigured,
            "code_execution_route_missing");
    }

    [Fact]
    public async Task ExecuteAsync_WhenProcessExitsNonZero_PreservesResultAndTypedFailure()
    {
        var handler = HandlerWithProxyResponse(JsonResponse(
            """
            {
              "success": false,
              "output": {
                "stdout": "partial output",
                "stderr": "traceback",
                "exit_code": 7,
                "execution_time_ms": 31
              },
              "error": {
                "code": "EXECUTION_FAILED",
                "message": "untrusted upstream detail"
              },
              "diagnostic_id": "diag-code-2"
            }
            """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().Be(new CodeExecutionResult(
            "partial output",
            "traceback",
            7,
            "diag-code-2",
            31));
        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.ExecutionFailed,
            "EXECUTION_FAILED",
            "Code execution exited unsuccessfully.",
            "diag-code-2"));
        outcome.ResolvedRoute!.UserServiceId.Should().Be(CodeServiceId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProcessFailureCodeIsNotOwned_UsesStableInternalCode()
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """
            {
              "success": false,
              "output": {
                "stdout": "partial output",
                "stderr": "provider detail",
                "exit_code": 9
              },
              "error": {
                "code": "UNTRUSTED_PROVIDER_CODE",
                "message": "untrusted upstream detail"
              }
            }
            """)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().NotBeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.ExecutionFailed,
            Code = "code_execution_failed",
            Message = "Code execution failed.",
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"output\":{\"stdout\":\"ok\",\"stderr\":\"\"}}")]
    [InlineData("{\"success\":true,\"output\":{\"stdout\":\"ok\",\"stderr\":\"\",\"exit_code\":1}}")]
    public async Task ExecuteAsync_WhenProxyOutputIsMalformed_ReturnsTypedFailure(string responseBody)
    {
        var logger = new RecordingLogger<NyxIdCodeExecutionPort>();
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(responseBody)), logger);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.MalformedOutput,
            Code = "code_execution_response_invalid",
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        AssertSafeCorrelatedLog(logger, outcome.Failure.DiagnosticId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSuccessfulDiagnosticIdIsUnsafe_UsesCorrelatedLocalFallback()
    {
        var logger = new RecordingLogger<NyxIdCodeExecutionPort>();
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """
            {
              "success": true,
              "output": {
                "stdout": "ok",
                "stderr": "",
                "exit_code": 0
              },
              "diagnostic_id": "unsafe/diagnostic"
            }
            """)), logger);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().BeNull();
        outcome.Result!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        AssertSafeCorrelatedLog(logger, outcome.Result.DiagnosticId);
        logger.Messages.Should().NotContain(message => message.Contains("unsafe/diagnostic"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CodeExecutionFailureKind.AdmissionDenied, "NYXID_PROXY_UNAUTHORIZED")]
    [InlineData(HttpStatusCode.Forbidden, CodeExecutionFailureKind.AdmissionDenied, "NYXID_PROXY_FORBIDDEN")]
    [InlineData(HttpStatusCode.NotFound, CodeExecutionFailureKind.TargetNotConfigured, "NYXID_PROXY_HTTP_404")]
    [InlineData(HttpStatusCode.TooManyRequests, CodeExecutionFailureKind.TransportUnavailable, "NYXID_PROXY_HTTP_429")]
    [InlineData(HttpStatusCode.BadGateway, CodeExecutionFailureKind.TransportUnavailable, "NYXID_PROXY_HTTP_502")]
    public async Task ExecuteAsync_WhenProxyRejectsRequest_ClassifiesHttpBoundary(
        HttpStatusCode statusCode,
        CodeExecutionFailureKind expectedKind,
        string expectedCode)
    {
        var logger = new RecordingLogger<NyxIdCodeExecutionPort>();
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """{"detail":"untrusted"}""",
            statusCode)), logger);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = expectedKind,
            Code = expectedCode,
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        AssertSafeCorrelatedLog(logger, outcome.Failure.DiagnosticId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenChronoReturnsTypedInfrastructureFailure_PreservesSafeEvidence()
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """
            {
              "success": false,
              "error": {
                "code": "SANDBOX_CREATION_FAILED",
                "message": "untrusted upstream detail"
              },
              "diagnostic_id": "diag-sandbox-1"
            }
            """,
            HttpStatusCode.BadGateway)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.TransportUnavailable,
            "SANDBOX_CREATION_FAILED",
            "The code execution sandbox is unavailable upstream.",
            "diag-sandbox-1"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNyxIdWrapsTypedTimeout_PreservesNestedSafeEvidence()
    {
        var nestedBody = JsonSerializer.Serialize(new
        {
            success = false,
            error = new { code = "SANDBOX_TIMEOUT", message = "untrusted" },
            diagnostic_id = "diag-timeout-1",
        });
        var envelope = JsonSerializer.Serialize(new
        {
            error = true,
            status = 504,
            body = nestedBody,
        });
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            envelope,
            HttpStatusCode.GatewayTimeout)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.TimedOut,
            "SANDBOX_TIMEOUT",
            "Code execution timed out upstream.",
            "diag-timeout-1"));
    }

    [Theory]
    [InlineData("UNAUTHENTICATED", HttpStatusCode.Unauthorized, false,
        "The code execution credential was rejected upstream.")]
    [InlineData("UNAUTHENTICATED", HttpStatusCode.Unauthorized, true,
        "The code execution credential was rejected upstream.")]
    [InlineData("FORBIDDEN", HttpStatusCode.Forbidden, false,
        "Code execution was denied upstream.")]
    [InlineData("FORBIDDEN", HttpStatusCode.Forbidden, true,
        "Code execution was denied upstream.")]
    public async Task ExecuteAsync_WhenChronoReturnsTypedAuthorizationFailure_PreservesChronoEvidence(
        string upstreamCode,
        HttpStatusCode statusCode,
        bool nested,
        string expectedMessage)
    {
        var chronoBody = JsonSerializer.Serialize(new
        {
            success = false,
            error = new { code = upstreamCode, message = "untrusted upstream detail" },
            diagnostic_id = "chrono-auth-diag",
        });
        var responseBody = nested
            ? JsonSerializer.Serialize(new
            {
                error = true,
                status = (int)statusCode,
                body = chronoBody,
            })
            : chronoBody;
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(responseBody, statusCode)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.AdmissionDenied,
            upstreamCode,
            expectedMessage,
            "chrono-auth-diag"));
        outcome.Failure!.Message.Should().NotContain("NyxID");
    }

    [Fact]
    public async Task ExecuteAsync_WhenChronoReturnsUnknownInfrastructureCode_UsesHttpClassification()
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """
            {
              "error": {
                "code": "UNTRUSTED_PROVIDER_CODE",
                "message": "untrusted upstream detail"
              },
              "diagnostic_id": "diag-generic-1"
            }
            """,
            HttpStatusCode.BadGateway)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.TransportUnavailable,
            "NYXID_PROXY_HTTP_502",
            "The code execution proxy request failed.",
            "diag-generic-1"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyTimesOut_ReturnsTypedTimeout()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                true,
                true,
                "proxy:* sandbox:execute"))),
            static _ => throw new OperationCanceledException("transport timeout"));
        var logger = new RecordingLogger<NyxIdCodeExecutionPort>();
        var port = CreatePort(handler, logger);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.TimedOut,
            Code = "code_execution_timed_out",
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        AssertSafeCorrelatedLog(logger, outcome.Failure.DiagnosticId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyTransportFails_ReturnsCorrelatedTypedFailure()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                true,
                true,
                "proxy:* sandbox:execute"))),
            static _ => throw new HttpRequestException("transport-secret"));
        var logger = new RecordingLogger<NyxIdCodeExecutionPort>();
        var port = CreatePort(handler, logger);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.TransportUnavailable,
            Code = "code_execution_transport_unavailable",
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        AssertSafeCorrelatedLog(logger, outcome.Failure.DiagnosticId);
        logger.Messages.Should().NotContain(message => message.Contains("transport-secret"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyResponseExceedsLimit_ReturnsTypedFailureWithoutReadingBody()
    {
        var oversizedContent = new ThrowOnReadContent(1_048_577);
        var handler = HandlerWithProxyResponse(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = oversizedContent,
        }));
        var logger = new RecordingLogger<NyxIdCodeExecutionPort>();
        var port = CreatePort(handler, logger);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.ResponseTooLarge,
            Code = "code_execution_response_too_large",
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        AssertSafeCorrelatedLog(logger, outcome.Failure.DiagnosticId);
        oversizedContent.ReadAttempted.Should().BeFalse();
    }

    private static NyxIdCodeExecutionPort CreatePort(
        HttpMessageHandler handler,
        ILogger<NyxIdCodeExecutionPort>? logger = null)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        return new NyxIdCodeExecutionPort(
            new TestNyxIdApiClientFactory(client),
            logger ?? NullLogger<NyxIdCodeExecutionPort>.Instance);
    }

    private static void AssertSafeCorrelatedLog(
        RecordingLogger<NyxIdCodeExecutionPort> logger,
        string? diagnosticId)
    {
        var message = logger.Messages.Should().ContainSingle().Which;
        message.Should().Contain(diagnosticId);
        message.Should().NotContain(ExecutionBearerToken);
        message.Should().NotContain(SourceReadableBearerToken);
        message.Should().NotContain(CodeServiceId);
        message.Should().NotContain("print(42)");
        message.Should().NotContain("untrusted");
        message.Should().Contain("bodyShape=");
        message.Should().Contain("diagnosticSource=");
    }

    private static CodeExecutionRequest Request(
        string? userServiceId,
        string executionCredential = ExecutionBearerToken,
        string? sourceReadableBearerToken = SourceReadableBearerToken,
        int timeoutSeconds = CodeExecutionContract.DefaultTimeoutSeconds,
        CodeExecutionNyxIdCredentialKind executionCredentialKind =
            CodeExecutionNyxIdCredentialKind.Bearer) =>
        new(
            CodeExecutionLanguage.Python,
            "print(42)",
            timeoutSeconds,
            new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                userServiceId,
                CodeExecutionRouteIdentitySource.CodeExecutionContract),
            new CodeExecutionCallerContext(
                executionCredential,
                sourceReadableBearerToken,
                executionCredentialKind));

    private static SequenceHandler HandlerWithProxyResponse(
        Func<CancellationToken, Task<HttpResponseMessage>> proxyResponse) =>
        new(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                true,
                true,
                "proxy:* sandbox:execute"))),
            proxyResponse);

    private static SequenceHandler HandlerWithProxyResponse(HttpResponseMessage proxyResponse) =>
        HandlerWithProxyResponse(_ => Task.FromResult(proxyResponse));

    private static Func<CancellationToken, Task<HttpResponseMessage>> JsonResponse(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        _ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static string Inventory(params object[] services) =>
        JsonSerializer.Serialize(new { services });

    private static object Service(
        string id,
        string slug,
        bool forwardAccessToken,
        bool injectDelegationToken,
        string delegationTokenScope,
        bool isActive = true,
        string credentialSourceType = "personal",
        bool allowed = true,
        string? catalogServiceId = "catalog-chrono-sandbox")
    {
        var credentialSource = credentialSourceType == "personal"
            ? (object)new { type = "personal" }
            : new
            {
                type = "org",
                org_id = "org-platform",
                org_name = "Aevatar Platform",
                role = "member",
                allowed,
            };
        return new
        {
            id,
            slug,
            catalog_service_id = catalogServiceId,
            is_active = isActive,
            forward_access_token = forwardAccessToken,
            inject_delegation_token = injectDelegationToken,
            delegation_token_scope = delegationTokenScope,
            credential_source = credentialSource,
        };
    }

    private static void AssertRouteFailure(
        CodeExecutionOutcome outcome,
        SequenceHandler handler,
        CodeExecutionFailureKind kind,
        string code)
    {
        outcome.Result.Should().BeNull();
        outcome.ResolvedRoute.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = kind,
            Code = code,
        });
        outcome.Failure!.DiagnosticId.Should().MatchRegex("^aevatar-[0-9a-f]{32}$");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
        handler.Requests[1].PathAndQuery.Should().Be("/api/v1/keys");
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class SequenceHandler(
        params Func<CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
    {
        private int _responseIndex;
        private string? _lastUserServicesResponse;

        public List<RecordedRequest> Requests { get; } = [];

        public Func<CancellationToken, Task<HttpResponseMessage>>? KeysResponse { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-API-Key", out var apiKeys)
                    ? apiKeys.ToArray()
                    : [],
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (request.RequestUri!.AbsolutePath == "/api/v1/keys")
            {
                return KeysResponse is null
                    ? await JsonResponse(KeysInventoryFromUserServices(_lastUserServicesResponse))(
                        cancellationToken)
                    : await KeysResponse(cancellationToken);
            }

            if (_responseIndex >= responses.Length)
                throw new InvalidOperationException("An unexpected HTTP request was sent.");
            var response = await responses[_responseIndex++](cancellationToken);
            if (request.RequestUri.AbsolutePath == "/api/v1/user-services")
            {
                _lastUserServicesResponse = response.Content is null
                    ? null
                    : await response.Content.ReadAsStringAsync(cancellationToken);
            }

            return response;
        }

        private static string KeysInventoryFromUserServices(string? response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return "{\"keys\":[]}";

            try
            {
                using var document = JsonDocument.Parse(response);
                if (!document.RootElement.TryGetProperty("services", out var services) ||
                    services.ValueKind != JsonValueKind.Array)
                {
                    return "{\"keys\":[]}";
                }

                var keys = services.EnumerateArray()
                    .Where(static service =>
                        service.ValueKind == JsonValueKind.Object &&
                        service.TryGetProperty("id", out _) &&
                        service.TryGetProperty("slug", out _) &&
                        service.TryGetProperty("is_active", out _) &&
                        service.TryGetProperty("credential_source", out _))
                    .Select(static service => new
                    {
                        id = service.GetProperty("id").GetString(),
                        slug = service.GetProperty("slug").GetString(),
                        catalog_service_id = service.TryGetProperty(
                            "catalog_service_id",
                            out var catalogServiceId)
                            ? catalogServiceId.GetString()
                            : null,
                        catalog_service_slug = service.GetProperty("slug").GetString(),
                        is_active = service.GetProperty("is_active").GetBoolean(),
                        status = "active",
                        connected = true,
                        credential_source = service.GetProperty("credential_source").Clone(),
                    })
                    .ToArray();
                return JsonSerializer.Serialize(new { keys });
            }
            catch (JsonException)
            {
                return "{\"keys\":[]}";
            }
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string PathAndQuery,
        string? Authorization,
        IReadOnlyList<string> ApiKeys,
        string? Body);

    private sealed class ThrowOnReadContent : HttpContent
    {
        public ThrowOnReadContent(long contentLength)
        {
            Headers.ContentLength = contentLength;
        }

        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("The oversized body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength!.Value;
            return true;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
