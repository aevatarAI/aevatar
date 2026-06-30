using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.Mainnet.Host.Api.Skills;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class WorkflowSkillsCatalogMapperTests
{
    [Fact]
    public void ToResult_ShouldMapOrnnSummaries_IncludingCategoryAndTags()
    {
        var search = new OrnnSearchResult
        {
            Total = 2,
            Page = 1,
            PageSize = 20,
            Items =
            [
                new OrnnSkillSummary
                {
                    Guid = "g1",
                    Name = "daily-brief",
                    Description = "writes a daily brief",
                    IsPrivate = false,
                    Tags = ["news", "summary"],
                    Metadata = new OrnnSkillMetadata { Category = "productivity" },
                },
                new OrnnSkillSummary
                {
                    Guid = "g2",
                    Name = "secret",
                    Description = "private skill",
                    IsPrivate = true,
                },
            ],
        };

        var result = UserSkillCatalogMapper.ToResult(search);

        result.Error.Should().BeNull();
        result.Total.Should().Be(2);
        result.Items.Should().HaveCount(2);

        var first = result.Items[0];
        first.Guid.Should().Be("g1");
        first.Name.Should().Be("daily-brief");
        first.Category.Should().Be("productivity");
        first.Tags.Should().BeEquivalentTo("news", "summary");
        first.Private.Should().BeFalse();

        result.Items[1].Private.Should().BeTrue();
        result.Items[1].Category.Should().BeEmpty();
        result.Items[1].Tags.Should().BeEmpty();
    }

    [Fact]
    public void ToResult_ShouldSurfaceError_WhenOrnnReturnedError()
    {
        var search = new OrnnSearchResult { Items = [], Error = "upstream down" };

        var result = UserSkillCatalogMapper.ToResult(search);

        result.Error.Should().Be("upstream down");
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public void ToResult_ShouldFallBackToMetadataTags_WhenTopLevelTagsNull()
    {
        var search = new OrnnSearchResult
        {
            Items =
            [
                new OrnnSkillSummary
                {
                    Guid = "g3",
                    Name = "x",
                    Metadata = new OrnnSkillMetadata { Tags = ["from-meta"] },
                },
            ],
        };

        var result = UserSkillCatalogMapper.ToResult(search);

        result.Items[0].Tags.Should().BeEquivalentTo("from-meta");
    }
}
