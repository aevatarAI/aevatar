using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptComposeSpecValidator : IScriptComposeSpecValidator
{
    public Task<ComposeSpecValidationResult> ValidateAsync(ComposeApplyYamlRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.StackId))
            return Task.FromResult(new ComposeSpecValidationResult(false, "COMPOSE_SPEC_INVALID", "stack_id is required."));

        if (string.IsNullOrWhiteSpace(request.ComposeSpecDigest))
            return Task.FromResult(new ComposeSpecValidationResult(false, "COMPOSE_SPEC_INVALID", "compose_spec_digest is required."));

        if (string.IsNullOrWhiteSpace(request.ComposeYaml))
            return Task.FromResult(new ComposeSpecValidationResult(false, "COMPOSE_SPEC_INVALID", "compose_yaml is required."));

        if (request.Services.Count == 0)
            return Task.FromResult(new ComposeSpecValidationResult(false, "COMPOSE_SPEC_INVALID", "at least one service is required."));

        foreach (var service in request.Services)
        {
            if (string.IsNullOrWhiteSpace(service.ServiceName))
                return Task.FromResult(new ComposeSpecValidationResult(false, "COMPOSE_SPEC_INVALID", "service_name is required."));
            if (string.IsNullOrWhiteSpace(service.ImageRef))
                return Task.FromResult(new ComposeSpecValidationResult(false, "COMPOSE_SPEC_INVALID", "image_ref is required."));
            if (service.ReplicasDesired < 0)
                return Task.FromResult(new ComposeSpecValidationResult(false, "COMPOSE_SPEC_INVALID", "replicas_desired must be >= 0."));
            if ((service.ServiceMode is DynamicServiceMode.Daemon or DynamicServiceMode.Hybrid) && service.ReplicasDesired < 1)
                return Task.FromResult(new ComposeSpecValidationResult(false, "SERVICE_MODE_CONFLICT", "daemon/hybrid requires replicas_desired >= 1."));
        }

        return Task.FromResult(new ComposeSpecValidationResult(true));
    }
}
