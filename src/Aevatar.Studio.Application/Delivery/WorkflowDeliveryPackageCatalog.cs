using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using ActorAcceptanceDateProjection = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceDateProjection;
using ActorAcceptanceInputBinding = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputBinding;
using ActorAcceptanceInputRecipe = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputRecipe;
using ContractAcceptanceMode = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceMode;
using ContractVariableKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableKind;

namespace Aevatar.Studio.Application.Delivery;

public interface IWorkflowDeliveryPackageCatalog
{
    Task<IReadOnlyList<WorkflowPackageVersionSnapshot>> ListAsync(
        string createdBy,
        CancellationToken ct = default);

    Task<WorkflowPackageVersionSnapshot> GetAsync(
        string workflowName,
        string createdBy,
        CancellationToken ct = default);
}

public interface IWorkflowDeliveryPackageSource
{
    Task<string?> ReadSourceYamlAsync(
        string workflowName,
        CancellationToken ct = default);
}

public sealed class WorkflowDeliveryPackageCatalog : IWorkflowDeliveryPackageCatalog
{
    private const int MaximumAcceptanceInputFields = 32;
    private const int MaximumAcceptanceStringLength = 4096;
    private const long MaximumExactJsonInteger = 9_007_199_254_740_991;

    private readonly IWorkflowDeliveryPackageSource _packageSource;
    private readonly IWorkflowDefinitionParser _parser;
    private readonly IOptions<WorkflowDeliveryOptions> _options;
    private readonly TimeProvider _timeProvider;

