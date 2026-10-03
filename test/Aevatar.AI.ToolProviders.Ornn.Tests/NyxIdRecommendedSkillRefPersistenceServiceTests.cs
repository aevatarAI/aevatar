using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class NyxIdRecommendedSkillRefPersistenceServiceTests
{
    private const string CatalogId = "aaaaaaaa-1111-1111-1111-111111111111";

    [Fact]
    public async Task Persist_ShouldUseObservedCatalogRevisionAndPreserveOtherPins()
    {
        var handler = new CatalogHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient(handler));
        var service = new NyxIdRecommendedSkillRefPersistenceService(client);

        var result = await service.PersistRecommendedSkillRefsAsync("publisher", new NyxIdServiceInstance
        {
            UserServiceId = "instance-different-from-catalog",
            CatalogServiceId = CatalogId,
        }, [new NyxIdRecommendedSkillRef
        {
            Source = NyxIdRecommendedSkillSource.Ornn,
            SkillId = "cccccccc-3333-3333-3333-333333333333",
            LiteralVersion = "1.0",
            DisplayName = "new-recommendation",
            ManifestDigest = new string('c', 64),
        }], CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var write = handler.Writes.Should().ContainSingle().Subject;
        write.Path.Should().Be($"/api/v1/catalog-curation/services/{CatalogId}/skills");
        using var document = JsonDocument.Parse(write.Body);
        document.RootElement.GetProperty("base_revision").GetInt64().Should().Be(7);
        Guid.TryParse(document.RootElement.GetProperty("request_id").GetString(), out _).Should().BeTrue();
        var references = document.RootElement.GetProperty("recommended_skill_refs");
        references.GetArrayLength().Should().Be(2);
        references[0].GetProperty("dependencies")[0].GetProperty("name").GetString().Should().Be("retained-dependency");
        handler.CurationReads.Should().Be(2);
    }

    [Fact]
    public async Task Persist_CachedCreationMustNotDowngradeAnAdministratorUpdatedReference()
    {
        var handler = new CatalogHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient(handler));
        var service = new NyxIdRecommendedSkillRefPersistenceService(client);

        var result = await service.PersistRecommendedSkillRefsAsync("publisher", new NyxIdServiceInstance
        {
            UserServiceId = "instance-alpha", CatalogServiceId = CatalogId,
        }, [new NyxIdRecommendedSkillRef
        {
            Source = NyxIdRecommendedSkillSource.Ornn, SkillId = "bbbbbbbb-2222-2222-2222-222222222222",
            LiteralVersion = "1.0", RecommendationName = "retained", ManifestDigest = new string('a', 64),
        }], CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("recommendation_version_conflict");
        handler.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Persist_ShouldNotReportSuccessWhenReadbackDidNotChange()
    {
        var handler = new CatalogHandler { IgnoreWrite = true };
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient(handler));
        var service = new NyxIdRecommendedSkillRefPersistenceService(client);
        var result = await service.PersistRecommendedSkillRefsAsync("publisher", new NyxIdServiceInstance
        {
            UserServiceId = "instance-alpha",
            CatalogServiceId = CatalogId,
        }, [new NyxIdRecommendedSkillRef
        {
            Source = NyxIdRecommendedSkillSource.Ornn,
            SkillId = "cccccccc-3333-3333-3333-333333333333",
            LiteralVersion = "1.0",
            DisplayName = "new-recommendation",
            ManifestDigest = new string('c', 64),
        }], CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Contain("verification");
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        private string _refs = """
            [{"source":"ornn","skill_id":"bbbbbbbb-2222-2222-2222-222222222222","name":"retained","version":"2.0","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","dependencies":[{"source":"ornn","skill_id":"dddddddd-4444-4444-4444-444444444444","name":"retained-dependency","version":"1.4","sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]}]
            """;
        private long _revision = 7;
        public bool IgnoreWrite { get; init; }
        public List<(string Path, string Body)> Writes { get; } = [];
        public int CurationReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Writes.Add((request.RequestUri!.AbsolutePath, body));
                if (!IgnoreWrite)
                {
                    using var document = JsonDocument.Parse(body);
                    _refs = document.RootElement.GetProperty("recommended_skill_refs").GetRawText();
                    _revision++;
                }
            }
            else if (request.RequestUri!.AbsolutePath.Contains("catalog-curation", StringComparison.Ordinal))
            {
                CurationReads++;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"service_id\":\"{CatalogId}\",\"skills_revision\":{_revision},\"recommended_skill_refs\":{_refs},\"skills_manifest_digest\":\"v1:{new string('f', 64)}\"}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
