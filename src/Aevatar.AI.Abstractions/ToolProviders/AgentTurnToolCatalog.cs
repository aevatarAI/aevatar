using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.Prompting;

namespace Aevatar.AI.Abstractions.ToolProviders;

public enum AgentProfileTurnDiagnosticCode
{
    ProfileInvalid = 0,
    RouteToolSetUnavailable = 1,
    ToolSetUnavailable = 2,
    ToolDiscoveryFailed = 3,
    ToolNameCollision = 4,
    ToolCapabilityRejected = 5,
    AliasMatched = 6,
    ClassifierMatched = 7,
    ClassifierNoMatch = 8,
    ClassifierFailed = 9,
    ShadowCandidate = 10,
    ExactSkillFetchFailed = 11,
    ExactSkillIdentityMismatch = 12,
    SelectedSkillBodyInvalid = 13,
    MaximumPolicyFilteredTools = 14,
    CatalogOverBudget = 15,
    CatalogNeedsDisambiguation = 16,
    SchemaInvalid = 17,
}

public sealed record AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode Code, string Detail);

public sealed record AgentProfileRequiredToolInvocation(string ToolName, string ArgumentsJson)
{
    public AgentProfileRequiredToolInvocation Normalize()
    {
        var toolName = string.IsNullOrWhiteSpace(ToolName) ? string.Empty : ToolName.Trim();
        var argumentsJson = string.IsNullOrWhiteSpace(ArgumentsJson) ? "{}" : ArgumentsJson.Trim();
        return new AgentProfileRequiredToolInvocation(toolName, argumentsJson);
    }
}

public enum AgentTurnToolOrigin
{
    Unspecified = 0,
    AgentRuntime = 1,
    RouteToolSet = 2,
    AgentProfile = 3,
    ConnectedService = 4,
    ResponsesState = 5,
    CallerForwarded = 6,
    Workflow = 7,
    Voice = 8,
}

public enum AgentTurnToolCatalogFailureCode
{
    InvalidToolName = 1,
    ToolNameCollision = 2,
    SchemaInvalid = 3,
    CatalogOverBudget = 4,
    CatalogNeedsDisambiguation = 5,
    CatalogProofMismatch = 6,
}

public sealed record AgentTurnToolCatalogFailure(
    AgentTurnToolCatalogFailureCode Code,
    string Detail,
    string? ToolName = null);

public sealed class AgentTurnToolCatalogException : InvalidOperationException
{
    public AgentTurnToolCatalogException(AgentTurnToolCatalogFailure failure)
        : base(failure.Detail)
    {
        Failure = failure;
        AgentTurnToolCatalogTelemetry.RecordRejected(failure.Code.ToString(), "final");
    }

    public AgentTurnToolCatalogFailure Failure { get; }
}

/// <summary>
/// Typed catalog optimization targets plus hard schema and connected-operation safety limits.
/// <see cref="MaximumToolCount"/> is retained in the durable proof as the reviewed optimization
/// target; exceeding it must never reject or truncate an otherwise valid exact catalog.
/// </summary>
public sealed record AgentTurnToolCatalogBudget(
    int MaximumToolCount,
    int MaximumSchemaBytes,
    int MaximumConnectedReadToolCount = int.MaxValue,
    int MaximumConnectedWriteToolCount = int.MaxValue)
{
    public static AgentTurnToolCatalogBudget Ordinary { get; } = new(8, 48 * 1024);

    public static AgentTurnToolCatalogBudget ConnectedOperations { get; } =
        new(8, 48 * 1024, MaximumConnectedReadToolCount: 3, MaximumConnectedWriteToolCount: 1);

    public static AgentTurnToolCatalogBudget Voice { get; } = new(6, 32 * 1024);

    public static AgentTurnToolCatalogBudget WorkflowOrAdmin { get; } = new(16, 128 * 1024);

    public static AgentTurnToolCatalogBudget Coding { get; } = new(6, 64 * 1024);

    internal void Validate()
    {
        if (MaximumToolCount < 0 || MaximumSchemaBytes < 0 ||
            MaximumConnectedReadToolCount < 0 || MaximumConnectedWriteToolCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumToolCount), "Catalog budgets cannot be negative.");
        }
    }
}

public sealed record AgentTurnToolSelection(
    IAgentTool Tool,
    AgentTurnToolOrigin Origin = AgentTurnToolOrigin.RouteToolSet,
    string SelectorDigest = "");

