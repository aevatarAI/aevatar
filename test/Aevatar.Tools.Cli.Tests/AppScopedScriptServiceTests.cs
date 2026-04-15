using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppScopedScriptServiceTests
{
    [Fact]
    public async Task ListAsync_WhenBackendRedirectsToLogin_ShouldThrowAuthRequiredException()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers =
            {
                Location = new Uri("https://login.example/sign-in", UriKind.Absolute),
            },
        });

        var act = () => service.ListAsync("scope-1");

        var exception = await Assert.ThrowsAsync<AppApiException>(act);
        exception.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        exception.Code.Should().Be(AppApiErrors.BackendAuthRequiredCode);
        exception.LoginUrl.Should().Be("https://login.example/sign-in");
    }

    [Fact]
    public async Task ListAsync_WhenBackendReturnsHtml_ShouldThrowInvalidResponseException()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<!DOCTYPE html><html><body>sign in</body></html>",
                Encoding.UTF8,
                "text/html"),
        });

        var act = () => service.ListAsync("scope-1");

        var exception = await Assert.ThrowsAsync<AppApiException>(act);
        exception.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        exception.Code.Should().Be(AppApiErrors.BackendInvalidResponseCode);
        exception.Message.Should().Be("Script backend returned a non-JSON response.");
    }

    [Fact]
    public async Task ProposeEvolutionAsync_ShouldCallScopedBackendRoute()
    {
        HttpRequestMessage? captured = null;
        var service = CreateService(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accepted = true,
                    proposalId = "scope-1:proposal-1",
                    scriptId = "script-1",
                    baseRevision = "rev-1",
                    candidateRevision = "rev-2",
                    status = "promoted",
                    failureReason = "",
                    definitionActorId = "definition-1",
                    catalogActorId = "catalog-1",
                    validationReport = new
                    {
                        isSuccess = true,
                        diagnostics = Array.Empty<string>(),
                    },
                }),
            };
        });

        var decision = await service.ProposeEvolutionAsync(
            "scope-1",
            new AppScopeScriptEvolutionRequest(
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class DemoScriptV2 {}",
                CandidateSourceHash: "hash-2",
                Reason: "rollout",
                ProposalId: "proposal-1"));

        decision.Accepted.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.PathAndQuery.Should().Be("/api/scopes/scope-1/scripts/script-1/evolutions/proposals");
    }

    [Fact]
    public async Task SaveAsync_ShouldReturnAcceptedSummaryWithoutImmediateReadBack()
    {
        var requests = new List<string>();
        var service = CreateService(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.PathAndQuery.Should().Be("/api/scopes/scope-1/scripts/script-1");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                StatusCode = HttpStatusCode.Accepted,
                Content = JsonContent.Create(new
                {
                    acceptedScript = new
                    {
                        scopeId = "scope-1",
                        scriptId = "script-1",
                        catalogActorId = "catalog-1",
                        definitionActorId = "definition-1",
                        revisionId = "rev-1",
                        sourceHash = "hash-1",
                        acceptedAt = DateTimeOffset.UtcNow,
                        proposalId = "scope-1:script-1:rev-1",
                        expectedBaseRevision = "rev-0",
                    },
                    definitionCommand = new
                    {
                        actorId = "definition-1",
                        commandId = "definition-command-1",
                        correlationId = "definition-correlation-1",
                    },
                    catalogCommand = new
                    {
                        actorId = "catalog-1",
                        commandId = "catalog-command-1",
                        correlationId = "catalog-correlation-1",
                    },
                }),
                Headers =
                {
                    Location = new Uri("https://backend.example/api/scopes/scope-1/scripts/script-1", UriKind.Absolute),
                },
            };
        });

        var accepted = await service.SaveAsync(
            "scope-1",
            new AppScopeScriptSaveRequest(
                ScriptId: "script-1",
                SourceText: "public sealed class DemoScript {}",
                RevisionId: "rev-1"));

        accepted.ScopeId.Should().Be("scope-1");
        accepted.ScriptId.Should().Be("script-1");
        accepted.RevisionId.Should().Be("rev-1");
        accepted.CatalogActorId.Should().Be("catalog-1");
        accepted.DefinitionActorId.Should().Be("definition-1");
        accepted.AcceptedScript.AcceptedAt.Should().NotBe(default);
        accepted.SubmittedSource.SourceText.Should().Be("public sealed class DemoScript {}");
        accepted.SubmittedSource.DefinitionActorId.Should().Be("definition-1");
        accepted.SubmittedSource.Revision.Should().Be("rev-1");
        accepted.SubmittedSource.SourceHash.Should().Be("hash-1");
        accepted.DefinitionCommand.CommandId.Should().Be("definition-command-1");
        accepted.CatalogCommand.CommandId.Should().Be("catalog-command-1");
        accepted.ProposalId.Should().Be("scope-1:script-1:rev-1");
        accepted.ExpectedBaseRevision.Should().Be("rev-0");
        requests.Should().ContainSingle().Which.Should().Be("PUT /api/scopes/scope-1/scripts/script-1");
    }

    [Fact]
    public async Task ObserveSaveAsync_ShouldCallScopedObservationRoute()
    {
        HttpRequestMessage? captured = null;
        var service = CreateService(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    scopeId = "scope-1",
                    scriptId = "script-1",
                    status = "applied",
                    message = "Revision 'rev-1' is now active.",
                    currentScript = new
                    {
                        scopeId = "scope-1",
                        scriptId = "script-1",
                        catalogActorId = "catalog-1",
                        definitionActorId = "definition-1",
                        activeRevision = "rev-1",
                        activeSourceHash = "hash-1",
                        updatedAt = DateTimeOffset.UtcNow,
                    },
                    isTerminal = true,
                }),
            };
        });

        var result = await service.ObserveSaveAsync(
            "scope-1",
            "script-1",
            new AppScopeScriptSaveObservationRequest(
                RevisionId: "rev-1",
                DefinitionActorId: "definition-1",
                SourceHash: "hash-1",
                ProposalId: "scope-1:script-1:rev-1",
                ExpectedBaseRevision: "rev-0",
                AcceptedAt: DateTimeOffset.UtcNow));

        result.Status.Should().Be("applied");
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.PathAndQuery.Should().Be("/api/scopes/scope-1/scripts/script-1/save-observation");
    }

    [Fact]
    public async Task SaveAsync_ShouldNotAwaitChronoStorageUploadBeforeReturning()
    {
        var storagePort = new BlockingScriptStoragePort();
        var service = CreateService(
            responseFactory: request =>
            {
                request.Method.Should().Be(HttpMethod.Put);
                request.RequestUri!.PathAndQuery.Should().Be("/api/scopes/scope-1/scripts/script-1");
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(new
                    {
                        acceptedScript = new
                        {
                            scopeId = "scope-1",
                            scriptId = "script-1",
                            catalogActorId = "catalog-1",
                            definitionActorId = "definition-1",
                            revisionId = "rev-1",
                            sourceHash = "hash-1",
                            acceptedAt = DateTimeOffset.UtcNow,
                            proposalId = "scope-1:script-1:rev-1",
                            expectedBaseRevision = "rev-0",
                        },
                        definitionCommand = new
                        {
                            actorId = "definition-1",
                            commandId = "definition-command-1",
                            correlationId = "definition-correlation-1",
                        },
                        catalogCommand = new
                        {
                            actorId = "catalog-1",
                            commandId = "catalog-command-1",
                            correlationId = "catalog-correlation-1",
                        },
                    }),
                };
            },
            scriptStoragePort: storagePort);

        var saveTask = service.SaveAsync(
            "scope-1",
            new AppScopeScriptSaveRequest(
                ScriptId: "script-1",
                SourceText: "public sealed class DemoScript {}",
                RevisionId: "rev-1"));

        var accepted = await saveTask.WaitAsync(TimeSpan.FromSeconds(1));

        accepted.ScriptId.Should().Be("script-1");
        storagePort.Started.Wait(TimeSpan.FromSeconds(1)).Should().BeTrue();
        saveTask.IsCompletedSuccessfully.Should().BeTrue();

        storagePort.Release.Set();
        await storagePort.UploadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static AppScopedScriptService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        IScriptStoragePort? scriptStoragePort = null)
    {
        var handler = new StubHttpMessageHandler(responseFactory);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };

        return new AppScopedScriptService(
            new StubHttpClientFactory(httpClient),
            scriptStoragePort: scriptStoragePort);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = _responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingScriptStoragePort : IScriptStoragePort
    {
        public ManualResetEventSlim Started { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public TaskCompletionSource<bool> UploadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task UploadScriptAsync(string scriptId, string content, CancellationToken ct = default)
        {
            scriptId.Should().Be("script-1");
            content.Should().Be("public sealed class DemoScript {}");
            Started.Set();
            await Task.Yield();
            Release.Wait(ct);
            UploadCompleted.TrySetResult(true);
            await Task.CompletedTask;
        }
    }
}
