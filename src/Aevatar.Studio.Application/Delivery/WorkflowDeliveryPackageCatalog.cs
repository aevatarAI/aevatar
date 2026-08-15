using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using ContractAcceptanceMode = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceMode;

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

public sealed class WorkflowDeliveryPackageCatalog : IWorkflowDeliveryPackageCatalog
{
    private static readonly IReadOnlyDictionary<string, PackageDefinition> Definitions =
        BuildDefinitions();

    private readonly IWorkflowDefinitionCatalog _workflowCatalog;
    private readonly IWorkflowDefinitionParser _parser;
    private readonly IOptions<WorkflowDeliveryOptions> _options;
    private readonly TimeProvider _timeProvider;

    public WorkflowDeliveryPackageCatalog(
        IWorkflowDefinitionCatalog workflowCatalog,
        IWorkflowDefinitionParser parser,
        IOptions<WorkflowDeliveryOptions> options,
        TimeProvider timeProvider)
    {
        _workflowCatalog = workflowCatalog ?? throw new ArgumentNullException(nameof(workflowCatalog));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<WorkflowPackageVersionSnapshot>> ListAsync(
        string createdBy,
        CancellationToken ct = default)
    {
        var names = GetAllowedWorkflowNames();
        var packages = new List<WorkflowPackageVersionSnapshot>(names.Count);
        foreach (var name in names)
            packages.Add(await GetAsync(name, createdBy, ct));
        return packages;
    }

    public async Task<WorkflowPackageVersionSnapshot> GetAsync(
        string workflowName,
        string createdBy,
        CancellationToken ct = default)
    {
        var normalizedName = NormalizeRequired(workflowName, nameof(workflowName));
        if (!GetAllowedWorkflowNames().Contains(normalizedName, StringComparer.Ordinal))
            throw new WorkflowDeliveryPackageNotAllowedException(normalizedName);
        if (!Definitions.TryGetValue(normalizedName, out var definition))
            throw new WorkflowDeliveryPackageNotAllowedException(normalizedName);

        var registration = _workflowCatalog.GetDefinition(normalizedName)
            ?? throw new WorkflowDeliveryPackageUnavailableException(
                normalizedName,
                $"Allowlisted workflow '{normalizedName}' is not loaded in the workflow definition catalog.");
        var sourceYaml = registration.WorkflowYaml;
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
        var acceptancePolicy = WorkflowDeliveryAcceptancePolicies.Resolve(normalizedName);
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
                Mode = acceptancePolicy.Mode switch
                {
                    ContractAcceptanceMode.AutomaticPreview => WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                    ContractAcceptanceMode.Manual => WorkflowDeliveryAcceptanceMode.Manual,
                    _ => WorkflowDeliveryAcceptanceMode.Unspecified,
                },
                Limitation = acceptancePolicy.Limitation ?? string.Empty,
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

    private IReadOnlyList<string> GetAllowedWorkflowNames()
    {
        var names = (_options.Value.AllowedWorkflowNames ?? [])
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
            throw new InvalidOperationException("Aevatar:Delivery:AllowedWorkflowNames must not be empty.");
        if (names.Any(name => !Definitions.ContainsKey(name)))
            throw new InvalidOperationException("Delivery allowlist contains an unsupported workflow package.");
        return names;
    }

    private static WorkflowDeliveryVariableDefinition ToProto(VariableDefinition value) =>
        new()
        {
            Key = value.Key,
            Label = value.Label,
            Description = value.Description,
            Kind = value.Kind,
            Required = value.Required,
            YamlPointer = value.YamlPointer,
            JsonPointer = value.JsonPointer,
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

    private static IReadOnlyDictionary<string, PackageDefinition> BuildDefinitions()
    {
        var lark = new[] { new ConnectionSlotDefinition("lark", "Lark workspace", "api-lark-bot", true) };
        return new Dictionary<string, PackageDefinition>(StringComparer.Ordinal)
        {
            ["hr_onboarding_email_approval"] = new(
                "HR onboarding email approval",
                "Create an onboarding mailbox approval with duplicate checks and deterministic evidence.",
                "Writes an approval only when the installed workflow is run in submit mode.",
                ["lark.base.read", "lark.approval.read", "lark.approval.write"],
                [
                    V("companyDomain", "Company domain", "Domain used for generated mailboxes.", "/steps/2/parameters/value", "/company_domain", "aelf.io"),
                    V("approvalCode", "Approval code", "Lark approval definition code.", "/steps/2/parameters/value", "/approval_code", "9C330885-C70A-4A5D-913A-CBA9A142FFD4"),
                    V("onboardingAppToken", "Employee Base app", "Employee Master Base app token.", "/steps/2/parameters/value", "/onboarding_app_token", "Xb5wb7YFqaCf4Ysuq2el3bVPgNe"),
                    V("onboardingTableId", "Employee table", "Employee Master table ID.", "/steps/2/parameters/value", "/onboarding_table_id", "tblxilSWWYTzV7al"),
                ],
                lark),
            ["hr_monthly_attendance_approval"] = new(
                "Monthly attendance approval",
                "Aggregate the monthly attendance Base and create the reviewed approval and notification.",
                "Reads attendance data and can create an approval and notification in submit mode.",
                ["lark.base.read", "lark.approval.write", "lark.message.write"],
                [
                    V("attendanceAppToken", "Attendance Base app", "Attendance Base app token.", "/steps/1/parameters/value", "/attendance_app_token", "MwIRb3h5hauYvcsA28kl8FGfgjg"),
                    V("attendanceTableId", "Attendance table", "Attendance table ID.", "/steps/1/parameters/value", "/attendance_table_id", "tblTDqjSDKffQ7cm"),
                    V("approvalCode", "Approval code", "Monthly attendance approval definition code.", "/steps/1/parameters/value", "/approval_code", "3F02FB04-3919-4089-B42B-B1B557820EB5"),
                    V("submitterUserId", "Submitter user", "Lark user ID that submits the approval.", "/steps/1/parameters/value", "/submitter_user_id", "ee689459"),
                    V("notifyUserId", "Notification user", "Lark user ID that receives the completion card.", "/steps/1/parameters/value", "/notify_user_id", "831cg5af"),
                ],
                lark),
            ["hr_attendance_fill_reminder"] = new(
                "Attendance completion reminder",
                "Send a reviewed attendance completion reminder card before month end.",
                "Sends one Lark message only when the installed workflow is run in submit mode.",
                ["lark.message.write"],
                [
                    V("notifyUserId", "Notification user", "Lark user ID that receives the reminder.", "/steps/1/parameters/value", "/notify_user_id", "831cg5af"),
                    V("documentUrl", "Attendance document", "URL opened from the reminder card.", "/steps/1/parameters/value", "/doc_url", "https://aelfblockchain.sg.larksuite.com/base/MwIRb3h5hauYvcsA28kl8FGfgjg?table=tblTDqjSDKffQ7cm&view=vewfR8fiQS"),
                    V("submitTimeNote", "Reminder note", "Deadline note displayed in the reminder card.", "/steps/1/parameters/value", "/submit_time_note", "月末最后一天 10:00 前请完成核对并运行月度考勤审批"),
                ],
                lark),
            ["fin_invoice_precheck_approval"] = new(
                "Invoice precheck and approval",
                "Extract invoice evidence, validate it, check duplicates, and prepare the payment approval.",
                "Uses document extraction and can create a payment approval in submit mode.",
                ["document.extract", "lark.approval.read", "lark.approval.write"],
                [
                    V("approvalCode", "Approval code", "Payment approval definition code.", "/steps/1/parameters/value", "/approval_code", "F640097D-0A68-47C1-A7BC-86659BC4B06F"),
                    V("defaultSubmitterEmail", "Default submitter", "Fallback submitter email.", "/steps/1/parameters/value", "/default_submitter_email", "jianwei.su@aelf.io"),
                ],
                [new ConnectionSlotDefinition("lark", "Lark workspace", "api-lark-bot-2", true)]),
            ["fin_budget_variance_monitor"] = new(
                "Budget variance monitor",
                "Read four finance tables, compute deterministic variance bands, and optionally notify Lark.",
                "Reads finance Base tables and can send a summary card in submit mode.",
                ["lark.base.read", "lark.message.write"],
                [
                    V("appToken", "Finance Base app", "Finance Base app token.", "/steps/2/parameters/value", "/app_token", "L80RbTPAjaiZpOsaYo3lnmYCg3s"),
                    V("coreActualTableId", "Core actuals table", "Core business actual-spend table ID.", "/steps/2/parameters/value", "/core_actual_table_id", "tblVuALMhd7WGu9k"),
                    V("coreBudgetTableId", "Core budget table", "Core business budget table ID.", "/steps/2/parameters/value", "/core_budget_table_id", "tblMtwgzEhW7yWTq"),
                    V("aelfActualTableId", "aelf actuals table", "aelf BU actual-spend table ID.", "/steps/2/parameters/value", "/aelf_actual_table_id", "tbl3lXCuWOSlaiR5"),
                    V("aelfBudgetTableId", "aelf budget table", "aelf BU budget table ID.", "/steps/2/parameters/value", "/aelf_budget_table_id", "tblVkaJLvAjLHARJ"),
                    V("watchPercent", "Watch threshold", "Budget utilization percentage for watch status.", "/steps/2/parameters/value", "/watch_pct", "80", WorkflowDeliveryVariableKind.Number),
                    V("warningPercent", "Warning threshold", "Budget utilization percentage for warning status.", "/steps/2/parameters/value", "/warning_pct", "100", WorkflowDeliveryVariableKind.Number),
                    V("overPercent", "Over threshold", "Budget utilization percentage for over status.", "/steps/2/parameters/value", "/over_pct", "120", WorkflowDeliveryVariableKind.Number),
                    V("notifyOpenId", "Notification recipient", "Lark open ID that receives the summary.", "/steps/2/parameters/value", "/notify_open_id", "ou_3d9067006e9fb8eb8e5fc7b2bb4c6264"),
                ],
                lark),
        };
    }

    private static VariableDefinition V(
        string key,
        string label,
        string description,
        string yamlPointer,
        string jsonPointer,
        string defaultValue,
        WorkflowDeliveryVariableKind kind = WorkflowDeliveryVariableKind.String) =>
        new(key, label, description, kind, true, yamlPointer, jsonPointer, defaultValue);

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0
            ? throw new InvalidOperationException($"{name} is required.")
            : normalized;
    }

    private sealed record PackageDefinition(
        string DisplayName,
        string Description,
        string RiskSummary,
        IReadOnlyList<string> Capabilities,
        IReadOnlyList<VariableDefinition> Variables,
        IReadOnlyList<ConnectionSlotDefinition> ConnectionSlots);

    private sealed record VariableDefinition(
        string Key,
        string Label,
        string Description,
        WorkflowDeliveryVariableKind Kind,
        bool Required,
        string YamlPointer,
        string JsonPointer,
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