public sealed class AgentTurnToolDescriptor
{
    private readonly byte[] _canonicalSchemaBytes;

    internal AgentTurnToolDescriptor(
        string name,
        string description,
        byte[] canonicalSchemaBytes,
        AgentTurnToolOrigin origin,
        string selectorDigest)
    {
        Name = name;
        Description = description;
        _canonicalSchemaBytes = canonicalSchemaBytes.ToArray();
        Origin = origin;
        SelectorDigest = selectorDigest;
        SchemaSha256 = AgentTurnToolCatalogProof.Sha256(_canonicalSchemaBytes);
    }

    public string Name { get; }

    public string Description { get; }

    public ReadOnlyMemory<byte> CanonicalSchemaBytes => _canonicalSchemaBytes;

    public string CanonicalSchemaJson => Encoding.UTF8.GetString(_canonicalSchemaBytes);

    public int SchemaBytes => _canonicalSchemaBytes.Length;

    public string SchemaSha256 { get; }

    public AgentTurnToolOrigin Origin { get; }

    public string SelectorDigest { get; }
}

public sealed class AgentTurnToolCatalogProof
{
    private readonly IReadOnlyList<AgentTurnToolDescriptor> _toolDescriptors;
    private readonly IReadOnlyDictionary<string, IAgentTool>? _exactToolsByName;

    internal AgentTurnToolCatalogProof(
        IReadOnlyList<AgentTurnToolDescriptor> toolDescriptors,
        AgentTurnToolCatalogBudget budget,
        IReadOnlyDictionary<string, IAgentTool>? exactToolsByName = null)
    {
        _toolDescriptors = toolDescriptors.ToArray();
        _exactToolsByName = exactToolsByName is null
            ? null
            : exactToolsByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Budget = budget;
        ToolCount = _toolDescriptors.Count;
        SchemaBytes = _toolDescriptors.Sum(static descriptor => descriptor.SchemaBytes);
        CatalogDigest = ComputeCatalogDigest(_toolDescriptors);
    }

    public IReadOnlyList<AgentTurnToolDescriptor> ToolDescriptors => _toolDescriptors;

    public AgentTurnToolCatalogBudget Budget { get; }

    public int ToolCount { get; }

    public int SchemaBytes { get; }

    // Kept for telemetry shape compatibility; authoritative read/write counts are validated by
    // AgentTurnToolCatalog from the exact tool objects rather than inferred from descriptors.
    public int ConnectedReadToolCount { get; internal set; }

    public int ConnectedWriteToolCount { get; internal set; }

    public string CatalogDigest { get; }

    public static AgentTurnToolCatalogProof RestrictedEmpty(AgentTurnToolCatalogBudget? budget = null)
    {
        var proof = new AgentTurnToolCatalogProof([], budget ?? AgentTurnToolCatalogBudget.Ordinary)
        {
            ConnectedReadToolCount = 0,
            ConnectedWriteToolCount = 0,
        };
        AgentTurnToolCatalogTelemetry.RecordRestrictedEmptyProof(proof);
        return proof;
    }

