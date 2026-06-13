using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class RedisWorkflowWebhookReplayStore : IWorkflowWebhookReplayStore
{
    private const string CompleteScript = """
        local current = redis.call('GET', KEYS[1])
        if not current then
            return 0
        end
        if current == ARGV[1] then
            redis.call('SET', KEYS[1], ARGV[2], 'KEEPTTL')
            return 1
        end
        return 0
        """;

    private const string ReleaseScript = """
        local current = redis.call('GET', KEYS[1])
        if not current then
            return 0
        end
        if current == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IDatabase _database;
    private readonly WorkflowWebhookIngressOptions _options;

    internal RedisWorkflowWebhookReplayStore(
        WorkflowWebhookReplayRedisConnection connection,
        IOptions<WorkflowWebhookIngressOptions> options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        var database = _options.RedisDatabase < 0 ? -1 : _options.RedisDatabase;
        _database = connection.GetDatabase(database);
    }

    internal RedisWorkflowWebhookReplayStore(
        IDatabase database,
        IOptions<WorkflowWebhookIngressOptions> options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options.Value;
    }

    public async ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request);
        var record = ToRecord(request, completed: false);
        var payload = record.ToByteArray();
        var retention = ResolveRetention(_options.ReplayRetentionDays);
        var admitted = await _database.StringSetAsync(
            key,
            payload,
            retention,
            When.NotExists);
        cancellationToken.ThrowIfCancellationRequested();

        if (admitted)
        {
            return new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.Admitted,
                request.CommandId,
                request.CorrelationId);
        }

        var existingPayload = await _database.StringGetAsync(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (existingPayload.IsNullOrEmpty)
        {
            return new WorkflowWebhookReplayAdmission(WorkflowWebhookReplayAdmissionStatus.ExpiredRejected);
        }

        var existing = WorkflowWebhookReplayRecord.Parser.ParseFrom((byte[])existingPayload!);
        var status = string.Equals(existing.PayloadFingerprint, request.PayloadFingerprint, StringComparison.Ordinal)
            ? existing.Completed
                ? WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted
                : WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress
            : WorkflowWebhookReplayAdmissionStatus.PayloadConflict;
        return new WorkflowWebhookReplayAdmission(
            status,
            existing.CommandId,
            existing.CorrelationId);
    }

    public async ValueTask CompleteAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request);
        var admittedPayload = ToRecord(request, completed: false).ToByteArray();
        var completedPayload = ToRecord(request, completed: true).ToByteArray();
        await _database.ScriptEvaluateAsync(
            CompleteScript,
            [key],
            [(RedisValue)admittedPayload, (RedisValue)completedPayload]);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask ReleaseAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request);
        var payload = ToRecord(request, completed: false).ToByteArray();
        await _database.ScriptEvaluateAsync(
            ReleaseScript,
            [key],
            [(RedisValue)payload]);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private RedisKey BuildKey(WorkflowWebhookReplayAdmissionRequest request)
    {
        var prefix = string.IsNullOrWhiteSpace(_options.RedisKeyPrefix)
            ? "aevatar:workflow:webhook-replay"
            : _options.RedisKeyPrefix.Trim();
        return $"{prefix}:{Escape(request.RouteKey)}:{Escape(request.SourceId)}:{Escape(request.DeliveryId)}";
    }

    private static WorkflowWebhookReplayRecord ToRecord(
        WorkflowWebhookReplayAdmissionRequest request,
        bool completed) =>
        new()
        {
            RouteKey = request.RouteKey,
            SourceId = request.SourceId,
            DeliveryId = request.DeliveryId,
            PayloadFingerprint = request.PayloadFingerprint,
            ReceivedAtUnixMs = request.ReceivedAt.ToUnixTimeMilliseconds(),
            CommandId = request.CommandId,
            CorrelationId = request.CorrelationId,
            Completed = completed,
        };

    private static TimeSpan ResolveRetention(int retentionDays) =>
        TimeSpan.FromDays(retentionDays <= 0 ? 30 : retentionDays);

    private static string Escape(string value) =>
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
}

internal sealed class WorkflowWebhookReplayRedisConnection : IDisposable
{
    private readonly IConnectionMultiplexer _connection;

    public WorkflowWebhookReplayRedisConnection(IOptions<WorkflowWebhookIngressOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Value.RedisConnectionString))
            throw new InvalidOperationException("Workflow webhook replay Redis connection string is required.");

        var redisOptions = ConfigurationOptions.Parse(options.Value.RedisConnectionString);
        redisOptions.AbortOnConnectFail = false;
        _connection = ConnectionMultiplexer.Connect(redisOptions);
    }

    public IDatabase GetDatabase(int database) => _connection.GetDatabase(database);

    public void Dispose() => _connection.Dispose();
}
