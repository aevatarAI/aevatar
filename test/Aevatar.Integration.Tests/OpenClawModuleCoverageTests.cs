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
[Trait("Feature", "OpenClawModule")]
public sealed class OpenClawModuleCoverageTests
{
    [Fact]
    public async Task HandleAsync_WhenNonOpenClawStep_ShouldNoop()
    {
        var module = new OpenClawModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-noop",
            StepType = "assign",
            Input = "ignored",
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenCommandSucceeds_ShouldPublishSuccess()
    {
        var module = new OpenClawModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-ok",
            StepType = "openclaw_call",
            Parameters =
            {
                ["cli"] = "dotnet",
                ["args"] = "--version",
                ["timeout_ms"] = "20000",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().NotBeNullOrWhiteSpace();
        completed.Metadata["openclaw.cli"].Should().Be("dotnet");
        completed.Metadata["openclaw.args"].Should().Contain("--version");
    }

    [Fact]
    public async Task HandleAsync_WhenCommandFailsAndContinue_ShouldKeepInput()
    {
        var module = new OpenClawModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-continue",
            StepType = "openclaw_call",
            Input = "fallback-input",
            Parameters =
            {
                ["cli"] = "dotnet",
                ["args"] = "__this_command_does_not_exist__",
                ["on_error"] = "continue",
                ["timeout_ms"] = "20000",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("fallback-input");
        completed.Metadata["openclaw.continued_on_error"].Should().Be("true");
        completed.Metadata.Should().ContainKey("openclaw.error");
    }

    [Fact]
    public async Task HandleAsync_WhenSaveMediaToConfigured_ShouldCopyArtifactAndReturnSavedPath()
    {
        if (!CommandExists("python3"))
            return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "aevatar-openclaw-source-" + Guid.NewGuid().ToString("N"));
        var saveDir = Path.Combine(Path.GetTempPath(), "aevatar-openclaw-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        try
        {
            var sourcePath = Path.Combine(sourceDir, "source.jpg");
            await File.WriteAllTextAsync(sourcePath, "fake-image-data");

            var args = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                "-c",
                "import json,sys; print(json.dumps({'path': sys.argv[1]}))",
                sourcePath,
            });

            var module = new OpenClawModule();
            var ctx = CreateContext();
            var request = new StepRequestEvent
            {
                StepId = "s-save",
                StepType = "openclaw_call",
                Parameters =
                {
                    ["cli"] = "python3",
                    ["args"] = args,
                    ["save_media_to"] = saveDir,
                    ["timeout_ms"] = "20000",
                },
            };

            await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

            var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
            completed.Success.Should().BeTrue();
            completed.Output.Should().NotBeNullOrWhiteSpace();
            File.Exists(completed.Output).Should().BeTrue();
            completed.Metadata["openclaw.media_source_path"].Should().Be(sourcePath);
            completed.Metadata["openclaw.saved_path"].Should().Be(completed.Output);
        }
        finally
        {
            if (Directory.Exists(sourceDir))
                Directory.Delete(sourceDir, recursive: true);
            if (Directory.Exists(saveDir))
                Directory.Delete(saveDir, recursive: true);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenBrowserProfileMissing_ShouldAutoCreateAndRetry()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "aevatar-openclaw-mock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "mock-openclaw.sh");
        var markerPath = Path.Combine(tempDir, "profile-created.txt");
        var previousMarkerPath = Environment.GetEnvironmentVariable("MOCK_PROFILE_MARKER");
        Environment.SetEnvironmentVariable("MOCK_PROFILE_MARKER", markerPath);

        try
        {
            await File.WriteAllTextAsync(
                scriptPath,
                """
                #!/bin/sh
                set -eu
                marker="${MOCK_PROFILE_MARKER:-}"
                if [ -z "$marker" ]; then
                  echo "missing marker env" >&2
                  exit 98
                fi

                if [ "$1" = "browser" ] && [ "$2" = "create-profile" ]; then
                  name=""
                  shift 2
                  while [ "$#" -gt 0 ]; do
                    if [ "$1" = "--name" ] && [ "$#" -ge 2 ]; then
                      name="$2"
                      shift 2
                      continue
                    fi

                    case "$1" in
                      --name=*)
                        name="${1#--name=}"
                        ;;
                    esac
                    shift
                  done

                  if [ -z "$name" ]; then
                    echo "name required" >&2
                    exit 3
                  fi

                  printf "%s" "$name" > "$marker"
                  echo "{\"name\":\"$name\"}"
                  exit 0
                fi

                if [ "$1" = "browser" ] && [ "$2" = "start" ]; then
                  profile=""
                  shift 2
                  while [ "$#" -gt 0 ]; do
                    if [ "$1" = "--browser-profile" ] && [ "$#" -ge 2 ]; then
                      profile="$2"
                      shift 2
                      continue
                    fi

                    case "$1" in
                      --browser-profile=*)
                        profile="${1#--browser-profile=}"
                        ;;
                    esac
                    shift
                  done

                  if [ -f "$marker" ] && [ "$(cat "$marker")" = "$profile" ]; then
                    echo "started:$profile"
                    exit 0
                  fi

                  echo "Error: Error: Error: Profile \"$profile\" not found. Available profiles: auto, openclaw" >&2
                  exit 2
                fi

                echo "unsupported args: $*" >&2
                exit 9
                """);
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var module = new OpenClawModule();
            var ctx = CreateContext();
            var request = new StepRequestEvent
            {
                StepId = "s-profile-recovery",
                StepType = "openclaw_call",
                Parameters =
                {
                    ["cli"] = scriptPath,
                    ["args"] = "browser start --browser-profile session-missing",
                    ["timeout_ms"] = "20000",
                },
            };

            await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

            var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
            completed.Success.Should().BeTrue();
            completed.Output.Should().Contain("started:session-missing");
            completed.Metadata["openclaw.profile_recovery_attempted"].Should().Be("true");
            completed.Metadata["openclaw.profile_recovery_succeeded"].Should().Be("true");
            completed.Metadata["openclaw.browser_profile"].Should().Be("session-missing");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOCK_PROFILE_MARKER", previousMarkerPath);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenBrowserOpenUsesLegacyArgs_ShouldAutoRewriteForCompatibility()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "aevatar-openclaw-open-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "mock-openclaw-open.sh");

        try
        {
            await File.WriteAllTextAsync(
                scriptPath,
                """
                #!/bin/sh
                set -eu
                if [ "$1" != "browser" ]; then
                  echo "unsupported args: $*" >&2
                  exit 9
                fi

                shift
                profile=""
                json="false"
                while [ "$#" -gt 0 ]; do
                  case "$1" in
                    --browser-profile)
                      if [ "$#" -lt 2 ]; then
                        echo "browser-profile value missing" >&2
                        exit 8
                      fi
                      profile="$2"
                      shift 2
                      continue
                      ;;
                    --browser-profile=*)
                      profile="${1#--browser-profile=}"
                      shift
                      continue
                      ;;
                    --json)
                      json="true"
                      shift
                      continue
                      ;;
                    open)
                      break
                      ;;
                    *)
                      echo "unsupported args: browser $*" >&2
                      exit 9
                      ;;
                  esac
                done

                if [ "${1:-}" != "open" ]; then
                  echo "missing open subcommand" >&2
                  exit 7
                fi

                shift
                if [ "$#" -ne 1 ]; then
                  echo "error: too many arguments for 'open'. Expected 1 argument but got $#." >&2
                  exit 1
                fi

                echo "opened:$1:profile=$profile:json=$json"
                exit 0
                """);
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var module = new OpenClawModule();
            var ctx = CreateContext();
            var request = new StepRequestEvent
            {
                StepId = "s-open-compat",
                StepType = "openclaw_call",
                Parameters =
                {
                    ["cli"] = scriptPath,
                    ["args"] = "browser open --browser-profile legacy-profile https://example.com --json",
                    ["timeout_ms"] = "20000",
                },
            };

            await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

            var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
            completed.Success.Should().BeTrue();
            completed.Output.Should().Contain("opened:https://example.com");
            completed.Output.Should().Contain("profile=legacy-profile");
            completed.Metadata["openclaw.args"].Should().Be("browser --browser-profile legacy-profile --json open https://example.com");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var pathExt = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate))
                    return true;
            }
        }

        return false;
    }

    private static RecordingEventHandlerContext CreateContext()
    {
        return new RecordingEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new StubAgent("openclaw-module-test-agent"),
            NullLogger.Instance);
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
