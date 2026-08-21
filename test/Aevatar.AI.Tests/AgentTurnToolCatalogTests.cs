using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentTurnToolCatalogTests
{
    [Fact]
    public void CatalogDigest_ShouldBeStableAcrossOneHundredInputPermutations()
    {
        IAgentTool[] tools =
        [
            new TestTool("zeta", "third", "{\"type\":\"object\",\"properties\":{\"z\":{\"type\":\"string\"}}}"),
            new TestTool("alpha", "first", "{\"required\":[\"a\"],\"properties\":{\"a\":{\"type\":\"integer\"}},\"type\":\"object\"}"),
            new TestTool("middle", "second", "{\"type\":\"object\"}"),
        ];
        var random = new Random(3512);
        var digests = new HashSet<string>(StringComparer.Ordinal);

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var permutation = tools.OrderBy(_ => random.Next()).ToArray();
            var catalog = NewCatalog(permutation, AgentTurnToolCatalogBudget.Ordinary);

            digests.Add(catalog.Proof.CatalogDigest);
            catalog.Proof.ToolDescriptors.Select(static descriptor => descriptor.Name)
                .Should().ContainInOrder("alpha", "middle", "zeta");
        }

        digests.Should().ContainSingle();
    }

    [Fact]
    public void CatalogDigest_ShouldCanonicalizeSchemaObjectPropertyOrder()
    {
        var first = NewCatalog(
            [new TestTool("search", "Search", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}")],
            AgentTurnToolCatalogBudget.Ordinary);
        var second = NewCatalog(
            [new TestTool("SEARCH", "Search", "{\"required\":[\"query\"],\"properties\":{\"query\":{\"type\":\"string\"}},\"type\":\"object\"}")],
            AgentTurnToolCatalogBudget.Ordinary);

        first.Proof.CatalogDigest.Should().Be(second.Proof.CatalogDigest);
        first.Proof.ToolDescriptors[0].CanonicalSchemaJson
            .Should().Be(second.Proof.ToolDescriptors[0].CanonicalSchemaJson);
    }

    [Fact]
    public void Catalog_ShouldRejectInvalidSchema()
    {
        var act = () => NewCatalog(
            [new TestTool("broken", "Broken", "[]")],
            AgentTurnToolCatalogBudget.Ordinary);

        act.Should().Throw<AgentTurnToolCatalogException>()
            .Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.SchemaInvalid);
    }

    [Fact]
    public void Catalog_ShouldRejectToolCountOverBudgetWithoutTruncating()
    {
        var tools = Enumerable.Range(0, 9)
            .Select(index => (IAgentTool)new TestTool($"tool-{index}", "Tool", "{}"))
            .ToArray();

        var act = () => NewCatalog(tools, AgentTurnToolCatalogBudget.Ordinary);

        act.Should().Throw<AgentTurnToolCatalogException>()
            .Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.CatalogOverBudget);
    }

    [Fact]
    public void ConnectedCatalog_ShouldRejectMoreThanThreeReadTools()
    {
        var tools = Enumerable.Range(0, 4)
            .Select(index => (IAgentTool)new TestTool($"read-{index}", "Read", "{}", isReadOnly: true))
            .ToArray();

        var act = () => NewCatalog(
            tools,
            AgentTurnToolCatalogBudget.ConnectedOperations,
            AgentTurnToolOrigin.ConnectedService);

        act.Should().Throw<AgentTurnToolCatalogException>()
            .Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.CatalogOverBudget);
    }

    [Fact]
    public void ConnectedCatalog_ShouldRejectMoreThanOneWriteTool()
    {
        IAgentTool[] tools =
        [
            new TestTool("write-a", "Write", "{}"),
            new TestTool("write-b", "Write", "{}"),
        ];

        var act = () => NewCatalog(
            tools,
            AgentTurnToolCatalogBudget.ConnectedOperations,
            AgentTurnToolOrigin.ConnectedService);

        act.Should().Throw<AgentTurnToolCatalogException>()
            .Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.CatalogOverBudget);
    }

    [Fact]
    public void Proof_ShouldRejectAReplacementExactObjectEvenWhenItsContractMatches()
    {
        var original = new TestTool("lookup", "Lookup", "{}");
        var replacement = new TestTool("LOOKUP", "Lookup", "{}");
        var catalog = NewCatalog([original], AgentTurnToolCatalogBudget.Ordinary);

        var act = () => catalog.Proof.AssertMatchesExactTools([replacement]);

        act.Should().Throw<AgentTurnToolCatalogException>()
            .Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.CatalogProofMismatch);
    }

    [Fact]
    public void CatalogDigest_ShouldChangeForEveryProofIdentityField()
    {
        var baselineTool = new TestTool("lookup", "Lookup", "{\"type\":\"object\"}");
        var baseline = NewCatalog([baselineTool], AgentTurnToolCatalogBudget.Ordinary);
        var changedDescription = NewCatalog(
            [new TestTool("lookup", "Lookup exactly", "{\"type\":\"object\"}")],
            AgentTurnToolCatalogBudget.Ordinary);
        var changedSchema = NewCatalog(
            [new TestTool("lookup", "Lookup", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}")],
            AgentTurnToolCatalogBudget.Ordinary);
        var changedOrigin = NewCatalog(
            [new TestTool("lookup", "Lookup", "{\"type\":\"object\"}")],
            AgentTurnToolCatalogBudget.Ordinary,
            AgentTurnToolOrigin.AgentProfile);
        var changedSelector = new AgentTurnToolCatalog(
            ["lookup"],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics: null,
            exactToolSelections:
            [
                new AgentTurnToolSelection(
                    new TestTool("lookup", "Lookup", "{\"type\":\"object\"}"),
                    AgentTurnToolOrigin.RouteToolSet,
                    "sha256:selector"),
            ],
            hasUnresolvedConnectedServiceSelectors: false,
            requiredToolInvocation: null,
            budget: AgentTurnToolCatalogBudget.Ordinary);

        var digests = new[]
        {
            baseline.Proof.CatalogDigest,
            changedDescription.Proof.CatalogDigest,
            changedSchema.Proof.CatalogDigest,
            changedOrigin.Proof.CatalogDigest,
            changedSelector.Proof.CatalogDigest,
        };
        digests.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void RestrictedEmptyProof_ShouldBeExplicitAndWithinBudget()
    {
        var proof = AgentTurnToolCatalogProof.RestrictedEmpty(AgentTurnToolCatalogBudget.Voice);

        proof.ToolCount.Should().Be(0);
        proof.SchemaBytes.Should().Be(0);
        proof.ToolDescriptors.Should().BeEmpty();
        proof.Budget.Should().BeSameAs(AgentTurnToolCatalogBudget.Voice);
        proof.AssertMatchesExactTools([]);
    }

    private static AgentTurnToolCatalog NewCatalog(
        IReadOnlyCollection<IAgentTool> tools,
        AgentTurnToolCatalogBudget budget,
        AgentTurnToolOrigin origin = AgentTurnToolOrigin.RouteToolSet) =>
        new(
            tools.Select(static tool => tool.Name),
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics: null,
            exactTools: tools,
            budget: budget,
            exactToolOrigin: origin);

    private sealed class TestTool(
        string name,
        string description,
        string schema,
        bool isReadOnly = false) : IAgentTool
    {
        public string Name => name;

        public string Description => description;

        public string ParametersSchema => schema;

        public bool IsReadOnly => isReadOnly;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
