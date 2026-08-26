using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class NyxIdDurableCodeExecutionPortTests
{
    private const string OperationId = "op_0123456789abcdefghijklmnopqrstuv";
    private const string IdempotencyKey =
        "tool:v1:operation:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-14T10:00:00Z");

    [Fact]
    public async Task SubmitAsync_UsesPublicApiAndBuildsCanonicalPathsFromOperationId()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.Accepted,
            $$"""
              {
                "operation_id":"{{OperationId}}",
                "status":"queued",
                "status_url":"https://attacker.invalid/status",
                "result_url":"//attacker.invalid/result",
                "cancel_url":"/other/cancel",
                "created_at":"2026-08-14T10:00:00Z",
                "expires_at":"2026-08-15T10:00:00Z"
              }
              """,
            response =>
            {
                response.Headers.Location = new Uri("https://attacker.invalid/status");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            }));
        var port = CreatePort(handler);

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest() with { TimeoutSeconds = 300 },
            IdempotencyKey));

        outcome.Failure.Should().BeNull();
        outcome.Receipt.Should().NotBeNull();
        outcome.Receipt!.ProviderOperationId.Should().Be(OperationId);
        outcome.Receipt.StatusPath.Should().Be($"/executions/{OperationId}");
        outcome.Receipt.ResultPath.Should().Be($"/executions/{OperationId}/result");
        outcome.Receipt.CancelPath.Should().Be($"/executions/{OperationId}/cancel");
        outcome.Receipt.RetryAfter.Should().Be(TimeSpan.FromSeconds(3));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be(
            $"https://nyx-public.example/api/v1/proxy/s/chrono-sandbox/executions?_nyxid_via=us-code-alpha");
        handler.Requests[0].Headers["Idempotency-Key"].Should().Equal(IdempotencyKey);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        body.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("language", "script", "timeout_secs");
        body.RootElement.GetProperty("language").GetString().Should().Be("python");
        body.RootElement.GetProperty("script").GetString().Should().Be("print(42)");
        body.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(300);
    }

    [Fact]
    public async Task SubmitAsync_ExactAdmittedRouteSkipsCatalogWhenSourceReadableTokenExists()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.Accepted,
            $$"""
              {
                "operation_id":"{{OperationId}}",
                "status":"queued",
                "created_at":"2026-08-14T10:00:00Z",
                "expires_at":"2026-08-15T10:00:00Z"
              }
              """));
        var port = CreatePort(handler);
        var execution = new CodeExecutionRequest(
            CodeExecutionLanguage.Python,
            "print(42)",
            CodeExecutionContract.DefaultTimeoutSeconds,
            Route(),
            new CodeExecutionCallerContext(
                "execution-token",
                "source-readable-token",
                CodeExecutionNyxIdCredentialKind.Bearer));

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            execution,
            IdempotencyKey));

        outcome.Failure.Should().BeNull();
        outcome.Receipt!.ResolvedRoute.Should().Be(Route());
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be(
            "https://nyx-public.example/api/v1/proxy/s/chrono-sandbox/executions?_nyxid_via=us-code-alpha");
    }

    [Fact]
    public async Task SubmitAsync_AgentKeyInjectsExactDurableAuthorityHeaders()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.Accepted,
            $$"""
              {
                "operation_id":"{{OperationId}}",
                "status":"queued",
                "created_at":"2026-08-14T10:00:00Z",
                "expires_at":"2026-08-15T10:00:00Z"
              }
              """));
        var port = CreatePort(handler, timeProvider: new FakeTimeProvider(Now));
        var execution = ExecutionRequest() with
        {
            Caller = new CodeExecutionCallerContext(
                "scheduled-agent-key",
                null,
                CodeExecutionNyxIdCredentialKind.AgentKey,
                DurableGrant()),
        };

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            execution,
            IdempotencyKey));

        outcome.Failure.Should().BeNull();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers["Authorization"].Should().Equal("Bearer scheduled-agent-key");
        handler.Requests[0].Headers.Should().NotContainKey("X-API-Key");
        handler.Requests[0].Headers["Idempotency-Key"].Should().Equal(IdempotencyKey);
        handler.Requests[0].Headers["X-NyxID-Durable-Grant-Id"].Should().Equal("grant-executions");
        handler.Requests[0].Headers["X-NyxID-Operation-Id"].Should().Equal(IdempotencyKey);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("route-mismatch")]
    [InlineData("expired")]
    [InlineData("legacy")]
    public async Task SubmitAsync_AgentKeyWithoutOneValidExactGrant_FailsBeforeHttp(string variant)
    {
        var handler = new SequenceHandler(_ => throw new InvalidOperationException("must not dispatch"));
        var grant = variant == "missing" ? null : DurableGrant();
        switch (variant)
        {
            case "route-mismatch":
                grant!.UserServiceId = "us-code-other";
                break;
            case "expired":
                grant!.ExpiresAtUnixMs = Now.ToUnixTimeMilliseconds();
                break;
            case "legacy":
                grant = new NyxIdDurableOperationGrantRef
                {
                    GrantId = "grant-legacy",
                    ApiKeyId = "key-schedule",
                };
                break;
        }
        var port = CreatePort(handler, timeProvider: new FakeTimeProvider(Now));
        var execution = ExecutionRequest() with
        {
            Caller = new CodeExecutionCallerContext(
                "scheduled-agent-key",
                null,
                CodeExecutionNyxIdCredentialKind.AgentKey,
                grant),
        };

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            execution,
            IdempotencyKey));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_durable_grant_rebind_required",
            Retryable = false,
        });
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("operation-alpha")]
    [InlineData("tool:v1:operation:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("tool:v1:operation:0123456789abcdef")]
    public async Task SubmitAsync_WithoutCanonicalStableOperationId_FailsBeforeHttp(
        string operationId)
    {
        var handler = new SequenceHandler(_ => throw new InvalidOperationException("must not dispatch"));
        var port = CreatePort(handler);

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest(),
            operationId));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.AdmissionDenied,
            Code = "durable_code_execution_request_invalid",
            Retryable = false,
        });
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_PersonalExecutionRouteUsesItsAdmittedSlug()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.Accepted,
            $$"""
              {
                "operation_id":"{{OperationId}}",
                "status":"queued",
                "created_at":"2026-08-14T10:00:00Z",
                "expires_at":"2026-08-15T10:00:00Z"
              }
              """));
        var port = CreatePort(handler);
        var route = new CodeExecutionRouteIdentity(
            "chrono-sandbox-aevatar",
            "us-code-aevatar",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);
        var execution = ExecutionRequest() with { Route = route };

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            execution,
            IdempotencyKey));

        outcome.Failure.Should().BeNull();
        outcome.Receipt!.ResolvedRoute.Should().Be(route);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be(
            "https://nyx-public.example/api/v1/proxy/s/chrono-sandbox-aevatar/executions?_nyxid_via=us-code-aevatar");
    }

    [Fact]
    public async Task GetStatusAsync_NotModifiedPreservesEtagAndRetryAfter()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.NotModified,
            string.Empty,
            response =>
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"v7\"");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            }));
        var port = CreatePort(handler);

        var outcome = await port.GetStatusAsync(OperationRequest("\"v6\""));

        outcome.Failure.Should().BeNull();
        outcome.NotModified.Should().BeTrue();
        outcome.ETag.Should().Be("\"v7\"");
        outcome.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().EndWith(
            $"/executions/{OperationId}?_nyxid_via=us-code-alpha");
        handler.Requests[0].Headers["If-None-Match"].Should().Equal("\"v6\"");
        handler.Requests[0].Headers.Should().NotContainKey("Idempotency-Key");
    }

    [Fact]
    public async Task GetStatusAsync_AgentKeyWithoutLifecycleGrantFailsBeforeHttp()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.NotModified,
            string.Empty,
            response => response.Headers.ETag = new EntityTagHeaderValue("\"v7\"")));
        var port = CreatePort(handler);
        var request = OperationRequest("\"v6\"") with
        {
            Caller = new CodeExecutionCallerContext(
                "scheduled-agent-key",
                null,
                CodeExecutionNyxIdCredentialKind.AgentKey),
        };

        var outcome = await port.GetStatusAsync(request);

        outcome.Snapshot.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_durable_lifecycle_authority_unavailable",
            Retryable = false,
        });
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetResultAsync_AgentKeyWithoutLifecycleGrantFailsBeforeHttp()
    {
        var handler = new SequenceHandler(_ => throw new InvalidOperationException("must not dispatch"));
        var port = CreatePort(handler);
        var request = OperationRequest() with
        {
            Caller = new CodeExecutionCallerContext(
                "scheduled-agent-key",
                null,
                CodeExecutionNyxIdCredentialKind.AgentKey,
                DurableGrant()),
        };

        var outcome = await port.GetResultAsync(request);

        outcome.Outcome.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_durable_lifecycle_authority_unavailable",
            Retryable = false,
        });
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelAsync_AgentKeyWithoutLifecycleGrantFailsBeforeHttp()
    {
        var handler = new SequenceHandler(_ => throw new InvalidOperationException("must not dispatch"));
        var port = CreatePort(handler);
        var request = OperationRequest() with
        {
            Caller = new CodeExecutionCallerContext(
                "scheduled-agent-key",
                null,
                CodeExecutionNyxIdCredentialKind.AgentKey,
                DurableGrant()),
        };

        var outcome = await port.CancelAsync(request);

        outcome.Snapshot.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_durable_lifecycle_authority_unavailable",
            Retryable = false,
        });
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatusAsync_RetryAfterIsCappedAtThirtySeconds()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.NotModified,
            string.Empty,
            response =>
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"v7\"");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            }));
        var port = CreatePort(handler);

        var outcome = await port.GetStatusAsync(OperationRequest("\"v6\""));

        outcome.Failure.Should().BeNull();
        outcome.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task GetStatusAsync_ParsesTypedSnapshot()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.OK,
            $$"""
              {
                "operation_id":"{{OperationId}}",
                "status":"running",
                "phase":"execute",
                "cleanup_status":"not_started",
                "version":7,
                "cancel_requested":false,
                "result_available":false,
                "created_at":"2026-08-14T10:00:00Z",
                "updated_at":"2026-08-14T10:01:00Z",
                "first_event_at":"2026-08-14T10:00:00Z",
                "last_event_at":"2026-08-14T10:01:00Z",
                "expires_at":"2026-08-15T10:00:00Z",
                "timings":{}
              }
              """,
            response =>
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"v7\"");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            }));
        var port = CreatePort(handler);

        var outcome = await port.GetStatusAsync(OperationRequest());

        outcome.Failure.Should().BeNull();
        outcome.Snapshot.Should().BeEquivalentTo(new
        {
            ProviderOperationId = OperationId,
            State = DurableCodeExecutionState.Running,
            Phase = DurableCodeExecutionPhase.Execute,
            CleanupState = DurableCodeExecutionCleanupState.NotStarted,
            Version = 7L,
            ETag = "\"v7\"",
            RetryAfter = TimeSpan.FromSeconds(2),
        });
    }

    [Fact]
    public async Task GetStatusAsync_KnownOperationNotFoundIsPermanent()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.NotFound,
            """{"success":false,"error":{"code":"OPERATION_NOT_FOUND"}}"""));
        var port = CreatePort(handler);

        var outcome = await port.GetStatusAsync(OperationRequest());

        outcome.Snapshot.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.OperationNotFound,
            Retryable = false,
        });
    }

    [Fact]
    public async Task GetResultAsync_NotTerminalIsTypedPending()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.Conflict,
            """{"success":false,"error":{"code":"OPERATION_NOT_TERMINAL"}}""",
            response => response.Headers.RetryAfter =
                new RetryConditionHeaderValue(TimeSpan.FromSeconds(4))));
        var port = CreatePort(handler);

        var outcome = await port.GetResultAsync(OperationRequest());

        outcome.Failure.Should().BeNull();
        outcome.Pending.Should().BeTrue();
        outcome.RetryAfter.Should().Be(TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData("sandbox_create", DurableCodeExecutionPhase.SandboxCreate)]
    [InlineData("credential-secret", DurableCodeExecutionPhase.Unspecified)]
    public async Task GetResultAsync_TerminalTimeoutPreservesOnlyAllowlistedProviderPhase(
        string providerPhase,
        DurableCodeExecutionPhase expectedPhase)
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.OK,
            $$"""
              {
                "success":false,
                "diagnostic_id":"diag-timeout-safe",
                "source":"console.log('must-not-escape')",
                "operation_id":"op-secret-provider-value",
                "user_service_id":"us-secret-provider-value",
                "credential":"credential-secret-value",
                "error":{
                  "code":"SANDBOX_TIMEOUT",
                  "message":"untrusted provider detail",
                  "phase":"{{providerPhase}}"
                }
              }
              """));
        var port = CreatePort(handler);

        var outcome = await port.GetResultAsync(OperationRequest());

        outcome.Outcome.Should().BeNull();
        outcome.Failure.Should().NotBeNull();
        outcome.Failure!.Code.Should().Be("SANDBOX_TIMEOUT");
        outcome.Failure.DiagnosticId.Should().Be("diag-timeout-safe");
        outcome.Failure.ProviderPhase.Should().Be(expectedPhase);
        outcome.Failure.Message.Should().Be(
            "Durable code execution failed before producing a result.");
        var committed = JsonSerializer.Serialize(outcome.Failure);
        committed.Should().NotContain("must-not-escape");
        committed.Should().NotContain("op-secret-provider-value");
        committed.Should().NotContain("us-secret-provider-value");
        committed.Should().NotContain("credential-secret-value");
        committed.Should().NotContain("untrusted provider detail");
    }

    [Fact]
    public async Task CancelAsync_UsesCanonicalPublicPostWithJsonBodyAndParsesSnapshot()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.OK,
            $$"""
              {
                "operation_id":"{{OperationId}}",
                "status":"cancelled",
                "phase":"complete",
                "cleanup_status":"complete",
                "version":8,
                "cancel_requested":true,
                "result_available":true,
                "created_at":"2026-08-14T10:00:00Z",
                "updated_at":"2026-08-14T10:01:00Z",
                "expires_at":"2026-08-15T10:00:00Z",
                "terminal_at":"2026-08-14T10:01:00Z",
                "failure":{"code":"EXECUTION_CANCELLED","message":"Execution was cancelled"},
                "timings":{}
              }
              """,
            response => response.Headers.ETag = new EntityTagHeaderValue("\"v8\"")));
        var port = CreatePort(handler);

        var outcome = await port.CancelAsync(OperationRequest());

        outcome.Failure.Should().BeNull();
        outcome.Snapshot.Should().BeEquivalentTo(new
        {
            ProviderOperationId = OperationId,
            State = DurableCodeExecutionState.Cancelled,
            Phase = DurableCodeExecutionPhase.Complete,
            CleanupState = DurableCodeExecutionCleanupState.Complete,
            Version = 8L,
            CancelRequested = true,
            ResultAvailable = true,
            ETag = "\"v8\"",
        });
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post.Method);
        handler.Requests[0].Uri.Should().Be(
            $"https://nyx-public.example/api/v1/proxy/s/chrono-sandbox/executions/{OperationId}/cancel?_nyxid_via=us-code-alpha");
        handler.Requests[0].Body.Should().Be("{}");
    }

    [Fact]
    public async Task SubmitAsync_TransportLossRequiresSameKeyRecovery()
    {
        var handler = new SequenceHandler(
            _ => throw new HttpRequestException("possibly dispatched"));
        var port = CreatePort(handler);

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest(),
            IdempotencyKey));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.SubmissionUncertain,
            Retryable = true,
        });
    }

    [Fact]
    public async Task SubmitAsync_AcceptedOversizedReceiptRequiresSameKeyRecovery()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.Accepted,
            "{}",
            response => response.Content.Headers.ContentLength = 1_048_577));
        var port = CreatePort(handler);

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest(),
            IdempotencyKey));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.SubmissionUncertain,
            Retryable = true,
        });
    }

    [Fact]
    public async Task SubmitAsync_AcceptedMalformedReceiptRequiresSameKeyRecovery()
    {
        var handler = new SequenceHandler(_ => Response(HttpStatusCode.Accepted, "{}"));
        var port = CreatePort(handler);

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest(),
            IdempotencyKey));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.SubmissionUncertain,
            Retryable = true,
        });
    }

    [Fact]
    public async Task SubmitAsync_UnreadableServerFailureRequiresSameKeyRecovery()
    {
        var handler = new SequenceHandler(_ => Response(
            HttpStatusCode.BadGateway,
            "{}",
            response => response.Content.Headers.ContentLength = 1_048_577));
        var port = CreatePort(handler);

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest(),
            IdempotencyKey));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.SubmissionUncertain,
            Retryable = true,
        });
    }

    [Fact]
    public async Task SubmitAsync_WithoutPublicApiEndpointFailsBeforeDispatch()
    {
        var handler = new SequenceHandler(_ => throw new InvalidOperationException("must not dispatch"));
        var port = CreatePort(handler, new NyxIdToolOptions
        {
            BaseUrl = "http://nyx-internal:3001",
            InternalApiBaseUrl = "http://nyx-internal:3001",
        });

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest(),
            IdempotencyKey));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.TargetNotConfigured,
            Retryable = false,
            Code = "durable_code_execution_public_api_not_configured",
        });
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task SubmitAsync_TimeoutOutsideContractFailsBeforeDispatch(int timeoutSeconds)
    {
        var handler = new SequenceHandler();
        var port = CreatePort(handler);

        var outcome = await port.SubmitAsync(new DurableCodeExecutionSubmitRequest(
            ExecutionRequest() with { TimeoutSeconds = timeoutSeconds },
            IdempotencyKey));

        outcome.Receipt.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = DurableCodeExecutionFailureKind.AdmissionDenied,
            Code = "durable_code_execution_request_invalid",
            Retryable = false,
        });
        handler.Requests.Should().BeEmpty();
    }

    private static NyxIdCodeExecutionPort CreatePort(
        HttpMessageHandler handler,
        NyxIdToolOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        options ??= new NyxIdToolOptions
        {
            BaseUrl = "https://legacy.example",
            InternalApiBaseUrl = "http://nyx-internal:3001",
            ApiBaseUrl = "https://nyx-public.example",
            PublicTransportFallbackBaseUrl = "https://fallback.invalid",
        };
        var client = new NyxIdApiClient(
            options,
            new HttpClient(handler));
        return new NyxIdCodeExecutionPort(
            new TestNyxIdApiClientFactory(client),
            NullLogger<NyxIdCodeExecutionPort>.Instance,
            timeProvider);
    }

    private static NyxIdDurableOperationGrantRef DurableGrant() => new()
    {
        GrantId = "grant-executions",
        ApiKeyId = "key-schedule",
        UserServiceId = "us-code-alpha",
        EndpointId = "endpoint-executions",
        HttpMethod = NyxIdDurableOperationHttpMethod.Post,
        NormalizedPathTemplate = "/executions",
        ContractDigest =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        ValidFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
        ExpiresAtUnixMs = Now.AddDays(1).ToUnixTimeMilliseconds(),
        ReplayPolicy = NyxIdDurableOperationReplayPolicy.DownstreamIdempotencyKey,
        ClientAuditBinding = new NyxIdDurableOperationClientAuditBinding
        {
            Platform = "lark",
            ScheduleId = "schedule-alpha",
            WorkflowRevision = "revision-7",
            CallSite = "code_execute",
        },
    };

    private static CodeExecutionRequest ExecutionRequest() =>
        new(
            CodeExecutionLanguage.Python,
            "print(42)",
            CodeExecutionContract.DefaultTimeoutSeconds,
            Route(),
            new CodeExecutionCallerContext(
                "execution-token",
                null,
                CodeExecutionNyxIdCredentialKind.Bearer));

    private static DurableCodeExecutionOperationRequest OperationRequest(string? etag = null) =>
        new(
            OperationId,
            Route(),
            new CodeExecutionCallerContext(
                "execution-token",
                null,
                CodeExecutionNyxIdCredentialKind.Bearer),
            etag);

    private static CodeExecutionRouteIdentity Route() =>
        new(
            CodeExecutionContract.ServiceSlug,
            "us-code-alpha",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string content,
        Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
        configure?.Invoke(response);
        return response;
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class SequenceHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.ToDictionary(
                    static header => header.Key,
                    static header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses[_index++](request);
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string? Body);
}
