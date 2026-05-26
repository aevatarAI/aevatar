using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class ResponsesAevatarToolProvider : IResponsesToolProvider
{
    private readonly IResponsesAgentToolStateCommandPort _commandPort;
    private readonly IResponsesAgentToolStateQueryPort _queryPort;
    private readonly IWebApiClient _webClient;
    private readonly WebToolOptions _webOptions;

    public ResponsesAevatarToolProvider(
        IResponsesAgentToolStateCommandPort commandPort,
        IResponsesAgentToolStateQueryPort queryPort,
        IWebApiClient webClient,
        WebToolOptions webOptions)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _webClient = webClient ?? throw new ArgumentNullException(nameof(webClient));
        _webOptions = webOptions ?? throw new ArgumentNullException(nameof(webOptions));
    }

    public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<IAgentTool>>(
        [
            new TodoWriteTool(_commandPort),
            new TaskTool("Task", _commandPort),
            new TaskTool("task", _commandPort),
            new WebFetchTool("WebFetch", _commandPort, _queryPort, _webClient),
            new WebFetchTool("web_fetch", _commandPort, _queryPort, _webClient),
            new WebSearchTool("WebSearch", _commandPort, _queryPort, _webClient, _webOptions),
            new WebSearchTool("web_search", _commandPort, _queryPort, _webClient, _webOptions),
        ]);

    private abstract class ResponsesStateTool : IAgentTool
    {
        public abstract string Name { get; }

        public abstract string Description { get; }

        public abstract string ParametersSchema { get; }

        public virtual bool IsReadOnly => false;

        public abstract Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);

        protected static ResponsesToolExecutionScope ResolveScope()
        {
            var scopeId = AgentToolRequestContext.ScopeId;
            var ownerSubject = AgentToolRequestContext.OwnerSubject;
            var responseId = AgentToolRequestContext.ResponseId
                             ?? AgentToolRequestContext.RequestId;
            if (string.IsNullOrWhiteSpace(scopeId) ||
                string.IsNullOrWhiteSpace(ownerSubject) ||
                string.IsNullOrWhiteSpace(responseId))
            {
                throw new InvalidOperationException(
                    "Responses substitute tools require scope_id, owner_subject, and response_id in request context.");
            }

            return new ResponsesToolExecutionScope(scopeId.Trim(), ownerSubject.Trim(), responseId.Trim());
        }

        protected static string? GetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        protected static string ComputeCacheKey(string toolName, string value)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{toolName}\n{value.Trim().ToLowerInvariant()}"));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    private sealed record ResponsesToolExecutionScope(
        string ScopeId,
        string OwnerSubject,
        string ResponseId);

    private sealed class TodoWriteTool : ResponsesStateTool
    {
        private readonly IResponsesAgentToolStateCommandPort _commandPort;

        public TodoWriteTool(IResponsesAgentToolStateCommandPort commandPort)
        {
            _commandPort = commandPort;
        }

        public override string Name => "TodoWrite";

        public override string Description =>
            "Persist the agent-scoped todo list in Aevatar so it is visible across sessions.";

        public override string ParametersSchema => """
            {
              "type": "object",
              "properties": {
                "todos": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" },
                      "content": { "type": "string" },
                      "status": { "type": "string" }
                    },
                    "required": ["content", "status"]
                  }
                }
              },
              "required": ["todos"]
            }
            """;

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var scope = ResolveScope();
            var result = await _commandPort.ApplyTodoWriteAsync(
                scope.ScopeId,
                scope.OwnerSubject,
                scope.ResponseId,
                argumentsJson,
                ct);

            return JsonSerializer.Serialize(new
            {
                status = "stored",
                actor_id = result.ActorId,
                todo_count = result.Todos.Count,
                todos = result.Todos.Select(static todo => new
                {
                    id = todo.Id,
                    content = todo.Content,
                    status = todo.Status,
                }).ToArray(),
            });
        }
    }

    private sealed class TaskTool : ResponsesStateTool
    {
        private readonly string _name;
        private readonly IResponsesAgentToolStateCommandPort _commandPort;

        public TaskTool(string name, IResponsesAgentToolStateCommandPort commandPort)
        {
            _name = name;
            _commandPort = commandPort;
        }

        public override string Name => _name;

        public override string Description =>
            "Record an Aevatar sub-agent task dispatch in agent-scoped topology state.";

        public override string ParametersSchema => """
            {
              "type": "object",
              "properties": {
                "description": { "type": "string" },
                "prompt": { "type": "string" },
                "subagent_type": { "type": "string" }
              }
            }
            """;

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var scope = ResolveScope();
            var result = await _commandPort.RecordTaskAsync(
                scope.ScopeId,
                scope.OwnerSubject,
                scope.ResponseId,
                argumentsJson,
                ct);
            return result.ResultJson;
        }
    }

    private sealed class WebFetchTool : ResponsesStateTool
    {
        private readonly string _name;
        private readonly IResponsesAgentToolStateCommandPort _commandPort;
        private readonly IResponsesAgentToolStateQueryPort _queryPort;
        private readonly IWebApiClient _webClient;

        public WebFetchTool(
            string name,
            IResponsesAgentToolStateCommandPort commandPort,
            IResponsesAgentToolStateQueryPort queryPort,
            IWebApiClient webClient)
        {
            _name = name;
            _commandPort = commandPort;
            _queryPort = queryPort;
            _webClient = webClient;
        }

        public override string Name => _name;

        public override string Description =>
            "Fetch a URL through Aevatar, trace the result, and reuse cached content across sessions.";

        public override string ParametersSchema => """
            {
              "type": "object",
              "properties": {
                "url": {
                  "type": "string",
                  "description": "The URL to fetch content from."
                },
                "extract_hint": {
                  "type": "string",
                  "description": "Optional hint for what information to focus on."
                }
              },
              "required": ["url"]
            }
            """;

        public override bool IsReadOnly => true;

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var scope = ResolveScope();
            var validation = WebFetchUrlGuard.Validate(NormalizeUrl(ExtractUrl(argumentsJson)));
            if (!validation.IsAllowed)
            {
                return JsonSerializer.Serialize(new
                {
                    error = validation.RejectionCode ?? "url_rejected",
                });
            }

            var url = validation.NormalizedUrl!;
            var cacheKey = ComputeCacheKey(Name, url);
            var cached = await _queryPort.GetWebCacheEntryAsync(
                scope.ScopeId,
                scope.OwnerSubject,
                Name,
                cacheKey,
                ct);
            if (cached != null)
            {
                await RecordTraceAsync(scope, cacheKey, url, query: string.Empty, cacheHit: true, cached.ResultJson, ct);
                return cached.ResultJson;
            }

            // The URL came from the LLM (and ultimately from upstream prompts/user
            // input). Forwarding the caller's NyxID bearer to that target would
            // let a prompt exfiltrate the token to an attacker-controlled host.
            // WebFetch is unauthenticated by design — anything that needs auth
            // must route through a typed NyxID-proxied tool.
            var result = await _webClient.FetchUrlAsync(token: string.Empty, url, ct);
            var resultJson = JsonSerializer.Serialize(new
            {
                url = result.OriginalUrl,
                status_code = result.StatusCode,
                content_type = result.ContentType,
                content = result.Body ?? string.Empty,
                redirect_url = result.RedirectUrl,
            });
            await RecordTraceAsync(scope, cacheKey, url, query: string.Empty, cacheHit: false, resultJson, ct);
            return resultJson;
        }

        private Task RecordTraceAsync(
            ResponsesToolExecutionScope scope,
            string cacheKey,
            string url,
            string query,
            bool cacheHit,
            string resultJson,
            CancellationToken ct) =>
            _commandPort.RecordWebTraceAsync(
                scope.ScopeId,
                scope.OwnerSubject,
                scope.ResponseId,
                new ResponsesWebTraceInput(
                    ResponseAgentToolStateIds.NewWebTraceId(),
                    Name,
                    cacheKey,
                    url,
                    query,
                    cacheHit,
                    resultJson),
                ct);

        private static string? ExtractUrl(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? GetString(document.RootElement, "url")
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? NormalizeUrl(string? url)
        {
            var normalized = url?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return null;
            if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "https://" + normalized[7..];
            return normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : "https://" + normalized;
        }
    }

    private sealed class WebSearchTool : ResponsesStateTool
    {
        private readonly string _name;
        private readonly IResponsesAgentToolStateCommandPort _commandPort;
        private readonly IResponsesAgentToolStateQueryPort _queryPort;
        private readonly IWebApiClient _webClient;
        private readonly WebToolOptions _webOptions;

        public WebSearchTool(
            string name,
            IResponsesAgentToolStateCommandPort commandPort,
            IResponsesAgentToolStateQueryPort queryPort,
            IWebApiClient webClient,
            WebToolOptions webOptions)
        {
            _name = name;
            _commandPort = commandPort;
            _queryPort = queryPort;
            _webClient = webClient;
            _webOptions = webOptions;
        }

        public override string Name => _name;

        public override string Description =>
            "Search the web through Aevatar, trace the result, and reuse cached results across sessions.";

        public override string ParametersSchema => """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string" },
                "max_results": { "type": "integer" }
              },
              "required": ["query"]
            }
            """;

        public override bool IsReadOnly => true;

        public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var scope = ResolveScope();
            var query = ExtractQuery(argumentsJson);
            if (string.IsNullOrWhiteSpace(query))
                return """{"error":"'query' is required"}""";

            var maxResults = ExtractMaxResults(argumentsJson) ?? _webOptions.MaxSearchResults;
            maxResults = Math.Clamp(maxResults, 1, 20);
            var cacheValue = $"{query.Trim()}\n{maxResults}";
            var cacheKey = ComputeCacheKey(Name, cacheValue);
            var cached = await _queryPort.GetWebCacheEntryAsync(
                scope.ScopeId,
                scope.OwnerSubject,
                Name,
                cacheKey,
                ct);
            if (cached != null)
            {
                await RecordTraceAsync(scope, cacheKey, query, cacheHit: true, cached.ResultJson, ct);
                return cached.ResultJson;
            }

            var token = AgentToolRequestContext.NyxIdAccessToken ?? string.Empty;
            var resultJson = string.IsNullOrWhiteSpace(token)
                ? """{"error":"No NyxID access token available. User must be authenticated."}"""
                : await _webClient.SearchAsync(token, query.Trim(), maxResults, ct);
            await RecordTraceAsync(scope, cacheKey, query, cacheHit: false, resultJson, ct);
            return resultJson;
        }

        private Task RecordTraceAsync(
            ResponsesToolExecutionScope scope,
            string cacheKey,
            string query,
            bool cacheHit,
            string resultJson,
            CancellationToken ct) =>
            _commandPort.RecordWebTraceAsync(
                scope.ScopeId,
                scope.OwnerSubject,
                scope.ResponseId,
                new ResponsesWebTraceInput(
                    ResponseAgentToolStateIds.NewWebTraceId(),
                    Name,
                    cacheKey,
                    string.Empty,
                    query,
                    cacheHit,
                    resultJson),
                ct);

        private static string? ExtractQuery(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? GetString(document.RootElement, "query")
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int? ExtractMaxResults(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("max_results", out var value))
                {
                    return null;
                }

                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
                    ? parsed
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
