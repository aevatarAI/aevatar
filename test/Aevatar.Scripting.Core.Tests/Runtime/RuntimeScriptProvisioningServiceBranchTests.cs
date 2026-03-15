using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class RuntimeScriptProvisioningServiceBranchTests
{
    [Fact]
    public void Constructor_ShouldThrow_ForNullDispatchService()
    {
        Action nullDispatch = () => _ = new RuntimeScriptProvisioningService(null!);

        nullDispatch.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("dispatchService");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenDefinitionSnapshotIsMissing()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => throw new InvalidOperationException("dispatch should not run")));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "definitionSnapshot");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenRevisionDoesNotMatchSnapshot()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => throw new InvalidOperationException("dispatch should not run")));

        var act = () => service.EnsureRuntimeAsync(
            "definition-1",
            "rev-requested",
            null,
            CreateDefinitionSnapshot("rev-actual"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime provisioning requires a definition snapshot for revision `rev-requested`, but received `rev-actual`.");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenDispatchFailsWithTypedError()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(
                CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Failure(
                    ScriptingCommandStartError.InvalidArgument("definitionActorId", "definition id is required")))));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CreateDefinitionSnapshot(), CancellationToken.None);

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
            })));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CreateDefinitionSnapshot(), CancellationToken.None);

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
            })));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CreateDefinitionSnapshot(), CancellationToken.None);

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
            }));

        var actorId = await service.EnsureRuntimeAsync("definition-1", "rev-1", "runtime-1", CreateDefinitionSnapshot(), CancellationToken.None);

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
            }));

        var actorId = await service.EnsureRuntimeAsync(
            "definition-1",
            "rev-provided",
            "runtime-provided",
            providedSnapshot,
            CancellationToken.None);

        actorId.Should().Be("runtime-provided");
        capturedCommand.Should().NotBeNull();
        capturedCommand!.DefinitionSnapshot.Should().BeEquivalentTo(providedSnapshot);
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldUseSnapshotRevision_WhenRequestedRevisionIsEmpty()
    {
        ProvisionScriptRuntimeCommand? capturedCommand = null;
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(command =>
            {
                capturedCommand = command;
                return Task.FromResult(CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                    new ScriptingCommandAcceptedReceipt("runtime-1", "command-1", "corr-1")));
            }));

        var actorId = await service.EnsureRuntimeAsync(
            "definition-1",
            string.Empty,
            null,
            CreateDefinitionSnapshot("rev-2"),
            CancellationToken.None);

        actorId.Should().Be("runtime-1");
        capturedCommand.Should().NotBeNull();
        capturedCommand!.ScriptRevision.Should().Be("rev-2");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldPropagateCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => throw new InvalidOperationException("dispatch should not run")));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CreateDefinitionSnapshot(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ScriptDefinitionSnapshot CreateDefinitionSnapshot(string revision = "rev-1") =>
        new(
            "script-1",
            revision,
            "public sealed class Behavior {}",
            "hash-1",
            "type.googleapis.com/example.State",
            "type.googleapis.com/example.ReadModel",
            "2",
            "schema-hash-1");

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
}