    public static AgentTurnToolCatalogProof CreateShadowCandidate(
        IEnumerable<IAgentTool> exactTools,
        AgentTurnToolCatalogBudget budget)
    {
        ArgumentNullException.ThrowIfNull(exactTools);
        var selections = new Dictionary<string, AgentTurnToolSelection>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in exactTools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var name = NormalizeToolName(tool.Name);
            if (selections.TryGetValue(name, out var existing) && !ReferenceEquals(existing.Tool, tool))
            {
                throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                    AgentTurnToolCatalogFailureCode.ToolNameCollision,
                    $"Shadow candidate tool name '{name}' resolves to different exact objects.",
                    name));
            }

            selections[name] = new AgentTurnToolSelection(tool);
        }

        return CreateForSelections(selections.Values, budget);
    }

    public void AssertMatchesExactTools(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var exact = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var name = NormalizeToolName(tool.Name);
            if (!exact.TryGetValue(name, out var existing))
            {
                exact.Add(name, tool);
                continue;
            }
            if (ReferenceEquals(existing, tool))
                continue;

            throw ProofMismatch();
        }

        if (exact.Count != _toolDescriptors.Count)
            throw ProofMismatch();

        if (_exactToolsByName is not null &&
            (_exactToolsByName.Count != exact.Count ||
             _exactToolsByName.Any(pair =>
                 !exact.TryGetValue(pair.Key, out var current) || !ReferenceEquals(pair.Value, current))))
        {
            throw ProofMismatch();
        }

        var rebuilt = new List<AgentTurnToolDescriptor>(_toolDescriptors.Count);
        var connectedReadCount = 0;
        var connectedWriteCount = 0;
        foreach (var descriptor in _toolDescriptors)
        {
            if (!exact.TryGetValue(descriptor.Name, out var tool))
                throw ProofMismatch();

            // A persisted selector digest is evidence about the tool selected at ingress. Proof
            // validation must rebuild that evidence from the live admission owner; feeding the
            // persisted digest back into Describe makes selector validation tautological.
            var liveSelectorDigest = tool is IAgentToolOperationAdmissionOwner owner
                ? AgentToolOperationSelector.ComputeDigest(owner.OperationAdmission)
                : string.Empty;
            var current = Describe(new AgentTurnToolSelection(
                tool,
                descriptor.Origin,
                liveSelectorDigest));
            if (!string.Equals(current.Name, descriptor.Name, StringComparison.Ordinal) ||
                !string.Equals(current.Description, descriptor.Description, StringComparison.Ordinal) ||
                !string.Equals(current.SchemaSha256, descriptor.SchemaSha256, StringComparison.Ordinal) ||
                !string.Equals(current.SelectorDigest, descriptor.SelectorDigest, StringComparison.Ordinal))
            {
                throw ProofMismatch();
            }

            rebuilt.Add(current);
            if (descriptor.Origin == AgentTurnToolOrigin.ConnectedService)
            {
                if (IsConnectedReadTool(tool))
                    connectedReadCount++;
                else
                    connectedWriteCount++;
            }
        }

        var recomputed = new AgentTurnToolCatalogProof(rebuilt, Budget)
        {
            ConnectedReadToolCount = connectedReadCount,
            ConnectedWriteToolCount = connectedWriteCount,
        };
        if (!string.Equals(recomputed.CatalogDigest, CatalogDigest, StringComparison.Ordinal) ||
            recomputed.SchemaBytes != SchemaBytes ||
            recomputed.ConnectedReadToolCount != ConnectedReadToolCount ||
            recomputed.ConnectedWriteToolCount != ConnectedWriteToolCount)
        {
            throw ProofMismatch();
        }
    }

    internal static AgentTurnToolDescriptor Describe(AgentTurnToolSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selection.Tool);
        var name = NormalizeToolName(selection.Tool.Name);
        var schemaBytes = CanonicalizeSchema(name, selection.Tool.ParametersSchema);
        var selectorDigest = string.IsNullOrWhiteSpace(selection.SelectorDigest)
            ? selection.Tool is IAgentToolOperationAdmissionOwner owner
                ? AgentToolOperationSelector.ComputeDigest(owner.OperationAdmission)
                : string.Empty
            : selection.SelectorDigest.Trim();
        var origin = selection.Tool is IAgentToolOperationAdmissionOwner &&
                     selection.Origin is AgentTurnToolOrigin.Unspecified or AgentTurnToolOrigin.RouteToolSet
            ? AgentTurnToolOrigin.ConnectedService
            : selection.Origin;
        return new AgentTurnToolDescriptor(
            name,
            selection.Tool.Description ?? string.Empty,
            schemaBytes,
            origin,
            selectorDigest);
    }

    internal static AgentTurnToolCatalogProof CreateForSelections(
        IEnumerable<AgentTurnToolSelection> selections,
        AgentTurnToolCatalogBudget budget)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();
        var materialized = selections
            .Select(selection => (Selection: selection, Descriptor: Describe(selection)))
            .OrderBy(static item => item.Descriptor.Name, StringComparer.Ordinal)
            .ToArray();
        var descriptors = materialized.Select(static item => item.Descriptor).ToArray();
        var connected = materialized
            .Where(static item => item.Descriptor.Origin == AgentTurnToolOrigin.ConnectedService)
            .ToArray();
        var connectedReadCount = connected.Count(static item => IsConnectedReadTool(item.Selection.Tool));
        var connectedWriteCount = connected.Length - connectedReadCount;
        var schemaBytes = descriptors.Sum(static descriptor => descriptor.SchemaBytes);
        if (schemaBytes > budget.MaximumSchemaBytes ||
            connectedReadCount > budget.MaximumConnectedReadToolCount ||
            connectedWriteCount > budget.MaximumConnectedWriteToolCount)
        {
            throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogOverBudget,
                $"Final tool catalog exceeds its typed safety budget (schema_bytes={schemaBytes}/{budget.MaximumSchemaBytes}, connected_reads={connectedReadCount}/{budget.MaximumConnectedReadToolCount}, connected_writes={connectedWriteCount}/{budget.MaximumConnectedWriteToolCount})."));
        }

        var exactToolsByName = materialized.ToDictionary(
            static item => item.Descriptor.Name,
            static item => item.Selection.Tool,
            StringComparer.OrdinalIgnoreCase);
        return new AgentTurnToolCatalogProof(descriptors, budget, exactToolsByName)
        {
            ConnectedReadToolCount = connectedReadCount,
            ConnectedWriteToolCount = connectedWriteCount,
        };
    }

    internal static string NormalizeToolName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                AgentTurnToolCatalogFailureCode.InvalidToolName,
                "A final catalog tool must have a non-empty name."));
        }

        return name.Trim().ToLowerInvariant();
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static bool IsConnectedReadTool(IAgentTool tool) =>
        tool is IAgentToolOperationAdmissionOwner owner
            ? owner.OperationAdmission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly
            : tool.IsReadOnly;

    internal static byte[] CanonicalizeSchema(string toolName, string? schema)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(schema) ? "{}" : schema);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The schema root must be an object.");
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteCanonicalJson(writer, document.RootElement);
            }

            return stream.ToArray();
        }
        catch (JsonException)
        {
            throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                AgentTurnToolCatalogFailureCode.SchemaInvalid,
                $"Tool '{toolName}' has an invalid parameters schema.",
                toolName));
        }
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string ComputeCatalogDigest(IReadOnlyList<AgentTurnToolDescriptor> descriptors)
    {
        using var stream = new MemoryStream();
        foreach (var descriptor in descriptors)
        {
            var toolHash = ComputeToolHash(descriptor);
            stream.Write(toolHash);
        }

        return Sha256(stream.ToArray());
    }

    private static byte[] ComputeToolHash(AgentTurnToolDescriptor descriptor)
    {
        using var stream = new MemoryStream();
        WriteDigestField(stream, descriptor.Name);
        WriteDigestField(stream, descriptor.Description);
        stream.Write(descriptor.CanonicalSchemaBytes.Span);
        stream.WriteByte(0);
        WriteDigestField(stream, ((int)descriptor.Origin).ToString(CultureInfo.InvariantCulture));
        WriteDigestField(stream, descriptor.SelectorDigest);
        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteDigestField(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
    }

    private static AgentTurnToolCatalogException ProofMismatch() =>
        new(new AgentTurnToolCatalogFailure(
            AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
            "The exact tool objects do not match the frozen turn catalog proof."));
}

