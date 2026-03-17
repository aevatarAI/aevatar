using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal sealed class ElasticsearchOptimisticWriter<TReadModel>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    private const int MaxAttempts = 3;
    private const string ProviderName = "Elasticsearch";

    private readonly HttpClient _httpClient;
    private readonly JsonFormatter _formatter;
    private readonly JsonParser _parser;
    private readonly ElasticsearchMissingIndexBehavior _missingIndexBehavior;
    private readonly bool _autoCreateIndex;
    private readonly ILogger _logger;

    public ElasticsearchOptimisticWriter(
        HttpClient httpClient,
        JsonFormatter formatter,
        JsonParser parser,
        bool autoCreateIndex,
        ElasticsearchMissingIndexBehavior missingIndexBehavior,
        ILogger logger)
    {
        _httpClient = httpClient;
        _formatter = formatter;
        _parser = parser;
        _autoCreateIndex = autoCreateIndex;
        _missingIndexBehavior = missingIndexBehavior;
        _logger = logger;
    }

    public async Task<ProjectionWriteResult> UpsertAsync(
        string indexName,
        string keyValue,
        TReadModel readModel,
        CancellationToken ct)
    {
        var payload = _formatter.Format(readModel);
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var existing = await TryGetExistingStateAsync(indexName, keyValue, ct);
                var result = ProjectionWriteResultEvaluator.Evaluate(existing.ReadModel, readModel);
                if (!result.IsApplied)
                {
                    LogWriteSkipped(keyValue, startedAt, result);
                    return result;
                }

                using var request = BuildConditionalUpsertRequest(indexName, keyValue, payload, existing);
                using var response = await _httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    LogWriteCompleted(keyValue, startedAt);
                    return ProjectionWriteResult.Applied();
                }

                if (response.StatusCode != HttpStatusCode.Conflict)
                    await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "upsert", ct);

                _logger.LogInformation(
                    "Projection read-model write hit optimistic concurrency conflict and will re-evaluate. provider={Provider} readModelType={ReadModelType} key={Key} attempt={Attempt}/{MaxAttempts}",
                    ProviderName,
                    typeof(TReadModel).FullName,
                    keyValue,
                    attempt,
                    MaxAttempts);
            }

            var reconciled = await TryGetExistingStateAsync(indexName, keyValue, ct);
            var reconciledResult = ProjectionWriteResultEvaluator.Evaluate(reconciled.ReadModel, readModel);
            if (!reconciledResult.IsApplied)
            {
                LogWriteSkipped(keyValue, startedAt, reconciledResult);
                return reconciledResult;
            }

            throw new InvalidOperationException(
                $"Elasticsearch optimistic concurrency write could not be reconciled for read-model '{typeof(TReadModel).FullName}' key '{keyValue}'.");
        }
        catch (Exception ex)
        {
            LogWriteFailure(keyValue, startedAt, ex);
            throw;
        }
    }

    private async Task<ExistingReadModelState> TryGetExistingStateAsync(
        string indexName,
        string keyValue,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync($"{indexName}/_doc/{Uri.EscapeDataString(keyValue)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var notFoundPayload = await response.Content.ReadAsStringAsync(ct);
            if (ElasticsearchProjectionDocumentStoreHttpSupport.IsIndexNotFoundPayload(notFoundPayload))
            {
                if (_autoCreateIndex || _missingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw)
                    throw new InvalidOperationException(
                        $"Elasticsearch index '{indexName}' was not found during 'get' for read-model '{typeof(TReadModel).FullName}'.");

                return ExistingReadModelState.Missing;
            }

            return ExistingReadModelState.Missing;
        }

        await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "get", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        var seqNo = TryReadLong(jsonDoc.RootElement, "_seq_no");
        var primaryTerm = TryReadLong(jsonDoc.RootElement, "_primary_term");
        if (!jsonDoc.RootElement.TryGetProperty("_source", out var sourceNode))
            return new ExistingReadModelState(null, seqNo, primaryTerm);

        return new ExistingReadModelState(DeserializeOrNull(sourceNode.GetRawText()), seqNo, primaryTerm);
    }

    private static HttpRequestMessage BuildConditionalUpsertRequest(
        string indexName,
        string keyValue,
        string payload,
        ExistingReadModelState existing)
    {
        var requestPath = existing.ReadModel == null
            ? $"{indexName}/_create/{Uri.EscapeDataString(keyValue)}"
            : $"{indexName}/_doc/{Uri.EscapeDataString(keyValue)}?if_seq_no={existing.SeqNo}&if_primary_term={existing.PrimaryTerm}";
        return new HttpRequestMessage(HttpMethod.Put, requestPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    private static long TryReadLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
            return -1;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(property.GetString(), out var parsed) => parsed,
            _ => -1,
        };
    }

    private TReadModel? DeserializeOrNull(string json)
    {
        try
        {
            return _parser.Parse<TReadModel>(json);
        }
        catch
        {
            return null;
        }
    }

    private void LogWriteCompleted(string keyValue, DateTimeOffset startedAt)
    {
        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogInformation(
            "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
            ProviderName,
            typeof(TReadModel).FullName,
            keyValue,
            elapsedMs,
            ProjectionWriteDisposition.Applied);
    }

    private void LogWriteSkipped(string keyValue, DateTimeOffset startedAt, ProjectionWriteResult result)
    {
        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogInformation(
            "Projection read-model write skipped. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
            ProviderName,
            typeof(TReadModel).FullName,
            keyValue,
            elapsedMs,
            result.Disposition);
    }

    private void LogWriteFailure(string keyValue, DateTimeOffset startedAt, Exception ex)
    {
        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogError(
            ex,
            "Projection read-model write failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
            ProviderName,
            typeof(TReadModel).FullName,
            keyValue,
            elapsedMs,
            "failed",
            ex.GetType().Name);
    }

    internal sealed record ExistingReadModelState(
        TReadModel? ReadModel,
        long SeqNo,
        long PrimaryTerm)
    {
        public static ExistingReadModelState Missing { get; } = new(null, -1, -1);
    }
}
