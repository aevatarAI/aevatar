using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class ResponsesAevatarToolProvider : IResponsesToolProvider, IAgentToolSource
{
    private readonly IResponsesAgentToolStateCommandPort _commandPort;
    private readonly ResponsesWebSubstituteToolExecutionService _webExecution;

    public ResponsesAevatarToolProvider(
        IResponsesAgentToolStateCommandPort commandPort,
        ResponsesWebSubstituteToolExecutionService webExecution)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _webExecution = webExecution ?? throw new ArgumentNullException(nameof(webExecution));
    }

    public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<IAgentTool>>(
        [
            new TodoWriteTool(_commandPort),
            // Refactor (iter159/cluster-624):
            //   Old pattern: Host layer owned WebFetch/WebSearch execution, cache lookup, and trace recording
            //   New principle: Host registers TodoWrite plus WebFetch/WebSearch substitutes and delegates Web orchestration to Application
            new WebFetchTool("WebFetch", _webExecution),
            new WebFetchTool("web_fetch", _webExecution),
            new WebSearchTool("WebSearch", _webExecution),
            new WebSearchTool("web_search", _webExecution),
        ]);

    public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<IAgentTool>>([]);

    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var context = new ResponsesToolProviderContext(
            AgentToolExecutionContext.Empty with
            {
                Channel = new AgentToolChannelContext(
                    LlmSessionOriginKind.ApiKey.ToString(),
                    null,
                    null,
                    null,
                    null),
            });
        var tools = new List<IAgentTool>();
        tools.AddRange(await GetSubstituteToolsAsync(context, ct).ConfigureAwait(false));
        tools.AddRange(await GetAdditiveToolsAsync(context, ct).ConfigureAwait(false));
        return tools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

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

        protected static ResponsesWebSubstituteToolExecutionRequest BuildWebRequest(
            string toolName,
            ResponsesWebFetchToolInput input)
        {
            var scope = ResolveScope();
            return new ResponsesWebSubstituteToolExecutionRequest
            {
                ToolName = toolName,
                ScopeId = scope.ScopeId,
                OwnerSubject = scope.OwnerSubject,
                ResponseId = scope.ResponseId,
                NyxIdAccessToken = AgentToolRequestContext.AccessToken ?? string.Empty,
                Fetch = input,
            };
        }

        protected static ResponsesWebSubstituteToolExecutionRequest BuildWebRequest(
            string toolName,
            ResponsesWebSearchToolInput input)
        {
            var scope = ResolveScope();
            return new ResponsesWebSubstituteToolExecutionRequest
            {
                ToolName = toolName,
                ScopeId = scope.ScopeId,
                OwnerSubject = scope.OwnerSubject,
                ResponseId = scope.ResponseId,
                NyxIdAccessToken = AgentToolRequestContext.AccessToken ?? string.Empty,
                Search = input,
            };
        }
    }

    private sealed record ResponsesToolExecutionScope(string ScopeId, string OwnerSubject, string ResponseId);

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

    private sealed class WebFetchTool : ResponsesStateTool
    {
        private readonly string _name;
        private readonly ResponsesWebSubstituteToolExecutionService _webExecution;

        public WebFetchTool(
            string name,
            ResponsesWebSubstituteToolExecutionService webExecution)
        {
            _name = name;
            _webExecution = webExecution;
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
            var input = ResponsesWebSubstituteToolJson.ParseFetchInput(argumentsJson);
            var result = await _webExecution.ExecuteAsync(BuildWebRequest(Name, input), ct)
                .ConfigureAwait(false);
            return ResponsesWebSubstituteToolJson.ToBoundaryJson(result);
        }
    }

    private sealed class WebSearchTool : ResponsesStateTool
    {
        private readonly string _name;
        private readonly ResponsesWebSubstituteToolExecutionService _webExecution;

        public WebSearchTool(
            string name,
            ResponsesWebSubstituteToolExecutionService webExecution)
        {
            _name = name;
            _webExecution = webExecution;
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
            var input = ResponsesWebSubstituteToolJson.ParseSearchInput(argumentsJson);
            var result = await _webExecution.ExecuteAsync(BuildWebRequest(Name, input), ct)
                .ConfigureAwait(false);
            return ResponsesWebSubstituteToolJson.ToBoundaryJson(result);
        }
    }
}
