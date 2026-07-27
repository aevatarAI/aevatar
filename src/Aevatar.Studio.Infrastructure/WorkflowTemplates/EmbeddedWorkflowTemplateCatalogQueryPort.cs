using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Studio.Infrastructure.WorkflowTemplates;

internal sealed record EmbeddedWorkflowTemplateRegistration(
    string TemplateId,
    string Revision,
    int ProductOrder,
    WorkflowTemplateLocalizedText Title,
    WorkflowTemplateLocalizedText Summary,
    WorkflowTemplateLocalizedText Description,
    string Category,
    IReadOnlyList<string> Tags,
    WorkflowTemplateExpectedIO ExpectedIO,
    string WorkflowYaml,
    WorkflowTemplateRequirements Requirements,
    WorkflowTemplateCompatibility Compatibility,
    bool IsEnabled = true);

internal sealed class EmbeddedWorkflowTemplateCatalogQueryPort : IWorkflowTemplateCatalogQueryPort
{
    private const int MaximumPageSize = 100;
    private const int MaximumQueryLength = 200;
    private const int MaximumCategoryLength = 64;
    private const int MaximumCursorLength = 256;
    private const string CursorPrefix = "workflow-templates:v1:";

    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly IWorkflowYamlDocumentService _workflowYamlDocumentService;
    private readonly WorkflowValidator _studioWorkflowValidator;
    private readonly WorkflowGraphMapper _workflowGraphMapper;
    private readonly EmbeddedWorkflowTemplateRegistration[] _registrations;

    public EmbeddedWorkflowTemplateCatalogQueryPort(
        IWorkflowDefinitionParser workflowDefinitionParser,
        IWorkflowYamlDocumentService workflowYamlDocumentService,
        WorkflowValidator studioWorkflowValidator,
        WorkflowGraphMapper workflowGraphMapper,
        IEnumerable<EmbeddedWorkflowTemplateRegistration> registrations)
    {
        _workflowDefinitionParser = workflowDefinitionParser ??
                                    throw new ArgumentNullException(nameof(workflowDefinitionParser));
        _workflowYamlDocumentService = workflowYamlDocumentService ??
                                       throw new ArgumentNullException(nameof(workflowYamlDocumentService));
        _studioWorkflowValidator = studioWorkflowValidator ??
                                   throw new ArgumentNullException(nameof(studioWorkflowValidator));
        _workflowGraphMapper = workflowGraphMapper ??
                               throw new ArgumentNullException(nameof(workflowGraphMapper));
        _registrations = registrations?.Select(Snapshot).ToArray() ??
                         throw new ArgumentNullException(nameof(registrations));
        ValidateRegistrationIdentities(_registrations);
    }

    public async Task<WorkflowTemplateCatalogPage> ListAsync(
        WorkflowTemplateCatalogQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalizedQuery = NormalizeOptional(query.Query, MaximumQueryLength, nameof(query.Query));
        var normalizedCategory = NormalizeOptional(query.Category, MaximumCategoryLength, nameof(query.Category));
        if (query.PageSize is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize), $"PageSize must be between 1 and {MaximumPageSize}.");

        var offset = DecodeCursor(query.Cursor);
        var matching = _registrations
            .Where(static registration => registration.IsEnabled)
            .Where(registration => Matches(registration, normalizedQuery, normalizedCategory))
            .OrderBy(static registration => registration.ProductOrder)
            .ThenBy(static registration => registration.TemplateId, StringComparer.Ordinal)
            .ThenBy(static registration => registration.Revision, StringComparer.Ordinal)
            .ToArray();
        if (offset > matching.Length)
            throw new ArgumentException("Cursor is outside the catalog result set.", nameof(query.Cursor));

        var pageRegistrations = matching.Skip(offset).Take(query.PageSize).ToArray();
        var items = new List<WorkflowTemplateSummary>(pageRegistrations.Length);
        foreach (var registration in pageRegistrations)
        {
            await EnsureValidWorkflowAsync(registration, ct);
            items.Add(ToSummary(registration));
        }

