using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "AevatarCallModule")]
public sealed class AevatarCallModuleCoverageTests
{
    [Fact]
    public async Task HandleAsync_WhenNonAevatarStep_ShouldNoop()
    {
        var module = new AevatarCallModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "noop",
            StepType = "assign",
            Input = "ignored",
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenCommandSucceeds_ShouldPublishSuccess()
    {
        await RunWithMockAevatarCliAsync(
            """
            #!/bin/sh
            set -eu
            if [ "${1:-}" = "config" ] && [ "${2:-}" = "paths" ] && [ "${3:-}" = "show" ]; then
              echo '{"ok":true}'
              exit 0
            fi

            echo "unsupported args: $*" >&2
            exit 7
            """,
            async () =>
            {
                var module = new AevatarCallModule();
                var ctx = CreateContext();
                var request = new StepRequestEvent
                {
                    StepId = "ok",
                    StepType = "aevatar_call",
                    Parameters =
                    {
                        ["args"] = "config paths show",
                        ["timeout_ms"] = "20000",
                    },
                };

                await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

                var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
                completed.Success.Should().BeTrue();
                completed.Output.Should().Contain("\"ok\":true");
                completed.Metadata["aevatar.cli"].Should().Be("aevatar");
                completed.Metadata["aevatar.args"].Should().Be("config paths show");
            },
            """
            @echo off
            if "%~1"=="config" if "%~2"=="paths" if "%~3"=="show" (
              echo {"ok":true}
              exit /b 0
            )

            echo unsupported args: %* 1>&2
            exit /b 7
            """);
    }

    [Fact]
    public async Task HandleAsync_WhenCommandFailsAndContinue_ShouldKeepInput()
    {
        await RunWithMockAevatarCliAsync(
            """
            #!/bin/sh
            set -eu
            echo "intentional failure" >&2
            exit 2
            """,
            async () =>
            {
                var module = new AevatarCallModule();
                var ctx = CreateContext();
                var request = new StepRequestEvent
                {
                    StepId = "continue",
                    StepType = "aevatar_call",
                    Input = "fallback-input",
                    Parameters =
                    {
                        ["args"] = "config ui ensure --json",
                        ["on_error"] = "continue",
                    },
                };

                await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

                var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
                completed.Success.Should().BeTrue();
                completed.Output.Should().Be("fallback-input");
                completed.Metadata["aevatar.continued_on_error"].Should().Be("true");
                completed.Metadata.Should().ContainKey("aevatar.error");
            },
            """
            @echo off
            echo intentional failure 1>&2
            exit /b 2
            """);
    }

    [Fact]
    public async Task HandleAsync_WhenSecureVariableProvided_ShouldResolveStdInWithoutLeakingThroughRequestInput()
    {
        await RunWithMockAevatarCliAsync(
            """
            #!/bin/sh
            set -eu
            payload="$(cat)"
            printf 'stdin-length=%s\n' "${#payload}"
            exit 0
            """,
            async () =>
            {
                var module = new AevatarCallModule();
                var ctx = CreateContext();

                await module.HandleAsync(
                    Envelope(new SecureValueCapturedEvent
                    {
                        RunId = "run-1",
                        StepId = "capture-secret",
                        Variable = "api_key",
                        Value = "secret-123",
                    }),
                    ctx,
                    CancellationToken.None);
                ctx.Published.Clear();

                await module.HandleAsync(
                    Envelope(new StepRequestEvent
                    {
                        StepId = "secure-call",
                        StepType = "secure_aevatar_call",
                        RunId = "run-1",
                        Parameters =
                        {
                            ["args"] = "config llm api-key set provider --stdin",
                            ["stdin_mode"] = "secure_variable",
                            ["stdin_secret_variable"] = "api_key",
                        },
                    }),
                    ctx,
                    CancellationToken.None);

                var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
                completed.Success.Should().BeTrue();
                completed.Output.Should().Contain("stdin-length=10");
            },
            """
            @echo off
            setlocal EnableDelayedExpansion
            set /p INPUT=
            set "LEN=0"
            :measure
            if defined INPUT (
              set "INPUT=!INPUT:~1!"
              set /a LEN+=1
              goto measure
            )
            echo stdin-length=!LEN!
            exit /b 0
            """);
    }

    [Fact]
    public async Task HandleAsync_WhenSecureJsonPlaceholderUsed_ShouldEscapeSecretInTemplate()
    {
        await RunWithMockAevatarCliAsync(
            """
            #!/bin/sh
            set -eu
            payload="$(cat)"
            printf 'stdin=%s\n' "$payload"
            exit 0
            """,
            async () =>
            {
                var module = new AevatarCallModule();
                var ctx = CreateContext();

                await module.HandleAsync(
                    Envelope(new SecureValueCapturedEvent
                    {
                        RunId = "run-json",
                        StepId = "capture-secret",
                        Variable = "api_key",
                        Value = "sk-\"line\ntwo",
                    }),
                    ctx,
                    CancellationToken.None);
                ctx.Published.Clear();

                await module.HandleAsync(
                    Envelope(new StepRequestEvent
                    {
                        StepId = "secure-call-json",
                        StepType = "secure_aevatar_call",
                        RunId = "run-json",
                        Parameters =
                        {
                            ["args"] = "config llm api-key set provider --stdin",
                            ["stdin_mode"] = "secure_template",
                            ["stdin_template"] = """{"apiKey":"[[secure_json:api_key]]"}""",
                        },
                    }),
                    ctx,
                    CancellationToken.None);

                var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
                completed.Success.Should().BeTrue();
                completed.Output.Should().Contain("""stdin={"apiKey":"sk-\"line\ntwo"}""");
            },
            """
            @echo off
            setlocal EnableDelayedExpansion
            set /p INPUT=
            echo stdin=!INPUT!
            exit /b 0
            """);
    }

    private static RecordingEventHandlerContext CreateContext()
    {
        return new RecordingEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new StubAgent("aevatar-call-module-test-agent"),
            NullLogger.Instance);
    }

    private static async Task RunWithMockAevatarCliAsync(
        string unixScript,
        Func<Task> action,
        string? windowsScript = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aevatar-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await CreateMockAevatarCliAsync(tempDir, unixScript, windowsScript);
            await RunWithPathPrependedAsync(tempDir, action);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task RunWithPathPrependedAsync(string pathToPrepend, Func<Task> action)
    {
        const string envName = "PATH";
        var previous = Environment.GetEnvironmentVariable(envName) ?? string.Empty;
        var next = string.IsNullOrWhiteSpace(previous)
            ? pathToPrepend
            : $"{pathToPrepend}{Path.PathSeparator}{previous}";
        Environment.SetEnvironmentVariable(envName, next);
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    private static async Task<string> CreateMockAevatarCliAsync(
        string tempDir,
        string unixScript,
        string? windowsScript = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempDir, "aevatar.cmd");
            var content = windowsScript ?? throw new InvalidOperationException("Windows mock aevatar script is required.");
            await File.WriteAllTextAsync(scriptPath, content);
            return scriptPath;
        }

        var unixPath = Path.Combine(tempDir, "aevatar");
        await File.WriteAllTextAsync(unixPath, unixScript);
        File.SetUnixFileMode(
            unixPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return unixPath;
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "test-publisher",
            Direction = EventDirection.Self,
        };
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
