using System.Net;
using System.Net.Http;
using System.Text;
using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap.Tests;

public class ConnectorAndHostingCoverageTests
{
    [Fact]
    public async Task HttpConnector_ShouldRejectMethodAndPathAndPayloadAndHandleSuccess()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                ReasonPhrase = "OK",
            });
        var client = new HttpClient(handler);

        var connector = new HttpConnector(
            "http-test",
            "https://example.com",
            allowedMethods: ["POST"],
            allowedPaths: ["/allowed"],
            allowedInputKeys: ["q"],
            defaultHeaders: new Dictionary<string, string> { ["x-test"] = "1" },
            client: client);

        var methodRejected = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/allowed",
            Parameters = new Dictionary<string, string> { ["method"] = "GET" },
        });
        methodRejected.Success.Should().BeFalse();
        methodRejected.Error.Should().Contain("not allowed");

        var pathRejected = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/blocked",
            Parameters = new Dictionary<string, string> { ["method"] = "POST" },
        });
        pathRejected.Success.Should().BeFalse();
        pathRejected.Error.Should().Contain("path '/blocked' is not allowed");

        var schemaRejected = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/allowed",
            Payload = "{\"blocked\":1}",
            Parameters = new Dictionary<string, string> { ["method"] = "POST" },
        });
        schemaRejected.Success.Should().BeFalse();
        schemaRejected.Error.Should().Contain("schema violation");

        var success = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/allowed",
            Payload = "{\"q\":\"hi\"}",
            Parameters = new Dictionary<string, string>
            {
                ["method"] = "POST",
                ["content_type"] = "application/json",
            },
        });

        success.Success.Should().BeTrue();
        success.Output.Should().Contain("ok");
        success.Metadata.Should().ContainKey("connector.http.status_code");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.GetValues("x-test").Should().ContainSingle().Which.Should().Be("1");
    }

    [Fact]
    public async Task CliConnector_ShouldRejectPolicyAndExecuteCommand()
    {
        var connector = new CliConnector(
            "cli-test",
            command: "dotnet",
            fixedArguments: ["--info"],
            allowedOperations: ["status"],
            allowedInputKeys: ["q"]);

        var operationRejected = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "other",
        });
        operationRejected.Success.Should().BeFalse();
        operationRejected.Error.Should().Contain("not allowed");

        var schemaRejected = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "status",
            Payload = "{\"blocked\":1}",
        });
        schemaRejected.Success.Should().BeFalse();
        schemaRejected.Error.Should().Contain("schema violation");

        var success = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "",
            Payload = "",
            Parameters = new Dictionary<string, string> { ["timeout_ms"] = "2000" },
        });

        success.Success.Should().BeTrue();
        success.Metadata.Should().ContainKey("connector.cli.exit_code");
        success.Metadata["connector.cli.exit_code"].Should().Be("0");
    }

    [Fact]
    public async Task HttpConnector_ShouldCoverEscapeTimeoutExceptionAndSchemaBranches()
    {
        var escapeConnector = new HttpConnector(
            "http-escape",
            "https://example.com",
            allowedMethods: ["POST"],
            allowedPaths: ["/"],
            client: new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })));

        var escaped = await escapeConnector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "//evil.example/path",
        });
        escaped.Success.Should().BeFalse();
        escaped.Error.Should().Contain("escapes configured base_url");

        var schemaConnector = new HttpConnector(
            "http-schema",
            "https://example.com",
            allowedMethods: ["POST"],
            allowedPaths: ["/allowed"],
            allowedInputKeys: ["q"],
            client: new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                })));

        var nonObject = await schemaConnector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/allowed",
            Payload = "[]",
        });
        nonObject.Success.Should().BeFalse();
        nonObject.Error.Should().Contain("expected JSON object");

        var invalidJson = await schemaConnector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/allowed",
            Payload = "{oops",
        });
        invalidJson.Success.Should().BeFalse();
        invalidJson.Error.Should().Contain("invalid JSON");

        var timeoutConnector = new HttpConnector(
            "http-timeout",
            "https://example.com",
            allowedMethods: ["POST"],
            allowedPaths: ["/slow"],
            client: new HttpClient(new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(800))));

        var timeout = await timeoutConnector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/slow",
            Parameters = new Dictionary<string, string> { ["timeout_ms"] = "100" },
        });
        timeout.Success.Should().BeFalse();
        timeout.Error.Should().Contain("timeout");

        var exceptionConnector = new HttpConnector(
            "http-exception",
            "https://example.com",
            allowedMethods: ["POST"],
            allowedPaths: ["/"],
            client: new HttpClient(new ThrowingHttpMessageHandler(new InvalidOperationException("boom-http"))));

        var failed = await exceptionConnector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/x",
        });
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("boom-http");
    }

    [Fact]
    public async Task HttpConnector_ShouldSupportPathParameterGetBranchAndNonSuccessResponse()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("down", Encoding.UTF8, "text/plain"),
                ReasonPhrase = "Service Unavailable",
            });

        var connector = new HttpConnector(
            "http-get",
            "https://example.com",
            allowedMethods: ["GET"],
            allowedPaths: ["/"],
            client: new HttpClient(handler));

        var response = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "",
            Parameters = new Dictionary<string, string>
            {
                ["method"] = "GET",
                ["path"] = "v1/ping",
            },
        });

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("503");
        response.Metadata["connector.http.method"].Should().Be("GET");
        response.Metadata["connector.http.url"].Should().Contain("/v1/ping");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Content.Should().BeNull();
        handler.LastRequest.Headers.Accept.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CliConnector_ShouldCoverConstructorValidationFailureAndExceptionBranches()
    {
        Action missingName = () => _ = new CliConnector("", "dotnet");
        Action missingCommand = () => _ = new CliConnector("cli", "");
        missingName.Should().Throw<ArgumentException>();
        missingCommand.Should().Throw<ArgumentException>();

        var schemaConnector = new CliConnector(
            "cli-schema",
            command: "dotnet",
            fixedArguments: ["--version"],
            allowedInputKeys: ["q"]);

        var nonObject = await schemaConnector.ExecuteAsync(new ConnectorRequest
        {
            Payload = "[]",
        });
        nonObject.Success.Should().BeFalse();
        nonObject.Error.Should().Contain("expected JSON object");

        var invalidJson = await schemaConnector.ExecuteAsync(new ConnectorRequest
        {
            Payload = "{bad",
        });
        invalidJson.Success.Should().BeFalse();
        invalidJson.Error.Should().Contain("invalid JSON");

        var nonZero = new CliConnector("cli-nonzero", command: "dotnet");
        var failed = await nonZero.ExecuteAsync(new ConnectorRequest
        {
            Operation = "definitely-not-a-dotnet-command",
        });
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("process exited with code");
        failed.Metadata.Should().ContainKey("connector.cli.exit_code");

        var exceptionConnector = new CliConnector("cli-ex", command: "/definitely/not/exist");
        var ex = await exceptionConnector.ExecuteAsync(new ConnectorRequest());
        ex.Success.Should().BeFalse();
        ex.Error.Should().NotBeNullOrWhiteSpace();
        ex.Metadata.Should().ContainKey("connector.cli.command");
    }

    [Fact]
    public async Task CliConnector_ShouldCoverTimeoutBranch_OnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var timeoutConnector = new CliConnector(
            "cli-timeout",
            command: "/bin/sh",
            fixedArguments: ["-c", "sleep 2"],
            timeoutMs: 2000);

        var timeout = await timeoutConnector.ExecuteAsync(new ConnectorRequest
        {
            Parameters = new Dictionary<string, string> { ["timeout_ms"] = "100" },
        });

        timeout.Success.Should().BeFalse();
        timeout.Error.Should().Contain("timeout");
    }

    [Fact]
    public void ConnectorRegistration_ShouldBuildSupportedConnectorsOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"connector-reg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "connectors.json");

        File.WriteAllText(filePath,
            """
            {
              "connectors": [
                {
                  "name": "valid_http",
                  "type": "http",
                  "http": { "baseUrl": "https://example.com" }
                },
                {
                  "name": "unsupported",
                  "type": "custom"
                }
              ]
            }
            """);

        try
        {
            var registry = new InMemoryConnectorRegistry();
            var logger = NullLogger.Instance;
            var builders = new IConnectorBuilder[] { new HttpConnectorBuilder() };

            var added = ConnectorRegistration.RegisterConnectors(registry, builders, logger, filePath);

            added.Should().Be(1);
            registry.ListNames().Should().ContainSingle().Which.Should().Be("valid_http");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectorBootstrapHostedService_ShouldSkipWithoutRegistryAndLoadWithRegistry()
    {
        var servicesWithoutRegistry = new ServiceCollection();
        servicesWithoutRegistry.AddLogging();
        using var providerWithoutRegistry = servicesWithoutRegistry.BuildServiceProvider();

        var serviceWithoutRegistry = new ConnectorBootstrapHostedService(
            providerWithoutRegistry,
            NullLogger<ConnectorBootstrapHostedService>.Instance);
        await serviceWithoutRegistry.StartAsync(CancellationToken.None);
        await serviceWithoutRegistry.StopAsync(CancellationToken.None);

        var tempHome = Path.Combine(Path.GetTempPath(), $"connector-host-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        var previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
        Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, tempHome);

        try
        {
            File.WriteAllText(Path.Combine(tempHome, "connectors.json"),
                """
                {
                  "connectors": [
                    {
                      "name": "h1",
                      "type": "http",
                      "http": { "baseUrl": "https://example.com" }
                    }
                  ]
                }
                """);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
            services.AddSingleton<IConnectorBuilder, HttpConnectorBuilder>();

            using var provider = services.BuildServiceProvider();
            var service = new ConnectorBootstrapHostedService(
                provider,
                NullLogger<ConnectorBootstrapHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);

            var registry = provider.GetRequiredService<IConnectorRegistry>();
            registry.ListNames().Should().Contain("h1");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, previousHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task ActorRestoreHostedService_ShouldInvokeRuntimeRestore()
    {
        var runtime = new StubActorRuntime();
        var service = new ActorRestoreHostedService(runtime, NullLogger<ActorRestoreHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        runtime.RestoreCalled.Should().BeTrue();
    }

    [Fact]
    public void ConnectorBuilders_ShouldValidateAndBuild()
    {
        var cliBuilder = new CliConnectorBuilder();
        var httpBuilder = new HttpConnectorBuilder();

        var missingCli = new ConnectorConfigEntry
        {
            Name = "cli-missing",
            Type = "cli",
            Cli = new CliConnectorConfig { Command = "" },
        };
        cliBuilder.TryBuild(missingCli, NullLogger.Instance, out var missingCliConnector).Should().BeFalse();
        missingCliConnector.Should().BeNull();

        var invalidCli = new ConnectorConfigEntry
        {
            Name = "cli-invalid",
            Type = "cli",
            Cli = new CliConnectorConfig { Command = "https://example.com/cmd" },
        };
        cliBuilder.TryBuild(invalidCli, NullLogger.Instance, out var invalidCliConnector).Should().BeFalse();
        invalidCliConnector.Should().BeNull();

        var validCli = new ConnectorConfigEntry
        {
            Name = "cli-valid",
            Type = "cli",
            TimeoutMs = 1000,
            Cli = new CliConnectorConfig { Command = "echo", AllowedOperations = ["x"] },
        };
        cliBuilder.TryBuild(validCli, NullLogger.Instance, out var cliConnector).Should().BeTrue();
        cliConnector.Should().NotBeNull();
        cliConnector!.Type.Should().Be("cli");
        cliConnector.Name.Should().Be("cli-valid");

        var missingHttp = new ConnectorConfigEntry
        {
            Name = "http-missing",
            Type = "http",
            Http = new HttpConnectorConfig { BaseUrl = "" },
        };
        httpBuilder.TryBuild(missingHttp, NullLogger.Instance, out var missingHttpConnector).Should().BeFalse();
        missingHttpConnector.Should().BeNull();

        var validHttp = new ConnectorConfigEntry
        {
            Name = "http-valid",
            Type = "http",
            Http = new HttpConnectorConfig { BaseUrl = "https://example.com" },
        };
        httpBuilder.TryBuild(validHttp, NullLogger.Instance, out var httpConnector).Should().BeTrue();
        httpConnector.Should().NotBeNull();
        httpConnector!.Type.Should().Be("http");
        httpConnector.Name.Should().Be("http-valid");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class DelayedHttpMessageHandler : HttpMessageHandler
    {
        public DelayedHttpMessageHandler(TimeSpan delay)
        {
            _ = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await pending.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
                ReasonPhrase = "OK",
            };
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw _exception;
        }
    }

    private sealed class InMemoryConnectorRegistry : IConnectorRegistry
    {
        private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);

        public void Register(IConnector connector) => _connectors[connector.Name] = connector;

        public bool TryGet(string name, out IConnector? connector)
        {
            var found = _connectors.TryGetValue(name, out var value);
            connector = value;
            return found;
        }

        public IReadOnlyList<string> ListNames() => _connectors.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public bool RestoreCalled { get; private set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RestoreAllAsync(CancellationToken ct = default)
        {
            RestoreCalled = true;
            return Task.CompletedTask;
        }
    }
}
