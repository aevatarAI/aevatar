using FluentAssertions;
using Aevatar.App.Application.Contracts;
using Aevatar.App.Application.Validation;

namespace Aevatar.App.Application.Tests;

public sealed class SyncRequestValidatorTests
{
    private readonly SyncRequestValidator _validator = new(new EntityValidator());

    [Fact]
    public void Validate_ValidRequest_Passes()
    {
        var request = BuildValidRequest();
        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_Null_Fails()
    {
        var action = () => _validator.Validate((SyncRequestDto)null!);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_MissingSyncId_Fails()
    {
        var request = new SyncRequestDto { SyncId = "", ClientRevision = 0, Entities = new() };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("syncId"));
    }

    [Fact]
    public void Validate_NegativeClientRevision_Fails()
    {
        var request = new SyncRequestDto { SyncId = "sync-1", ClientRevision = -1, Entities = new() };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("clientRevision"));
    }

    [Fact]
    public void Validate_EntityMissingClientId_Fails()
    {
        var request = new SyncRequestDto
        {
            SyncId = "sync-1",
            ClientRevision = 0,
            Entities = new Dictionary<string, Dictionary<string, EntityDto>>
            {
                ["manifestation"] = new()
                {
                    ["m_1"] = new EntityDto
                    {
                        ClientId = "",
                        EntityType = "manifestation",
                        Revision = 0,
                        CreatedAt = "2024-01-01T00:00:00Z",
                        UpdatedAt = "2024-01-01T00:00:00Z"
                    }
                }
            }
        };

        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("clientId"));
    }

    [Fact]
    public void Validate_TooManyEntities_FailsAtLimit()
    {
        var typeMap = new Dictionary<string, EntityDto>();
        for (var i = 0; i < 501; i++)
        {
            typeMap[$"m_{i}"] = new EntityDto
            {
                ClientId = $"m_{i}",
                EntityType = "manifestation",
                Revision = 0,
                Source = "user",
                CreatedAt = "2024-01-01T00:00:00Z",
                UpdatedAt = "2024-01-01T00:00:00Z"
            };
        }

        var request = new SyncRequestDto
        {
            SyncId = "sync-1",
            ClientRevision = 0,
            Entities = new() { ["manifestation"] = typeMap }
        };

        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Select(x => x.ErrorMessage).Should().Contain(x => x.Contains("500"));
    }

    [Fact]
    public void Validate_EmptyEntities_Passes()
    {
        var request = new SyncRequestDto
        {
            SyncId = "sync-1",
            ClientRevision = 0,
            Entities = new()
        };

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    private static SyncRequestDto BuildValidRequest() => new()
    {
        SyncId = "sync-1",
        ClientRevision = 0,
        Entities = new Dictionary<string, Dictionary<string, EntityDto>>
        {
            ["manifestation"] = new()
            {
                ["m_1"] = new EntityDto
                {
                    ClientId = "m_1",
                    EntityType = "manifestation",
                    Revision = 0,
                    Source = "user",
                    CreatedAt = "2024-01-01T00:00:00Z",
                    UpdatedAt = "2024-01-01T00:00:00Z"
                }
            }
        }
    };
}
