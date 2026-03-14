using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class RuntimeScriptProvisioningServiceBranchTests
{
    [Fact]
    public void Constructor_ShouldThrow_ForNullDependencies()
    {
        Action nullDispatch = () => _ = new RuntimeScriptProvisioningService(null!, CreateDefinitionSnapshotPort());
        Action nullSnapshotPort = () => _ = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(
                CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                    new ScriptingCommandAcceptedReceipt("runtime-1", "command-1", "corr-1")))),
            null!);

        nullDispatch.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("dispatchService");
        nullSnapshotPort.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("definitionSnapshotPort");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenDefinitionSnapshotIsMissing()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => throw new InvalidOperationException("dispatch should not run")),
            new StaticDefinitionSnapshotPort((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("missing snapshot");
            }));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("Timed out waiting for script definition snapshot observation.*");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenDispatchFailsWithTypedError()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(
                CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Failure(
                    ScriptingCommandStartError.InvalidArgument("definitionActorId", "definition id is required")))),
            CreateDefinitionSnapshotPort());

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("definition id is required*");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenDispatchFailsWithoutTypedError()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(new CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
            {
                Succeeded = false,
                Error = null!,
                Receipt = null,
            })),
            CreateDefinitionSnapshotPort());

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime provisioning dispatch failed.");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenReceiptIsMissing()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(new CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
            {
                Succeeded = true,
                Error = null!,
                Receipt = null,
            })),
            CreateDefinitionSnapshotPort());

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime provisioning did not produce a receipt.");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldReturnReceiptActorId_WhenDispatchSucceeds()
    {
        ProvisionScriptRuntimeCommand? capturedCommand = null;
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(command =>
            {
                capturedCommand = command;
                return Task.FromResult(CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                    new ScriptingCommandAcceptedReceipt("runtime-1", "command-1", "corr-1")));
            }),
            CreateDefinitionSnapshotPort());

        var actorId = await service.EnsureRuntimeAsync("definition-1", "rev-1", "runtime-1", CancellationToken.None);

        actorId.Should().Be("runtime-1");
        capturedCommand.Should().NotBeNull();
        capturedCommand!.DefinitionActorId.Should().Be("definition-1");
        capturedCommand.ScriptRevision.Should().Be("rev-1");
        capturedCommand.RuntimeActorId.Should().Be("runtime-1");
        capturedCommand.DefinitionSnapshot.ScriptId.Should().Be("script-1");
        capturedCommand.DefinitionSnapshot.Revision.Should().Be("rev-1");
        capturedCommand.DefinitionSnapshot.SourceHash.Should().Be("hash-1");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldUseProvidedDefinitionSnapshot_WhenSupplied()
    {
        ProvisionScriptRuntimeCommand? capturedCommand = null;
        var providedSnapshot = new ScriptDefinitionSnapshot(
            "script-provided",
            "rev-provided",
            "public sealed class ProvidedBehavior {}",
            "hash-provided",
            "type.googleapis.com/example.State",
            "type.googleapis.com/example.ReadModel",
            "7",
            "schema-hash-provided");
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(command =>
            {
                capturedCommand = command;
                return Task.FromResult(CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                    new ScriptingCommandAcceptedReceipt("runtime-provided", "command-1", "corr-1")));
            }),
            new StaticDefinitionSnapshotPort((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("snapshot port should not run");
            }));

        var actorId = await service.EnsureRuntimeAsync(
            "definition-1",
            "rev-provided",
            "runtime-provided",
            CancellationToken.None,
            providedSnapshot);

        actorId.Should().Be("runtime-provided");
        capturedCommand.Should().NotBeNull();
        capturedCommand!.DefinitionSnapshot.Should().BeEquivalentTo(providedSnapshot);
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldRetryUntilSnapshotBecomesAvailable()
    {
        var attempts = 0;
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(command =>
            {
                command.DefinitionSnapshot.Revision.Should().Be("rev-1");
                return Task.FromResult(CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                    new ScriptingCommandAcceptedReceipt("runtime-1", "command-1", "corr-1")));
            }),
            new StaticDefinitionSnapshotPort((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                return Task.FromResult(attempts < 3
                    ? null!
                    : new ScriptDefinitionSnapshot(
                        "script-1",
                        "rev-1",
                        "public sealed class Behavior {}",
                        "hash-1",
                        "type.googleapis.com/example.State",
                        "type.googleapis.com/example.ReadModel",
                        "2",
                        "schema-hash-1"));
            }));

        var actorId = await service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        actorId.Should().Be("runtime-1");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldPropagateCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => throw new InvalidOperationException("dispatch should not run")),
            new StaticDefinitionSnapshotPort((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ScriptDefinitionSnapshot>(null!);
            }));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static StaticDefinitionSnapshotPort CreateDefinitionSnapshotPort() =>
        new((definitionActorId, requestedRevision, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            definitionActorId.Should().Be("definition-1");
            return Task.FromResult(new ScriptDefinitionSnapshot(
                "script-1",
                string.IsNullOrWhiteSpace(requestedRevision) ? "rev-latest" : requestedRevision,
                "public sealed class Behavior {}",
                "hash-1",
                "type.googleapis.com/example.State",
                "type.googleapis.com/example.ReadModel",
                "2",
                "schema-hash-1"));
        });

    private sealed class StaticDispatchService(
        Func<ProvisionScriptRuntimeCommand, Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>>> dispatch)
        : ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
    {
        public Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>> DispatchAsync(
            ProvisionScriptRuntimeCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return dispatch(command);
        }
    }

    private sealed class StaticDefinitionSnapshotPort(
        Func<string, string, CancellationToken, Task<ScriptDefinitionSnapshot>> getAsync) : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct) =>
            getAsync(definitionActorId, requestedRevision, ct);
    }
}