        var nextOffset = offset + pageRegistrations.Length;
        var nextCursor = nextOffset < matching.Length ? EncodeCursor(nextOffset) : null;
        return new WorkflowTemplateCatalogPage(
            items,
            nextCursor,
            ComputeETag(
                normalizedQuery,
                normalizedCategory,
                offset,
                query.PageSize,
                matching.Length,
                pageRegistrations));
    }

    public async Task<WorkflowTemplateLookupResult> GetAsync(
        string templateId,
        string revision,
        CancellationToken ct = default)
    {
        var normalizedTemplateId = NormalizeTemplateId(templateId);
        var normalizedRevision = NormalizeRevision(revision);
        var registration = _registrations.FirstOrDefault(item =>
            string.Equals(item.TemplateId, normalizedTemplateId, StringComparison.Ordinal) &&
            string.Equals(item.Revision, normalizedRevision, StringComparison.Ordinal));
        if (registration == null)
            return new WorkflowTemplateLookupResult(WorkflowTemplateLookupStatus.NotFound, null);
        if (!registration.IsEnabled)
            return new WorkflowTemplateLookupResult(WorkflowTemplateLookupStatus.Disabled, null);

        await EnsureValidWorkflowAsync(registration, ct);
        var detail = ToDetail(registration);
        var status = registration.Compatibility.Status == WorkflowTemplateCompatibilityStatus.Compatible
            ? WorkflowTemplateLookupStatus.Found
            : WorkflowTemplateLookupStatus.Incompatible;
        return new WorkflowTemplateLookupResult(status, detail);
    }

    private async Task EnsureValidWorkflowAsync(
        EmbeddedWorkflowTemplateRegistration registration,
        CancellationToken ct)
    {
        var result = await _workflowDefinitionParser.ParseWorkflowYamlAsync(registration.WorkflowYaml, ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Embedded workflow template '{registration.TemplateId}' revision '{registration.Revision}' failed canonical workflow validation.");
        }

        var studioParse = _workflowYamlDocumentService.Parse(registration.WorkflowYaml);
        if (studioParse.Document == null || HasErrors(studioParse.Findings))
            throw InvalidStudioWorkflow(registration);

        var studioFindings = _studioWorkflowValidator.Validate(studioParse.Document);
        if (HasErrors(studioFindings))
            throw InvalidStudioWorkflow(registration);

        var graph = _workflowGraphMapper.Map(studioParse.Document);
        var nodeIds = graph.Nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (nodeIds.Count != graph.Nodes.Count ||
            graph.Nodes.Count != studioParse.Document.Steps.Count ||
            graph.Edges.Any(edge => !nodeIds.Contains(edge.Source) || !nodeIds.Contains(edge.Target)))
        {
            throw InvalidStudioWorkflow(registration);
        }
    }

    private static bool HasErrors(IEnumerable<ValidationFinding> findings) =>
        findings.Any(static finding => finding.Level == ValidationLevel.Error);

    private static InvalidOperationException InvalidStudioWorkflow(
        EmbeddedWorkflowTemplateRegistration registration) =>
        new(
            $"Embedded workflow template '{registration.TemplateId}' revision '{registration.Revision}' failed Studio authoring or graph validation.");

    private static bool Matches(
        EmbeddedWorkflowTemplateRegistration registration,
        string? query,
        string? category)
    {
        if (category != null && !string.Equals(registration.Category, category, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query == null)
            return true;

        return registration.TemplateId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Contains(registration.Title, query) ||
               Contains(registration.Summary, query) ||
               Contains(registration.Description, query) ||
               registration.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               registration.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Contains(WorkflowTemplateLocalizedText text, string query) =>
        text.EnUS.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        text.ZhCN.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static WorkflowTemplateSummary ToSummary(EmbeddedWorkflowTemplateRegistration registration) =>
        new(
            registration.TemplateId,
            registration.Revision,
            registration.Title,
            registration.Summary,
            registration.Description,
            registration.Category,
            registration.Tags,
            registration.ExpectedIO,
            registration.Requirements,
            registration.Compatibility);

    private static WorkflowTemplateDetail ToDetail(EmbeddedWorkflowTemplateRegistration registration) =>
        new(
            registration.TemplateId,
            registration.Revision,
            registration.Title,
            registration.Summary,
            registration.Description,
            registration.Category,
            registration.Tags,
            registration.ExpectedIO,
            registration.Requirements,
            registration.Compatibility,
            registration.WorkflowYaml);

    private static EmbeddedWorkflowTemplateRegistration Snapshot(
        EmbeddedWorkflowTemplateRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Requirements);
        ArgumentNullException.ThrowIfNull(registration.Compatibility);
        return registration with
        {
            Tags = Array.AsReadOnly(
                registration.Tags?.ToArray() ??
                throw new ArgumentNullException(nameof(registration.Tags))),
            Requirements = registration.Requirements with
            {
                RequiredPrimitives = Array.AsReadOnly(
                    registration.Requirements.RequiredPrimitives?.ToArray() ??
                    throw new ArgumentNullException(nameof(registration.Requirements.RequiredPrimitives))),
            },
        };
    }

    private static string? NormalizeOptional(string? value, int maximumLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"{parameterName} cannot exceed {maximumLength} characters.", parameterName);
        return normalized;
    }

    private static string NormalizeTemplateId(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("TemplateId must be a 1-64 character ASCII alphanumeric slug.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeRevision(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Revision must be a 1-64 character immutable revision token.", nameof(value));
        }

        return normalized;
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        if (cursor.Length > MaximumCursorLength)
            throw new ArgumentException("Cursor is invalid.", nameof(cursor));

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (!decoded.StartsWith(CursorPrefix, StringComparison.Ordinal) ||
                !int.TryParse(decoded[CursorPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
                offset < 0)
            {
                throw new FormatException();
            }

            return offset;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new ArgumentException("Cursor is invalid.", nameof(cursor));
        }
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(CursorPrefix + offset.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string ComputeETag(
        string? query,
        string? category,
        int offset,
        int pageSize,
        int matchingCount,
        IReadOnlyList<EmbeddedWorkflowTemplateRegistration> registrations)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, query ?? string.Empty);
        Append(hash, category ?? string.Empty);
        Append(hash, offset.ToString(CultureInfo.InvariantCulture));
        Append(hash, pageSize.ToString(CultureInfo.InvariantCulture));
        Append(hash, matchingCount.ToString(CultureInfo.InvariantCulture));
        foreach (var registration in registrations)
        {
            Append(hash, registration.TemplateId);
            Append(hash, registration.Revision);
            Append(hash, registration.ProductOrder.ToString(CultureInfo.InvariantCulture));
            Append(hash, registration.Title.EnUS);
            Append(hash, registration.Title.ZhCN);
            Append(hash, registration.Summary.EnUS);
            Append(hash, registration.Summary.ZhCN);
            Append(hash, registration.Description.EnUS);
            Append(hash, registration.Description.ZhCN);
            Append(hash, registration.Category);
            foreach (var tag in registration.Tags)
                Append(hash, tag);
            Append(hash, registration.ExpectedIO.Input.EnUS);
            Append(hash, registration.ExpectedIO.Input.ZhCN);
            Append(hash, registration.ExpectedIO.Output.EnUS);
            Append(hash, registration.ExpectedIO.Output.ZhCN);
            Append(hash, registration.WorkflowYaml);
            Append(hash, registration.Requirements.WorkflowSchemaVersion);
            foreach (var requiredPrimitive in registration.Requirements.RequiredPrimitives)
                Append(hash, requiredPrimitive);
            Append(hash, registration.Requirements.RequiresDefaultLLMRoute.ToString(CultureInfo.InvariantCulture));
            Append(hash, registration.Requirements.RequiresHumanInteraction.ToString(CultureInfo.InvariantCulture));
            Append(hash, registration.Compatibility.Status.ToString());
            Append(hash, registration.Compatibility.Reason.ToString());
        }

        return $"\"{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}\"";
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static void ValidateRegistrationIdentities(
        IReadOnlyList<EmbeddedWorkflowTemplateRegistration> registrations)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var templateId = NormalizeTemplateId(registration.TemplateId);
            var revision = NormalizeRevision(registration.Revision);
            if (!identities.Add(templateId + "\u001f" + revision))
            {
                throw new InvalidOperationException(
                    $"Embedded workflow template '{templateId}' revision '{revision}' is registered more than once.");
            }
        }
    }
}
