using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Hosting.WorkflowDeliveries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ConfigAcceptanceDateProjection = Aevatar.Studio.Application.Delivery.WorkflowDeliveryAcceptanceDateProjection;
using ContractAcceptanceMode = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceMode;
using ContractVariableKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableKind;
using ProtoAcceptanceDateProjection = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceDateProjection;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryPackageCatalogTests
{
    private static readonly WorkflowDeliveryPackageOptions[] PackageDefinitions =
    [
        Package("workflow-alpha"),
        Package("workflow-beta"),
    ];

    public static TheoryData<string> InvalidAcceptanceInputKeys =>
    [
        new string('x', 129),
        "line\nbreak",
    ];

    public static TheoryData<
        WorkflowDeliveryAcceptanceInputValueSource,
        ConfigAcceptanceDateProjection,
        int> InvalidAcceptanceInputBindings =>
    new()
    {
        { WorkflowDeliveryAcceptanceInputValueSource.Unspecified, ConfigAcceptanceDateProjection.UtcDate, 0 },
        { (WorkflowDeliveryAcceptanceInputValueSource)99, ConfigAcceptanceDateProjection.UtcDate, 0 },
        { WorkflowDeliveryAcceptanceInputValueSource.InstallationCreatedAtUtc, ConfigAcceptanceDateProjection.Unspecified, 0 },
        { WorkflowDeliveryAcceptanceInputValueSource.InstallationCreatedAtUtc, (ConfigAcceptanceDateProjection)99, 0 },
        { WorkflowDeliveryAcceptanceInputValueSource.InstallationCreatedAtUtc, ConfigAcceptanceDateProjection.UtcDate, 3651 },
        { WorkflowDeliveryAcceptanceInputValueSource.InstallationCreatedAtUtc, ConfigAcceptanceDateProjection.UtcDate, -3651 },
        { WorkflowDeliveryAcceptanceInputValueSource.AuthenticatedOwnerExternalUserId, ConfigAcceptanceDateProjection.UtcDate, 0 },
        { WorkflowDeliveryAcceptanceInputValueSource.AuthenticatedOwnerExternalUserId, ConfigAcceptanceDateProjection.Unspecified, 1 },
    };

    [Fact]
    public async Task ListAsync_ShouldExposeExactlyTheExternallyConfiguredParseablePackages()
    {
        var catalog = CreateCatalog(PackageDefinitions);

        var packages = await catalog.ListAsync("admin-alpha", CancellationToken.None);

        packages.Select(static package => package.WorkflowName)
            .Should().Equal("workflow-alpha", "workflow-beta");
        packages.Should().OnlyContain(static package =>
            package.PackageId == package.WorkflowName &&
            package.SourceYaml.Length > 0 &&
            package.AcceptancePolicy.Input.Literals.Fields.ContainsKey("dry_run"));

        var parser = new WorkflowParser();
        foreach (var package in packages)
            parser.Parse(package.SourceYaml).Name.Should().Be(package.WorkflowName);
    }

    [Theory]
    [InlineData("workflow-alpha")]
    [InlineData("workflow-beta")]
    public async Task GetAsync_ShouldDeriveStableSourceAndPackageHashes(string workflowName)
    {
        var catalog = CreateCatalog(PackageDefinitions);

        var first = await catalog.GetAsync(workflowName, "admin-alpha", CancellationToken.None);
        var second = await catalog.GetAsync(workflowName, "admin-beta", CancellationToken.None);
        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(first.SourceYaml)));
        var expectedPackageHash = WorkflowDeliveryConventions.ComputePackageHash(first);

        first.SourceHash.Should().Be(expectedHash);
        first.PackageHash.Should().Be(expectedPackageHash);
        first.Version.Should().Be(expectedPackageHash[..16]);
        first.PackageVersionId.Should().Be($"{workflowName}@{expectedPackageHash[..16]}");
        second.SourceYaml.Should().Be(first.SourceYaml);
        second.SourceHash.Should().Be(first.SourceHash);
        second.PackageHash.Should().Be(first.PackageHash);
        second.Version.Should().Be(first.Version);
        second.PackageVersionId.Should().Be(first.PackageVersionId);
    }

    [Fact]
    public async Task ComputePackageHash_ShouldIgnoreAcceptanceLiteralInsertionOrder()
    {
        var package = await CreateCatalog(PackageDefinitions).GetAsync(
            "workflow-alpha",
            "admin-alpha",
            CancellationToken.None);
        var reordered = package.Clone();
        var literals = reordered.AcceptancePolicy.Input.Literals.Fields
            .OrderByDescending(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => KeyValuePair.Create(item.Key, item.Value.Clone()))
            .ToArray();
        reordered.AcceptancePolicy.Input.Literals.Fields.Clear();
        foreach (var (key, value) in literals)
            reordered.AcceptancePolicy.Input.Literals.Fields.Add(key, value);

        WorkflowDeliveryConventions.ComputePackageHash(reordered)
            .Should().Be(package.PackageHash);
    }

    [Fact]
    public async Task GetAsync_PackageIdentity_ShouldCoverAllImmutableDeliverySemantics()
    {
        var package = await CreateCatalog(PackageDefinitions).GetAsync(
            "workflow-alpha",
            "admin-alpha",
            CancellationToken.None);

        var variants = new[]
        {
            Mutate(package, value => value.VariableSchema[0].Label += " changed"),
            Mutate(package, value => value.ConnectionSlots[0].ServiceSlug += "-changed"),
            Mutate(package, value => value.Capabilities.Add("new.capability")),
            Mutate(package, value => value.RiskSummary += " changed"),
            Mutate(package, value => value.ParserDiagnostics.Add("new diagnostic")),
            Mutate(package, value => value.AcceptancePolicy.Mode = WorkflowDeliveryAcceptanceMode.Manual),
            Mutate(package, value => value.AcceptancePolicy.Limitation = "changed"),
            Mutate(package, value => value.AcceptancePolicy.Input.Literals.Fields["dry_run"].BoolValue = false),
            Mutate(package, value => value.AcceptancePolicy.Input.Bindings[0].Prefix += "changed:"),
        };

        variants.Should().OnlyContain(value =>
            WorkflowDeliveryConventions.ComputePackageHash(value) != package.PackageHash);
    }

    [Fact]
    public async Task GetAsync_ShouldBuildGenericAcceptanceRecipeWithCanonicalBindings()
    {
        var package = await CreateCatalog(PackageDefinitions).GetAsync(
            "workflow-alpha",
            "admin-alpha",
            CancellationToken.None);

        package.AcceptancePolicy.Input.Literals.Fields.Should().ContainKey("dry_run")
            .WhoseValue.BoolValue.Should().BeTrue();
        package.AcceptancePolicy.Input.Literals.Fields.Should().ContainKey("limit")
            .WhoseValue.NumberValue.Should().Be(5);
        package.AcceptancePolicy.Input.Bindings.Select(static value => value.Key)
            .Should().Equal("period", "requested_by");
        var date = package.AcceptancePolicy.Input.Bindings[0];
        date.Prefix.Should().Be("period:");
        date.Suffix.Should().Be(":utc");
        date.SourceCase.Should().Be(
            WorkflowDeliveryAcceptanceInputBinding.SourceOneofCase.InstallationCreatedAtUtc);
        date.InstallationCreatedAtUtc.DateProjection.Should().Be(
            ProtoAcceptanceDateProjection.UtcYearMonth);
        date.InstallationCreatedAtUtc.DayOffset.Should().Be(-1);
        package.AcceptancePolicy.Input.Bindings[1].SourceCase.Should().Be(
            WorkflowDeliveryAcceptanceInputBinding.SourceOneofCase.AuthenticatedOwnerExternalUserId);
    }

    [Fact]
    public async Task GetAsync_WhenSourceExistsButPackageIsNotConfigured_ShouldFailClosed()
    {
        var catalog = CreateCatalog(PackageDefinitions);

        var act = () => catalog.GetAsync(
            "workflow-outside-catalog",
            "admin-alpha",
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<WorkflowDeliveryPackageNotAllowedException>();
        exception.Which.WorkflowName.Should().Be("workflow-outside-catalog");
    }

    [Fact]
    public async Task ListAsync_WhenConfigurationContainsDuplicateWorkflowName_ShouldFailClosed()
    {
        var catalog = CreateCatalog([Package("workflow-alpha"), Package("workflow-alpha")]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow names must be unique*");
    }

    [Fact]
    public async Task ListAsync_WhenConfiguredPackagesAreEmpty_ShouldReturnEmptyCatalog()
    {
        var catalog = CreateCatalog([]);

        var packages = await catalog.ListAsync("admin-alpha", CancellationToken.None);

        packages.Should().BeEmpty();
    }

    [Fact]
    public async Task StartupProbe_WhenConfiguredPackagesAreEmpty_ShouldAllowHostStartup()
    {
        var probe = new WorkflowDeliveryPackageCatalogStartupProbe(CreateCatalog([]));

        await probe.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupProbe_WhenTypedPackageDefinitionIsInvalid_ShouldFailHostStartup()
    {
        var definition = Package("workflow-alpha");
        definition.DisplayName = " ";
        var probe = new WorkflowDeliveryPackageCatalogStartupProbe(CreateCatalog([definition]));

        var action = () => probe.StartAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*delivery display name is required*");
    }

    [Fact]
    public async Task StartupProbe_WhenConfiguredPackageSourceIsMissing_ShouldFailHostStartup()
    {
        var probe = new WorkflowDeliveryPackageCatalogStartupProbe(CreateCatalog(
            [Package("workflow-alpha")],
            new Dictionary<string, string>()));

        var action = () => probe.StartAsync(CancellationToken.None);

        await action.Should().ThrowAsync<WorkflowDeliveryPackageUnavailableException>()
            .WithMessage("*workflow-alpha*unavailable*");
    }

    [Fact]
    public async Task StartupProbe_WhenSourceWorkflowIdentityDiffers_ShouldFailHostStartup()
    {
        var probe = new WorkflowDeliveryPackageCatalogStartupProbe(CreateCatalog(
            [Package("workflow-alpha")],
            new Dictionary<string, string>
            {
                ["workflow-alpha"] = "name: workflow-beta\nsteps: []\n",
            }));

        var action = () => probe.StartAsync(CancellationToken.None);

        await action.Should().ThrowAsync<WorkflowDeliveryPackageUnavailableException>()
            .WithMessage("*different workflow identity*");
    }

    [Fact]
    public async Task ListAsync_WhenAcceptanceInputScalarIsInvalid_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "limit",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.Integer,
                Value = "not-an-integer",
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid typed scalar value*");
    }

    [Theory]
    [MemberData(nameof(InvalidAcceptanceInputBindings))]
    public async Task ListAsync_WhenAcceptanceInputBindingIsInvalid_ShouldFailClosed(
        WorkflowDeliveryAcceptanceInputValueSource source,
        ConfigAcceptanceDateProjection projection,
        int dayOffset)
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "dynamic_value",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.String,
                Source = source,
                DateProjection = projection,
                DayOffset = dayOffset,
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*acceptance*");
    }

    [Fact]
    public async Task ListAsync_WhenLiteralDeclaresBindingOptions_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "literal",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.String,
                Value = "value",
                Prefix = "prefix:",
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Literal*binding options*");
    }

    [Fact]
    public async Task ListAsync_WhenDynamicValueDeclaresLiteralOrNonStringKind_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "dynamic_value",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.Integer,
                Source = WorkflowDeliveryAcceptanceInputValueSource.InstallationCreatedAtUtc,
                Value = "1",
                DateProjection = ConfigAcceptanceDateProjection.UtcDate,
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be String*cannot declare a literal value*");
    }

    [Fact]
    public async Task ListAsync_WhenAcceptanceInputKeysAreDuplicatedAcrossLiteralAndBinding_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "same_key",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.Boolean,
                Value = "true",
            },
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "same_key",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.String,
                Source = WorkflowDeliveryAcceptanceInputValueSource.AuthenticatedOwnerExternalUserId,
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*keys must be unique*");
    }

    [Fact]
    public async Task ListAsync_WhenAcceptanceInputExceedsFieldLimit_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input = Enumerable.Range(0, 33)
            .Select(static index => new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = $"field_{index:D2}",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.Boolean,
                Value = "true",
            })
            .ToList();
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*more than 32 fields*");
    }

    [Theory]
    [InlineData("bad\nprefix", "")]
    [InlineData("", "bad\tsuffix")]
    public async Task ListAsync_WhenAcceptanceInputBindingAffixContainsControls_ShouldFailClosed(
        string prefix,
        string suffix)
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "dynamic_value",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.String,
                Source = WorkflowDeliveryAcceptanceInputValueSource.AuthenticatedOwnerExternalUserId,
                Prefix = prefix,
                Suffix = suffix,
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binding affixes are invalid*");
    }

    [Fact]
    public async Task ListAsync_WhenAcceptanceInputBindingAffixIsTooLong_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = "dynamic_value",
                Kind = WorkflowDeliveryAcceptanceInputValueKind.String,
                Source = WorkflowDeliveryAcceptanceInputValueSource.AuthenticatedOwnerExternalUserId,
                Prefix = new string('x', 4097),
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binding affixes are invalid*");
    }

    [Fact]
    public async Task ListAsync_WhenAcceptanceModeIsUndefined_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Mode = (ContractAcceptanceMode)99;
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AutomaticPreview*Manual*");
    }

    [Fact]
    public async Task ListAsync_WhenVariableKindIsUndefined_ShouldFailClosed()
    {
        var definition = Package("workflow-alpha");
        definition.Variables[0].Kind = (ContractVariableKind)99;
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*String*Integer*Number*Boolean*");
    }

    [Theory]
    [MemberData(nameof(InvalidAcceptanceInputKeys))]
    public async Task ListAsync_WhenAcceptanceInputKeyViolatesActorContract_ShouldFailBeforePublication(
        string key)
    {
        var definition = Package("workflow-alpha");
        definition.Acceptance.Input =
        [
            new WorkflowDeliveryAcceptanceInputValueOptions
            {
                Key = key,
                Kind = WorkflowDeliveryAcceptanceInputValueKind.Boolean,
                Value = "true",
            },
        ];
        var catalog = CreateCatalog([definition]);

        var act = () => catalog.ListAsync("admin-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*acceptance*input key*");
    }

    private static WorkflowDeliveryPackageCatalog CreateCatalog(
        IReadOnlyList<WorkflowDeliveryPackageOptions> packageDefinitions,
        IReadOnlyDictionary<string, string>? configuredSources = null)
    {
        var sources = configuredSources == null
            ? packageDefinitions
                .Select(static definition => definition.WorkflowName)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(
                    static name => name,
                    static name => $"name: {name}\nsteps:\n  - id: complete\n    type: assign\n    parameters:\n      target: result\n      value: done\n",
                    StringComparer.Ordinal)
            : new Dictionary<string, string>(configuredSources, StringComparer.Ordinal);
        sources["workflow-outside-catalog"] = "name: workflow-outside-catalog\nsteps: []\n";

        return new WorkflowDeliveryPackageCatalog(
            new DictionaryWorkflowDeliveryPackageSource(sources),
            new RealWorkflowDefinitionParser(),
            Options.Create(new WorkflowDeliveryOptions
            {
                Packages = [.. packageDefinitions],
            }),
            TimeProvider.System);
    }

    private static WorkflowDeliveryPackageOptions Package(string workflowName) =>
        new()
        {
            WorkflowName = workflowName,
            DisplayName = $"Package {workflowName}",
            Description = "A configured workflow delivery package.",
            RiskSummary = "May invoke an externally configured capability.",
            Capabilities = ["example.read", "example.write"],
            Variables =
            [
                new WorkflowDeliveryVariableOptions
                {
                    Key = "threshold",
                    Label = "Threshold",
                    Description = "Configured threshold.",
                    Kind = ContractVariableKind.Integer,
                    Required = true,
                    YamlPointer = "/steps/0/parameters/value",
                    JsonPointer = "/threshold",
                    DefaultValue = "10",
                },
            ],
            ConnectionSlots =
            [
                new WorkflowDeliveryConnectionSlotOptions
                {
                    Key = "provider",
                    Label = "Provider",
                    ServiceSlug = "provider-alpha",
                    Required = true,
                },
            ],
            Acceptance = new WorkflowDeliveryAcceptanceOptions
            {
                Mode = ContractAcceptanceMode.AutomaticPreview,
                Input =
                [
                    new WorkflowDeliveryAcceptanceInputValueOptions
                    {
                        Key = "dry_run",
                        Kind = WorkflowDeliveryAcceptanceInputValueKind.Boolean,
                        Value = "true",
                    },
                    new WorkflowDeliveryAcceptanceInputValueOptions
                    {
                        Key = "limit",
                        Kind = WorkflowDeliveryAcceptanceInputValueKind.Integer,
                        Value = "5",
                    },
                    new WorkflowDeliveryAcceptanceInputValueOptions
                    {
                        Key = "requested_by",
                        Kind = WorkflowDeliveryAcceptanceInputValueKind.String,
                        Source = WorkflowDeliveryAcceptanceInputValueSource.AuthenticatedOwnerExternalUserId,
                    },
                    new WorkflowDeliveryAcceptanceInputValueOptions
                    {
                        Key = "period",
                        Kind = WorkflowDeliveryAcceptanceInputValueKind.String,
                        Source = WorkflowDeliveryAcceptanceInputValueSource.InstallationCreatedAtUtc,
                        DateProjection = ConfigAcceptanceDateProjection.UtcYearMonth,
                        DayOffset = -1,
                        Prefix = "period:",
                        Suffix = ":utc",
                    },
                ],
            },
        };

    private sealed class DictionaryWorkflowDeliveryPackageSource(
        IReadOnlyDictionary<string, string> sources) : IWorkflowDeliveryPackageSource
    {
        public Task<string?> ReadSourceYamlAsync(
            string workflowName,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(sources.GetValueOrDefault(workflowName));
        }
    }

    private static WorkflowPackageVersionSnapshot Mutate(
        WorkflowPackageVersionSnapshot source,
        Action<WorkflowPackageVersionSnapshot> mutation)
    {
        var clone = source.Clone();
        mutation(clone);
        return clone;
    }

    private sealed class RealWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private readonly WorkflowParser _parser = new();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = _parser.Parse(workflowYaml);
                return Task.FromResult(WorkflowYamlParseResult.Success(workflow.Name));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
