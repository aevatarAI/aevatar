using Aevatar.App.Application.Contracts;
using FluentValidation;

namespace Aevatar.App.Application.Validation;

public sealed class SyncRequestValidator : AbstractValidator<SyncRequestDto>
{
    public SyncRequestValidator(IValidator<EntityDto> entityValidator)
    {
        RuleFor(request => request.SyncId)
            .NotEmpty()
            .WithMessage("syncId required");

        RuleFor(request => request.ClientRevision)
            .GreaterThanOrEqualTo(0)
            .WithMessage("clientRevision must be a non-negative number");

        RuleFor(request => request.Entities)
            .NotNull()
            .WithMessage("entities must be an object");

        RuleFor(request => request)
            .Custom((request, context) =>
            {
                if (request.Entities is null)
                    return;

                var totalEntities = 0;
                foreach (var (entityType, entitiesOfType) in request.Entities)
                {
                    if (entitiesOfType is null)
                    {
                        context.AddFailure($"entities.{entityType} must be an object");
                        continue;
                    }

                    foreach (var (_, entity) in entitiesOfType)
                    {
                        if (entity is null)
                        {
                            context.AddFailure("Entity must be an object");
                            continue;
                        }

                        var entityResult = entityValidator.Validate(entity);
                        foreach (var error in entityResult.Errors)
                        {
                            var id = entity.ClientId ?? "unknown";
                            context.AddFailure($"{id}: {error.ErrorMessage}");
                        }

                        totalEntities++;
                    }
                }

                if (totalEntities > 500)
                    context.AddFailure("Too many entities per sync (max 500)");
            });
    }
}