/// <summary>
/// Immutable, request-local final Aevatar-owned tool catalog for one model turn. A restricted
/// empty catalog is represented by an instance with zero exact tools; null is never an alias for it.
/// </summary>
public sealed class AgentTurnToolCatalog
{
    public const int MaximumDiagnostics = 16;
    private const int MaximumDiagnosticDetailUtf8Bytes = 256;
    private readonly IReadOnlyDictionary<string, AgentTurnToolSelection> _selections;

    public AgentTurnToolCatalog(
        IEnumerable<string> finalAllowedToolNames,
        ProfileRoutingPromptLayer? profilePromptLayer,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        string? selectedIntentId,
        string? candidateIntentId,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics = null,
        IEnumerable<IAgentTool>? exactTools = null,
        bool hasUnresolvedConnectedServiceSelectors = false,
        AgentProfileRequiredToolInvocation? requiredToolInvocation = null,
        AgentTurnToolCatalogBudget? budget = null,
        AgentTurnToolOrigin exactToolOrigin = AgentTurnToolOrigin.RouteToolSet)
        : this(
            finalAllowedToolNames,
            profilePromptLayer,
            selectedSkillPromptLayer,
            selectedIntentId,
            candidateIntentId,
            diagnostics,
            (exactTools ?? []).Select(tool => new AgentTurnToolSelection(tool, exactToolOrigin)),
            hasUnresolvedConnectedServiceSelectors,
            requiredToolInvocation,
            budget ?? AgentTurnToolCatalogBudget.Ordinary)
    {
    }