    public WorkflowDeliveryPackageCatalog(
        IWorkflowDeliveryPackageSource packageSource,
        IWorkflowDefinitionParser parser,
        IOptions<WorkflowDeliveryOptions> options,
        TimeProvider timeProvider)
    {
        _packageSource = packageSource ?? throw new ArgumentNullException(nameof(packageSource));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<WorkflowPackageVersionSnapshot>> ListAsync(
        string createdBy,
        CancellationToken ct = default)
    {
        var definitions = GetDefinitions();
        var packages = new List<WorkflowPackageVersionSnapshot>(definitions.Count);
        foreach (var definition in definitions)
            packages.Add(await CreatePackageAsync(definition, createdBy, ct));
        return packages;
    }

    public async Task<WorkflowPackageVersionSnapshot> GetAsync(
        string workflowName,
        string createdBy,
        CancellationToken ct = default)
    {
        var normalizedName = NormalizeRequired(workflowName, nameof(workflowName));
        var definition = GetDefinitions().SingleOrDefault(value =>
            string.Equals(value.WorkflowName, normalizedName, StringComparison.Ordinal));
        if (definition == null)
            throw new WorkflowDeliveryPackageNotAllowedException(normalizedName);

        return await CreatePackageAsync(definition, createdBy, ct);
    }

    private async Task<WorkflowPackageVersionSnapshot> CreatePackageAsync(
        PackageDefinition definition,
        string createdBy,
        CancellationToken ct)
    {
        var normalizedName = definition.WorkflowName;
        var sourceYaml = await _packageSource.ReadSourceYamlAsync(normalizedName, ct)
            ?? throw new WorkflowDeliveryPackageUnavailableException(
                normalizedName,
                $"Allowlisted workflow package '{normalizedName}' is unavailable.");
        var parse = await _parser.ParseWorkflowYamlAsync(sourceYaml, ct);
        if (!parse.Succeeded || !string.Equals(parse.WorkflowName, normalizedName, StringComparison.Ordinal))
        {
            throw new WorkflowDeliveryPackageUnavailableException(
                normalizedName,
                string.IsNullOrWhiteSpace(parse.Error)
                    ? "Workflow parser returned a different workflow identity."
                    : parse.Error);
        }

        var sourceHash = ComputeHash(sourceYaml);
        var now = _timeProvider.GetUtcNow();
        var package = new WorkflowPackageVersionSnapshot
        {
            PackageId = normalizedName,
            WorkflowName = normalizedName,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            SourceYaml = sourceYaml,
            SourceHash = sourceHash,
            RiskSummary = definition.RiskSummary,
            AcceptancePolicy = new WorkflowDeliveryAcceptancePolicy
            {
                Mode = definition.AcceptanceMode switch
                {
                    ContractAcceptanceMode.AutomaticPreview => WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                    ContractAcceptanceMode.Manual => WorkflowDeliveryAcceptanceMode.Manual,
                    _ => WorkflowDeliveryAcceptanceMode.Unspecified,
                },
                Limitation = definition.AcceptanceLimitation ?? string.Empty,
                Input = definition.AcceptanceInput.Clone(),
            },
            CreatedBy = NormalizeRequired(createdBy, nameof(createdBy)),
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        };
        package.VariableSchema.Add(definition.Variables.Select(ToProto));
        package.ConnectionSlots.Add(definition.ConnectionSlots.Select(ToProto));
        package.Capabilities.Add(definition.Capabilities);
        package.PackageHash = WorkflowDeliveryConventions.ComputePackageHash(package);
        package.Version = package.PackageHash[..16];
        package.PackageVersionId = WorkflowDeliveryConventions.BuildPackageVersionId(
            normalizedName,
            package.PackageHash);
        return package;
    }

    internal static string ComputeHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private IReadOnlyList<PackageDefinition> GetDefinitions()
    {
        var definitions = (_options.Value.Packages ?? [])
            .Select(ToDefinition)
            .ToArray();
        if (definitions.Select(static value => value.WorkflowName)
            .Distinct(StringComparer.Ordinal).Count() != definitions.Length)
        {
            throw new InvalidOperationException("Delivery package workflow names must be unique.");
        }

        return definitions;
    }

    private static WorkflowDeliveryVariableDefinition ToProto(VariableDefinition value) =>
        new()
        {
            Key = value.Key,
            Label = value.Label,
            Description = value.Description,
            Kind = (WorkflowDeliveryVariableKind)(int)value.Kind,
            Required = value.Required,
            YamlPointer = value.YamlPointer,
            JsonPointer = value.JsonPointer ?? string.Empty,
            DefaultValue = value.DefaultValue,
        };

    private static WorkflowDeliveryConnectionSlotDefinition ToProto(ConnectionSlotDefinition value) =>
        new()
        {
            Key = value.Key,
            Label = value.Label,
            ServiceSlug = value.ServiceSlug,
            Required = value.Required,
        };

    private static PackageDefinition ToDefinition(WorkflowDeliveryPackageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var variables = (options.Variables ?? []).Select(static value => new VariableDefinition(
            NormalizeRequired(value.Key, "delivery variable key"),
            NormalizeRequired(value.Label, "delivery variable label"),
            NormalizeRequired(value.Description, "delivery variable description"),
            RequireVariableKind(value.Kind),
            value.Required,
            NormalizeRequired(value.YamlPointer, "delivery variable yaml pointer"),
            NormalizeOptional(value.JsonPointer),
            value.DefaultValue ?? string.Empty)).ToArray();
        if (variables.Select(static value => value.Key).Distinct(StringComparer.Ordinal).Count() != variables.Length)
            throw new InvalidOperationException("Delivery package variable keys must be unique.");

        var connectionSlots = (options.ConnectionSlots ?? []).Select(static value => new ConnectionSlotDefinition(
            NormalizeRequired(value.Key, "delivery connection slot key"),
            NormalizeRequired(value.Label, "delivery connection slot label"),
            NormalizeRequired(value.ServiceSlug, "delivery connection service slug"),
            value.Required)).ToArray();
        if (connectionSlots.Select(static value => value.Key).Distinct(StringComparer.Ordinal).Count() != connectionSlots.Length)
            throw new InvalidOperationException("Delivery package connection slot keys must be unique.");

        var acceptance = options.Acceptance ?? throw new InvalidOperationException(
            "Delivery package acceptance policy is required.");
        if (acceptance.Mode is not (ContractAcceptanceMode.AutomaticPreview or ContractAcceptanceMode.Manual))
        {
            throw new InvalidOperationException(
                "Delivery package acceptance mode must be AutomaticPreview or Manual.");
        }
        var limitation = NormalizeOptional(acceptance.Limitation);
        if (acceptance.Mode == ContractAcceptanceMode.Manual && limitation == null)
            throw new InvalidOperationException("Manual delivery package acceptance requires a limitation.");

        var acceptanceInput = BuildAcceptanceInput(acceptance.Input);
        return new PackageDefinition(
            NormalizeRequired(options.WorkflowName, "delivery workflow name"),
            NormalizeRequired(options.DisplayName, "delivery display name"),
            NormalizeRequired(options.Description, "delivery description"),
            NormalizeRequired(options.RiskSummary, "delivery risk summary"),
            (options.Capabilities ?? [])
                .Select(static value => NormalizeRequired(value, "delivery capability"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            variables,
            connectionSlots,
            acceptance.Mode,
            limitation,
            acceptanceInput);
    }

    private static ActorAcceptanceInputRecipe BuildAcceptanceInput(
        IList<WorkflowDeliveryAcceptanceInputValueOptions>? configuredValues)
    {
        var values = configuredValues ?? [];
        if (values.Count > MaximumAcceptanceInputFields)
        {
            throw new InvalidOperationException(
                $"Delivery acceptance input cannot contain more than {MaximumAcceptanceInputFields} fields.");
        }

        var normalized = values.Select(static item => new
        {
            Key = NormalizeRequired(item.Key, "delivery acceptance input key"),
            item.Kind,
            item.Source,
            Value = item.Value ?? string.Empty,
            item.DateProjection,
            item.DayOffset,
            Prefix = item.Prefix ?? string.Empty,
            Suffix = item.Suffix ?? string.Empty,
        }).OrderBy(static item => item.Key, StringComparer.Ordinal).ToArray();
        if (normalized.Select(static item => item.Key).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new InvalidOperationException("Delivery acceptance input keys must be unique.");

        var recipe = new ActorAcceptanceInputRecipe
        {
            Literals = new Struct(),
        };
        foreach (var item in normalized)
        {
            if (item.Source == WorkflowDeliveryAcceptanceInputValueSource.Literal)
            {
                if (item.DateProjection != WorkflowDeliveryAcceptanceDateProjection.Unspecified ||
                    item.DayOffset != 0 ||
                    item.Prefix.Length != 0 ||
                    item.Suffix.Length != 0)
                {
                    throw new InvalidOperationException(
                        "Literal delivery acceptance input cannot declare binding options.");
                }
                recipe.Literals.Fields.Add(item.Key, ToProtoValue(item.Kind, item.Value));
                continue;
            }

            if (item.Kind != WorkflowDeliveryAcceptanceInputValueKind.String || item.Value.Length != 0)
            {
                throw new InvalidOperationException(
                    "Dynamic delivery acceptance input must be String and cannot declare a literal value.");
            }
            recipe.Bindings.Add(BuildInputBinding(
                item.Key,
                item.Source,
                item.DateProjection,
                item.DayOffset,
                item.Prefix,
                item.Suffix));
        }
        WorkflowDeliveryConventions.ValidateAcceptanceInput(recipe);
        return recipe;
    }

    private static ActorAcceptanceInputBinding BuildInputBinding(
        string key,
        WorkflowDeliveryAcceptanceInputValueSource source,
        WorkflowDeliveryAcceptanceDateProjection dateProjection,
        int dayOffset,
        string prefix,
        string suffix)
    {
        var binding = new ActorAcceptanceInputBinding
        {
            Key = key,
            Prefix = prefix,
            Suffix = suffix,
        };

        switch (source)
        {
            case WorkflowDeliveryAcceptanceInputValueSource.InstallationCreatedAtUtc:
                binding.InstallationCreatedAtUtc = new WorkflowDeliveryInstallationCreatedAtUtcInput
                {
                    DateProjection = MapDateProjection(dateProjection),
                    DayOffset = dayOffset,
                };
                break;
            case WorkflowDeliveryAcceptanceInputValueSource.AuthenticatedOwnerExternalUserId:
                if (dateProjection != WorkflowDeliveryAcceptanceDateProjection.Unspecified || dayOffset != 0)
                {
                    throw new InvalidOperationException(
                        "Authenticated owner delivery acceptance input cannot declare date options.");
                }
                binding.AuthenticatedOwnerExternalUserId =
                    new WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput();
                break;
            default:
                throw new InvalidOperationException(
                    "Delivery acceptance input source must be Literal, InstallationCreatedAtUtc, or AuthenticatedOwnerExternalUserId.");
        }

        return binding;
    }

    private static ActorAcceptanceDateProjection MapDateProjection(
        WorkflowDeliveryAcceptanceDateProjection projection) => projection switch
        {
            WorkflowDeliveryAcceptanceDateProjection.UtcDate => ActorAcceptanceDateProjection.UtcDate,
            WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth => ActorAcceptanceDateProjection.UtcYearMonth,
            WorkflowDeliveryAcceptanceDateProjection.UtcIsoWeek => ActorAcceptanceDateProjection.UtcIsoWeek,
            WorkflowDeliveryAcceptanceDateProjection.UtcCompactDate => ActorAcceptanceDateProjection.UtcCompactDate,
            _ => throw new InvalidOperationException(
                "Installation-created UTC delivery acceptance input requires a supported date projection."),
        };

    private static Value ToProtoValue(
        WorkflowDeliveryAcceptanceInputValueKind kind,
        string? value)
    {
        var normalized = value ?? string.Empty;
        return kind switch
        {
            WorkflowDeliveryAcceptanceInputValueKind.String when normalized.Length <= MaximumAcceptanceStringLength =>
                Value.ForString(normalized),
            WorkflowDeliveryAcceptanceInputValueKind.Integer when
                long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) &&
                integer is >= -MaximumExactJsonInteger and <= MaximumExactJsonInteger =>
                Value.ForNumber(integer),
            WorkflowDeliveryAcceptanceInputValueKind.Number when
                double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                double.IsFinite(number) => Value.ForNumber(number),
            WorkflowDeliveryAcceptanceInputValueKind.Boolean when bool.TryParse(normalized, out var boolean) =>
                Value.ForBool(boolean),
            WorkflowDeliveryAcceptanceInputValueKind.String =>
                throw new InvalidOperationException(
                    $"Delivery acceptance string values cannot exceed {MaximumAcceptanceStringLength} characters."),
            _ => throw new InvalidOperationException("Delivery acceptance input contains an invalid typed scalar value."),
        };
    }

    private static ContractVariableKind RequireVariableKind(ContractVariableKind kind) => kind switch
    {
        ContractVariableKind.String or
        ContractVariableKind.Integer or
        ContractVariableKind.Number or
        ContractVariableKind.Boolean => kind,
        _ => throw new InvalidOperationException(
            "Delivery package variable kind must be String, Integer, Number, or Boolean."),
    };

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0
            ? throw new InvalidOperationException($"{name} is required.")
            : normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PackageDefinition(
        string WorkflowName,
        string DisplayName,
        string Description,
        string RiskSummary,
        IReadOnlyList<string> Capabilities,
        IReadOnlyList<VariableDefinition> Variables,
        IReadOnlyList<ConnectionSlotDefinition> ConnectionSlots,
        ContractAcceptanceMode AcceptanceMode,
        string? AcceptanceLimitation,
        ActorAcceptanceInputRecipe AcceptanceInput);

    private sealed record VariableDefinition(
        string Key,
        string Label,
        string Description,
        ContractVariableKind Kind,
        bool Required,
        string YamlPointer,
        string? JsonPointer,
        string DefaultValue);

    private sealed record ConnectionSlotDefinition(
        string Key,
        string Label,
        string ServiceSlug,
        bool Required);
}

public sealed class WorkflowDeliveryPackageNotAllowedException(string workflowName)
    : InvalidOperationException($"Workflow '{workflowName}' is not in the Delivery allowlist.")
{
    public string WorkflowName { get; } = workflowName;
}

public sealed class WorkflowDeliveryPackageUnavailableException(string workflowName, string safeMessage)
    : InvalidOperationException(safeMessage)
{
    public string WorkflowName { get; } = workflowName;
}
