using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Schedules;

public sealed class FileWorkflowScheduleStore : IWorkflowScheduleStore
{
    private const int StoreFormatMagic = 0x53574641; // AFWS
    private const int StoreFormatVersion = 1;

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<FileWorkflowScheduleStore> _logger;

    public FileWorkflowScheduleStore(
        IOptions<WorkflowScheduleStoreOptions>? options = null,
        ILogger<FileWorkflowScheduleStore>? logger = null)
    {
        var value = options?.Value ?? new WorkflowScheduleStoreOptions();
        if (string.IsNullOrWhiteSpace(value.StorePath))
            throw new InvalidOperationException("Workflow schedule store path is required.");

        _storePath = Path.GetFullPath(value.StorePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath) ?? ".");
        _logger = logger ?? NullLogger<FileWorkflowScheduleStore>.Instance;
    }

    public async Task<WorkflowScheduleDefinition?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        await _gate.WaitAsync(ct);
        try
        {
            var document = ReadDocument(ct);
            var record = document.Schedules.FirstOrDefault(x =>
                string.Equals(x.ScheduleId, scheduleId, StringComparison.Ordinal));
            return record == null ? null : ToDefinition(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkflowScheduleDefinition>> ListAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ReadDocument(ct).Schedules.Select(ToDefinition).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _gate.WaitAsync(ct);
        try
        {
            var document = ReadDocument(ct);
            if (document.Schedules.Any(x => string.Equals(x.ScheduleId, definition.ScheduleId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Schedule '{definition.ScheduleId}' already exists.");

            document.Schedules.Add(ToRecord(definition));
            WriteDocument(document, ct);
            _logger.LogDebug("Workflow schedule added. scheduleId={ScheduleId}", definition.ScheduleId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _gate.WaitAsync(ct);
        try
        {
            var document = ReadDocument(ct);
            var index = FindScheduleIndex(document, definition.ScheduleId);
            if (index < 0)
                throw new InvalidOperationException($"Schedule '{definition.ScheduleId}' does not exist.");

            document.Schedules[index] = ToRecord(definition);
            WriteDocument(document, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkflowScheduleRunRecord?> GetRunAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await _gate.WaitAsync(ct);
        try
        {
            var record = ReadDocument(ct).Runs.FirstOrDefault(x =>
                string.Equals(x.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            return record == null ? null : ToRun(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddRunAsync(
        WorkflowScheduleRunRecord run,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _gate.WaitAsync(ct);
        try
        {
            var document = ReadDocument(ct);
            if (document.Runs.Any(x => string.Equals(x.IdempotencyKey, run.IdempotencyKey, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Schedule run '{run.IdempotencyKey}' already exists.");

            document.Runs.Add(ToRecord(run));
            WriteDocument(document, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateRunAsync(
        WorkflowScheduleRunRecord run,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _gate.WaitAsync(ct);
        try
        {
            var document = ReadDocument(ct);
            var index = FindRunIndex(document, run.IdempotencyKey);
            if (index < 0)
                throw new InvalidOperationException($"Schedule run '{run.IdempotencyKey}' does not exist.");

            document.Runs[index] = ToRecord(run);
            WriteDocument(document, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkflowScheduleStoreDocument ReadDocument(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_storePath))
            return new WorkflowScheduleStoreDocument();

        using var stream = new FileStream(_storePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
            return new WorkflowScheduleStoreDocument();

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadInt32();
        if (magic != StoreFormatMagic)
            throw new InvalidOperationException("Workflow schedule store has an invalid header.");

        var version = reader.ReadInt32();
        if (version != StoreFormatVersion)
            throw new InvalidOperationException($"Unsupported workflow schedule store format version {version}.");

        var payloadLength = reader.ReadInt32();
        if (payloadLength < 0)
            throw new InvalidOperationException("Workflow schedule store has an invalid payload length.");

        var payload = reader.ReadBytes(payloadLength);
        if (payload.Length != payloadLength)
            throw new InvalidOperationException("Workflow schedule store payload is truncated.");

        return WorkflowScheduleStoreDocument.Parser.ParseFrom(payload);
    }

    private void WriteDocument(
        WorkflowScheduleStoreDocument document,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = document.ToByteArray();
        var tempPath = _storePath + ".tmp";

        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(StoreFormatMagic);
            writer.Write(StoreFormatVersion);
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        File.Move(tempPath, _storePath, overwrite: true);
    }

    private static int FindScheduleIndex(
        WorkflowScheduleStoreDocument document,
        string scheduleId)
    {
        for (var i = 0; i < document.Schedules.Count; i++)
        {
            if (string.Equals(document.Schedules[i].ScheduleId, scheduleId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static int FindRunIndex(
        WorkflowScheduleStoreDocument document,
        string idempotencyKey)
    {
        for (var i = 0; i < document.Runs.Count; i++)
        {
            if (string.Equals(document.Runs[i].IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static WorkflowScheduleDefinition ToDefinition(WorkflowScheduleDefinitionRecord record)
    {
        return new WorkflowScheduleDefinition(
            record.ScheduleId,
            record.Name,
            record.Cron,
            record.Timezone,
            ParseStatus(record.Status),
            ToTarget(record.Target),
            record.CreatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch,
            record.UpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch,
            record.NextFireAtUtc == null ? null : record.NextFireAtUtc.ToDateTimeOffset(),
            ToWakeupLease(record.WakeupLease));
    }

    private static WorkflowScheduleDefinitionRecord ToRecord(WorkflowScheduleDefinition definition)
    {
        var record = new WorkflowScheduleDefinitionRecord
        {
            ScheduleId = definition.ScheduleId,
            Name = definition.Name,
            Cron = definition.Cron,
            Timezone = definition.Timezone,
            Status = definition.Status.ToString(),
            Target = ToRecord(definition.Target),
            CreatedAtUtc = Timestamp.FromDateTimeOffset(definition.CreatedAtUtc.ToUniversalTime()),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(definition.UpdatedAtUtc.ToUniversalTime()),
        };
        if (definition.NextFireAtUtc != null)
            record.NextFireAtUtc = Timestamp.FromDateTimeOffset(definition.NextFireAtUtc.Value.ToUniversalTime());
        if (definition.WakeupLease != null)
            record.WakeupLease = ToRecord(definition.WakeupLease);

        return record;
    }

    private static WorkflowScheduleWakeupLease? ToWakeupLease(WorkflowScheduleWakeupLeaseRecord? record)
    {
        if (record == null || string.IsNullOrWhiteSpace(record.ActorId) || string.IsNullOrWhiteSpace(record.CallbackId))
            return null;

        return new WorkflowScheduleWakeupLease(
            record.ActorId,
            record.CallbackId,
            record.Generation,
            ParseWakeupBackend(record.Backend),
            record.SlotEpoch);
    }

    private static WorkflowScheduleWakeupLeaseRecord ToRecord(WorkflowScheduleWakeupLease lease)
    {
        return new WorkflowScheduleWakeupLeaseRecord
        {
            ActorId = lease.ActorId,
            CallbackId = lease.CallbackId,
            Generation = lease.Generation,
            Backend = lease.Backend.ToString(),
            SlotEpoch = lease.SlotEpoch,
        };
    }

    private static WorkflowScheduleTarget ToTarget(WorkflowScheduleTargetRecord? record)
    {
        record ??= new WorkflowScheduleTargetRecord();
        return new WorkflowScheduleTarget(
            record.Prompt,
            ToSource(record.Source),
            NormalizeEmpty(record.SessionId),
            InputParts: null,
            Annotations: new Dictionary<string, string>(record.Annotations, StringComparer.Ordinal),
            ScopeId: NormalizeEmpty(record.ScopeId),
            Headers: new Dictionary<string, string>(record.Headers, StringComparer.Ordinal));
    }

    private static WorkflowScheduleTargetRecord ToRecord(WorkflowScheduleTarget target)
    {
        var record = new WorkflowScheduleTargetRecord
        {
            Prompt = target.Prompt,
            Source = ToRecord(target.Source),
            SessionId = target.SessionId ?? string.Empty,
            ScopeId = target.ScopeId ?? string.Empty,
        };
        AppendMap(record.Annotations, target.Annotations);
        AppendMap(record.Headers, target.Headers);
        return record;
    }

    private static WorkflowChatSource ToSource(WorkflowScheduleSourceRecord? record)
    {
        record ??= new WorkflowScheduleSourceRecord();
        var workflowName = NormalizeEmpty(record.WorkflowName);
        var actorId = NormalizeEmpty(record.ActorId);
        var workflowYamls = record.WorkflowYamls.Count == 0 ? null : record.WorkflowYamls.ToList();
        return ParseSourceKind(record.Kind) switch
        {
            WorkflowChatSourceKind.CatalogWorkflow =>
                WorkflowChatSource.CatalogWorkflow(workflowName ?? string.Empty),
            WorkflowChatSourceKind.DefinitionActor =>
                WorkflowChatSource.DefinitionActor(actorId ?? string.Empty, workflowName),
            WorkflowChatSourceKind.InlineYamlBundle =>
                WorkflowChatSource.InlineYamlBundle(workflowYamls ?? [], workflowName, actorId),
            WorkflowChatSourceKind.Direct =>
                WorkflowChatSource.Direct(actorId),
            _ => WorkflowChatSource.Direct(actorId),
        };
    }

    private static WorkflowScheduleSourceRecord ToRecord(WorkflowChatSource source)
    {
        var record = new WorkflowScheduleSourceRecord
        {
            Kind = source.Kind.ToString(),
            WorkflowName = source.WorkflowName ?? string.Empty,
            ActorId = source.ActorId ?? string.Empty,
        };
        if (source.WorkflowYamls is { Count: > 0 })
            record.WorkflowYamls.Add(source.WorkflowYamls);

        return record;
    }

    private static WorkflowScheduleRunRecord ToRun(WorkflowScheduleRunRecordEntry record)
    {
        return new WorkflowScheduleRunRecord(
            record.RunRecordId,
            record.ScheduleId,
            record.ScheduledFireAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch,
            record.FiredAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch,
            record.IdempotencyKey,
            ParseFireStatus(record.Status),
            NormalizeEmpty(record.AcceptedCommandId),
            NormalizeEmpty(record.CorrelationId),
            NormalizeEmpty(record.ActorId),
            NormalizeEmpty(record.Error));
    }

    private static WorkflowScheduleRunRecordEntry ToRecord(WorkflowScheduleRunRecord run)
    {
        return new WorkflowScheduleRunRecordEntry
        {
            RunRecordId = run.RunRecordId,
            ScheduleId = run.ScheduleId,
            ScheduledFireAtUtc = Timestamp.FromDateTimeOffset(run.ScheduledFireAtUtc.ToUniversalTime()),
            FiredAtUtc = Timestamp.FromDateTimeOffset(run.FiredAtUtc.ToUniversalTime()),
            IdempotencyKey = run.IdempotencyKey,
            Status = run.Status.ToString(),
            AcceptedCommandId = run.AcceptedCommandId ?? string.Empty,
            CorrelationId = run.CorrelationId ?? string.Empty,
            ActorId = run.ActorId ?? string.Empty,
            Error = run.Error ?? string.Empty,
        };
    }

    private static WorkflowScheduleStatus ParseStatus(string? value) =>
        System.Enum.TryParse<WorkflowScheduleStatus>(value, ignoreCase: true, out var status)
            ? status
            : WorkflowScheduleStatus.Disabled;

    private static WorkflowScheduleFireStatus ParseFireStatus(string? value) =>
        System.Enum.TryParse<WorkflowScheduleFireStatus>(value, ignoreCase: true, out var status)
            ? status
            : WorkflowScheduleFireStatus.Rejected;

    private static WorkflowScheduleWakeupBackend ParseWakeupBackend(string? value) =>
        System.Enum.TryParse<WorkflowScheduleWakeupBackend>(value, ignoreCase: true, out var status)
            ? status
            : WorkflowScheduleWakeupBackend.InMemory;

    private static WorkflowChatSourceKind ParseSourceKind(string? value) =>
        System.Enum.TryParse<WorkflowChatSourceKind>(value, ignoreCase: true, out var status)
            ? status
            : WorkflowChatSourceKind.Direct;

    private static void AppendMap(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is not { Count: > 0 })
            return;

        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(key))
                destination[key] = value ?? string.Empty;
        }
    }

    private static string? NormalizeEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
