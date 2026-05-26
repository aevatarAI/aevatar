using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: endpoint/bootstrap tests repeated near-identical command dispatch fakes in each file.
//   New principle: refactor helper, no behavior change; shared fakes keep accepted/rejected/throwing dispatch semantics consistent.
internal sealed class RecordingCommandDispatch<TCommand>(
    Func<TCommand, ChannelIdentityOAuthAcceptedReceipt>? receiptFactory = null)
    : ICommandDispatchService<TCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
{
    public List<TCommand> Commands { get; } = new();

    public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        Commands.Add(command);
        var receipt = receiptFactory?.Invoke(command) ??
                      new ChannelIdentityOAuthAcceptedReceipt(
                          ActorId: "actor",
                          CommandId: "cmd-1",
                          CorrelationId: "cmd-1");
        return Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(receipt));
    }
}

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: endpoint/bootstrap tests repeated near-identical command dispatch fakes in each file.
//   New principle: refactor helper, no behavior change; shared fakes keep accepted/rejected/throwing dispatch semantics consistent.
internal sealed class RejectingCommandDispatch<TCommand>
    : ICommandDispatchService<TCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
{
    public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default) =>
        Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Failure(
            ChannelIdentityOAuthDispatchError.InvalidTarget));
}

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: endpoint/bootstrap tests repeated near-identical command dispatch fakes in each file.
//   New principle: refactor helper, no behavior change; shared fakes keep accepted/rejected/throwing dispatch semantics consistent.
internal sealed class ThrowingCommandDispatch<TCommand>
    : ICommandDispatchService<TCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
{
    public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default) =>
        throw new InvalidOperationException("dispatch failed");
}
