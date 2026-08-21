using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aevatar.AI.Tests;

public sealed class AgentTurnToolCatalogTests
{
    [Fact]
    public void CatalogFailureDegradationReasons_ShouldKeepStableWireValues()
    {
        ((int)AgentProfileTurnDegradationReason.CatalogNeedsDisambiguation).Should().Be(16);
        ((int)AgentProfileTurnDegradationReason.CatalogOverBudget).Should().Be(17);
        ((int)AgentProfileTurnDegradationReason.SchemaInvalid).Should().Be(18);
    }

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
    public void CatalogDigest_ShouldMatchCanonicalSnapshot()
    {
        var catalog = NewCatalog(
            [
                new TestTool(
                    "lookup",
                    "Lookup one resource",
                    "{\"required\":[\"id\"],\"properties\":{\"id\":{\"type\":\"string\"}},\"type\":\"object\"}"),
            ],
            AgentTurnToolCatalogBudget.Ordinary,
            AgentTurnToolOrigin.RouteToolSet);

        catalog.Proof.CatalogDigest.Should().Be(
            "sha256:ac8a952508a88afb07f3ab8cbcfa47688a65e394bff4f6720fbf60eec498e6c0");
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

    [Fact]
    public void PersistedProof_ShouldRoundTripAndValidateRematerializedExactContracts()
    {
        var ingressTool = new TestTool(
            "lookup",
            "Lookup",
            "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}");
        var catalog = NewCatalog([ingressTool], AgentTurnToolCatalogBudget.Ordinary);

        var restored = AgentTurnToolCatalogProofPayloadMapper.FromPayload(catalog.Proof.ToPayload());
        var rematerializedTool = new TestTool(
            "LOOKUP",
            "Lookup",
            "{\"properties\":{\"id\":{\"type\":\"string\"}},\"type\":\"object\"}");

        restored.CatalogDigest.Should().Be(catalog.Proof.CatalogDigest);
        restored.AssertMatchesExactTools([rematerializedTool]);
    }

    [Fact]
    public void PersistedProof_ShouldRejectTamperedOverBudgetSummaryBeforeRematerialization()
    {
        var catalog = NewCatalog(
            [new TestTool("lookup", "Lookup", "{}")],
            AgentTurnToolCatalogBudget.Ordinary);
        var payload = catalog.Proof.ToPayload();
        payload.Budget.MaximumToolCount = 0;

        var act = () => AgentTurnToolCatalogProofPayloadMapper.FromPayload(payload);

        act.Should().Throw<AgentTurnToolCatalogException>()
            .Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.CatalogProofMismatch);
    }

    [Fact]
    public void CatalogTelemetry_ShouldEmitLowCardinalityCountsAndKeepDigestOnTrace()
    {
        var measurements = new ConcurrentDictionary<string, ConcurrentBag<long>>(StringComparer.Ordinal);
        var instruments = new ConcurrentBag<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (!string.Equals(
                    instrument.Meter.Name,
                    AgentTurnToolCatalogTelemetry.MeterName,
                    StringComparison.Ordinal))
            {
                return;
            }

            instruments.Add(instrument.Name);
            listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.GetOrAdd(instrument.Name, static _ => []).Add(measurement));
        meterListener.Start();

        var completedActivities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(
                source.Name,
                AgentTurnToolCatalogTelemetry.ActivitySourceName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = completedActivities.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        var catalog = NewCatalog(
            [new TestTool("lookup", "Lookup", "{\"type\":\"object\"}")],
            AgentTurnToolCatalogBudget.Ordinary);

        measurements[AgentTurnToolCatalogTelemetry.AuthorityCounterName].Should().Contain(1);
        measurements[AgentTurnToolCatalogTelemetry.FinalCounterName].Should().Contain(1);
        measurements[AgentTurnToolCatalogTelemetry.SchemaBytesCounterName]
            .Should().Contain(value => value > 0);
        instruments.Should().NotContain(name => name.Contains("digest", StringComparison.OrdinalIgnoreCase));
        completedActivities
            .Select(activity =>
                activity.GetTagItem("aevatar.agent_turn_tool_catalog.digest")?.ToString())
            .Should()
            .Contain(catalog.Proof.CatalogDigest);
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
