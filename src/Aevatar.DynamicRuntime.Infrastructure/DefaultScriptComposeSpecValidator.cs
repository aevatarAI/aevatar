using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptComposeSpecValidator : IScriptComposeSpecValidator
{
    public Task ValidateAsync(ApplyComposeRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.StackId))
            throw new InvalidOperationException("COMPOSE_SPEC_INVALID: stack_id is required.");

        if (string.IsNullOrWhiteSpace(request.ComposeSpecDigest))
            throw new InvalidOperationException("COMPOSE_SPEC_INVALID: compose_spec_digest is required.");

        if (request.Services.Count == 0)
            throw new InvalidOperationException("COMPOSE_SPEC_INVALID: at least one service is required.");

        foreach (var service in request.Services)
        {
            if (string.IsNullOrWhiteSpace(service.ServiceName))
                throw new InvalidOperationException("COMPOSE_SPEC_INVALID: service_name is required.");
            if (service.ReplicasDesired < 0)
                throw new InvalidOperationException("COMPOSE_SPEC_INVALID: replicas_desired must be >= 0.");
        }

        return Task.CompletedTask;
    }
}
