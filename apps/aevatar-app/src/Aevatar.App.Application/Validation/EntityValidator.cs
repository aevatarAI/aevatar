using Aevatar.App.Application.Contracts;
using FluentValidation;

namespace Aevatar.App.Application.Validation;

public sealed class EntityValidator : AbstractValidator<EntityDto>
{
    private static readonly HashSet<string> ValidSources = ["ai", "bank", "user", "edited"];

    public EntityValidator()
    {
        RuleFor(entity => entity.ClientId)
            .NotEmpty()
            .WithMessage("clientId must be a non-empty string");

        RuleFor(entity => entity.EntityType)
            .NotEmpty()
            .WithMessage("entityType must be a non-empty string");

        RuleFor(entity => entity.Revision)
            .GreaterThanOrEqualTo(0)
            .WithMessage("revision must be a non-negative integer");

        RuleFor(entity => entity.Refs)
            .NotNull()
            .WithMessage("refs must be an object");

        RuleFor(entity => entity.Source)
            .Must(source => source is null || ValidSources.Contains(source))
            .WithMessage("source must be ai|bank|user|edited");

        RuleFor(entity => entity.DeletedAt)
            .Must(deletedAt => deletedAt is null || deletedAt.Length > 0)
            .WithMessage("deletedAt must be null or non-empty string");

        RuleFor(entity => entity.CreatedAt)
            .NotEmpty()
            .WithMessage("createdAt must be a string");

        RuleFor(entity => entity.UpdatedAt)
            .NotEmpty()
            .WithMessage("updatedAt must be a string");
    }
}
