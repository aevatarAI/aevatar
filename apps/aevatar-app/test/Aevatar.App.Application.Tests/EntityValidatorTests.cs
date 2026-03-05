using FluentAssertions;
using Aevatar.App.Application.Contracts;
using Aevatar.App.Application.Validation;

namespace Aevatar.App.Application.Tests;

public sealed class EntityValidatorTests
{
    private readonly EntityValidator _validator = new();

    [Fact]
    public void Validate_ValidEntity_Passes()
    {
        var entity = BuildValidEntity();
        _validator.Validate(entity).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_Null_Fails()
    {
        var action = () => _validator.Validate((EntityDto)null!);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_EmptyClientId_Fails()
    {
        var entity = BuildValidEntity();
        entity = new EntityDto { ClientId = "", EntityType = entity.EntityType, Revision = entity.Revision, Source = entity.Source, CreatedAt = entity.CreatedAt, UpdatedAt = entity.UpdatedAt };
        var result = _validator.Validate(entity);
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("clientId"));
    }

    [Fact]
    public void Validate_EmptyEntityType_Fails()
    {
        var result = _validator.Validate(new EntityDto
        {
            ClientId = "m_1", EntityType = "", Revision = 0,
            Source = "user", CreatedAt = "2024-01-01T00:00:00Z", UpdatedAt = "2024-01-01T00:00:00Z"
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("entityType"));
    }

    [Fact]
    public void Validate_NegativeRevision_Fails()
    {
        var result = _validator.Validate(new EntityDto
        {
            ClientId = "m_1", EntityType = "manifestation", Revision = -1,
            Source = "user", CreatedAt = "2024-01-01T00:00:00Z", UpdatedAt = "2024-01-01T00:00:00Z"
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("revision"));
    }

    [Fact]
    public void Validate_InvalidSource_Fails()
    {
        var result = _validator.Validate(new EntityDto
        {
            ClientId = "m_1", EntityType = "manifestation", Revision = 0,
            Source = "invalid", CreatedAt = "2024-01-01T00:00:00Z", UpdatedAt = "2024-01-01T00:00:00Z"
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("source"));
    }

    [Fact]
    public void Validate_ValidSources_Pass()
    {
        foreach (var source in new[] { "ai", "bank", "user", "edited" })
        {
            var result = _validator.Validate(new EntityDto
            {
                ClientId = "m_1", EntityType = "manifestation", Revision = 0,
                Source = source, CreatedAt = "2024-01-01T00:00:00Z", UpdatedAt = "2024-01-01T00:00:00Z"
            });
            result.IsValid.Should().BeTrue($"source '{source}' should be valid");
        }
    }

    [Fact]
    public void Validate_NullSource_Passes()
    {
        var result = _validator.Validate(new EntityDto
        {
            ClientId = "m_1", EntityType = "manifestation", Revision = 0,
            Source = null, CreatedAt = "2024-01-01T00:00:00Z", UpdatedAt = "2024-01-01T00:00:00Z"
        });
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_MissingCreatedAt_Fails()
    {
        var result = _validator.Validate(new EntityDto
        {
            ClientId = "m_1", EntityType = "manifestation", Revision = 0,
            Source = "user", CreatedAt = null, UpdatedAt = "2024-01-01T00:00:00Z"
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("createdAt"));
    }

    [Fact]
    public void Validate_MissingUpdatedAt_Fails()
    {
        var result = _validator.Validate(new EntityDto
        {
            ClientId = "m_1", EntityType = "manifestation", Revision = 0,
            Source = "user", CreatedAt = "2024-01-01T00:00:00Z", UpdatedAt = null
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("updatedAt"));
    }

    private static EntityDto BuildValidEntity() => new()
    {
        ClientId = "m_abc123",
        EntityType = "manifestation",
        Revision = 0,
        Source = "user",
        BankEligible = false,
        CreatedAt = "2024-01-01T00:00:00Z",
        UpdatedAt = "2024-01-01T00:00:00Z"
    };
}