    public AgentTurnToolCatalog(
        IEnumerable<string> finalAllowedToolNames,
        ProfileRoutingPromptLayer? profilePromptLayer,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        string? selectedIntentId,
        string? candidateIntentId,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics,
        IEnumerable<AgentTurnToolSelection> exactToolSelections,
        bool hasUnresolvedConnectedServiceSelectors,
        AgentProfileRequiredToolInvocation? requiredToolInvocation,
        AgentTurnToolCatalogBudget budget)
        : this(
            finalAllowedToolNames,
            profilePromptLayer,
            selectedSkillPromptLayer,
            selectedIntentId,
            candidateIntentId,
            diagnostics,
            exactToolSelections,
            hasUnresolvedConnectedServiceSelectors,
            requiredToolInvocation,
            budget,
            recordTelemetry: true)
    {
    }

    /// <param name="recordTelemetry">
    /// False when this instance re-expresses an already observed turn catalog (exact-object
    /// binding or narrowing). Those derivations describe the same materialized turn, so counting
    /// them again would multiply every catalog metric by the number of derivation steps.
    /// </param>
    private AgentTurnToolCatalog(
        IEnumerable<string> finalAllowedToolNames,
        ProfileRoutingPromptLayer? profilePromptLayer,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        string? selectedIntentId,
        string? candidateIntentId,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics,
        IEnumerable<AgentTurnToolSelection> exactToolSelections,
        bool hasUnresolvedConnectedServiceSelectors,
        AgentProfileRequiredToolInvocation? requiredToolInvocation,
        AgentTurnToolCatalogBudget budget,
        bool recordTelemetry)
    {
        ArgumentNullException.ThrowIfNull(finalAllowedToolNames);
        ArgumentNullException.ThrowIfNull(exactToolSelections);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();

        var selectionSnapshot = exactToolSelections.ToArray();
        FinalAllowedToolNames = finalAllowedToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        ToolVisibility = new AgentToolVisibilityScope(FinalAllowedToolNames);
        _selections = FreezeSelections(selectionSnapshot, FinalAllowedToolNames);
        ExactTools = _selections.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Tool,
            StringComparer.OrdinalIgnoreCase);
        Budget = budget;
        Proof = BuildProof(_selections.Values, budget);
        ProfilePromptLayer = profilePromptLayer;
        SelectedSkillPromptLayer = selectedSkillPromptLayer;
        SelectedIntentId = Normalize(selectedIntentId);
        CandidateIntentId = Normalize(candidateIntentId);
        Diagnostics = CopyDiagnostics(diagnostics);
        HasUnresolvedConnectedServiceSelectors = hasUnresolvedConnectedServiceSelectors;
        RequiredToolInvocation = requiredToolInvocation?.Normalize();
        var authorityToolCount = selectionSnapshot.Count(static selection => selection?.Tool is not null);
        var filteredToolCount = Math.Max(
            0,
            Math.Max(authorityToolCount, FinalAllowedToolNames.Count) - Proof.ToolCount);
        if (recordTelemetry)
        {
            AgentTurnToolCatalogTelemetry.RecordCatalog(
                Proof,
                authorityToolCount,
                filteredToolCount,
                Diagnostics,
                ProfilePromptLayer?.Provenance.Source,
                SelectedIntentId ?? CandidateIntentId,
                HasUnresolvedConnectedServiceSelectors);
        }
    }

    public IReadOnlySet<string> FinalAllowedToolNames { get; }

    public AgentToolVisibilityScope ToolVisibility { get; }

    public IReadOnlyDictionary<string, IAgentTool> ExactTools { get; }

    public AgentTurnToolCatalogBudget Budget { get; }

    public AgentTurnToolCatalogProof Proof { get; }

    public ProfileRoutingPromptLayer? ProfilePromptLayer { get; }

    public SelectedSkillPromptLayer? SelectedSkillPromptLayer { get; }

    public string? SelectedIntentId { get; }

    public string? CandidateIntentId { get; }

    public IReadOnlyList<AgentProfileTurnDiagnostic> Diagnostics { get; }

    public bool HasUnresolvedConnectedServiceSelectors { get; }

    public AgentProfileRequiredToolInvocation? RequiredToolInvocation { get; }

    public AgentTurnToolCatalog BindFinalExactTools(
        IEnumerable<IAgentTool> runtimeTools,
        AgentTurnToolOrigin runtimeOrigin = AgentTurnToolOrigin.AgentRuntime)
    {
        ArgumentNullException.ThrowIfNull(runtimeTools);
        var selections = new List<AgentTurnToolSelection>(_selections.Values);
        foreach (var tool in runtimeTools)
        {
            var name = AgentTurnToolCatalogProof.NormalizeToolName(tool.Name);
            if (!FinalAllowedToolNames.Contains(name))
                continue;
            // A name already bound by this catalog keeps its own exact object: that object was
            // discovered under the turn's real execution context and is what the proof covers.
            // Runtime tools are a separate discovery pass of the same sources, so an equally
            // named object is a duplicate of an already-authorized tool, not a conflict.
            if (_selections.ContainsKey(name))
                continue;

            selections.Add(new AgentTurnToolSelection(tool, runtimeOrigin));
        }

        var selectedNames = selections
            .Select(static selection => AgentTurnToolCatalogProof.NormalizeToolName(selection.Tool.Name))
            .Where(FinalAllowedToolNames.Contains)
            .ToArray();
        return new AgentTurnToolCatalog(
            selectedNames,
            ProfilePromptLayer,
            SelectedSkillPromptLayer,
            SelectedIntentId,
            CandidateIntentId,
            Diagnostics,
            selections,
            HasUnresolvedConnectedServiceSelectors,
            RequiredToolInvocation,
            Budget,
            recordTelemetry: false);
    }

    public AgentTurnToolCatalog NarrowToAllowedToolNames(IEnumerable<string> allowedToolNames)
    {
        ArgumentNullException.ThrowIfNull(allowedToolNames);
        var allowed = allowedToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Where(FinalAllowedToolNames.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selections = _selections
            .Where(pair => allowed.Contains(pair.Key))
            .Select(static pair => pair.Value)
            .ToArray();
        return new AgentTurnToolCatalog(
            allowed,
            ProfilePromptLayer,
            SelectedSkillPromptLayer,
            SelectedIntentId,
            CandidateIntentId,
            Diagnostics,
            selections,
            HasUnresolvedConnectedServiceSelectors,
            RequiredToolInvocation,
            Budget,
            recordTelemetry: false);
    }

    public void AssertProofMatchesExactTools(IEnumerable<IAgentTool> tools)
    {
        Proof.AssertMatchesExactTools(tools);
    }

    private static IReadOnlyDictionary<string, AgentTurnToolSelection> FreezeSelections(
        IEnumerable<AgentTurnToolSelection> selections,
        IReadOnlySet<string> allowedNames)
    {
        var exact = new Dictionary<string, AgentTurnToolSelection>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            ArgumentNullException.ThrowIfNull(selection.Tool);
            var name = AgentTurnToolCatalogProof.NormalizeToolName(selection.Tool.Name);
            if (!allowedNames.Contains(name))
                continue;
            if (!exact.TryGetValue(name, out var existing))
            {
                exact.Add(name, selection with { SelectorDigest = selection.SelectorDigest?.Trim() ?? string.Empty });
                continue;
            }
            if (ReferenceEquals(existing.Tool, selection.Tool))
                continue;

            throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                AgentTurnToolCatalogFailureCode.ToolNameCollision,
                $"Final catalog tool name '{name}' resolves to different exact objects.",
                name));
        }

        return exact
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToFrozenDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static AgentTurnToolCatalogProof BuildProof(
        IEnumerable<AgentTurnToolSelection> selections,
        AgentTurnToolCatalogBudget budget) =>
        AgentTurnToolCatalogProof.CreateForSelections(selections, budget);

    private static IReadOnlyList<AgentProfileTurnDiagnostic> CopyDiagnostics(
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics)
    {
        if (diagnostics is null or { Count: 0 })
            return [];

        return diagnostics
            .Take(MaximumDiagnostics)
            .Select(static diagnostic => diagnostic with
            {
                Detail = TruncateUtf8(diagnostic.Detail ?? string.Empty, MaximumDiagnosticDetailUtf8Bytes),
            })
            .ToArray();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
                break;

            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }
}
