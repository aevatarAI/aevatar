# Lark Docs Knowledge Assistant Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the supplied-context enterprise knowledge starter with a workflow that searches and reads the caller's Lark Docs/Wiki content, then answers questions or performs structured extraction with source links.

**Architecture:** Add a focused read-only Lark knowledge client and `lark_docs_search` agent tool on top of the existing NyxID proxy and workflow tool adapter. The workflow visibly plans a bounded search query, invokes that tool, and passes its normalized evidence envelope to a tool-free LLM role that selects answer or extraction presentation from the original request.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, Google.Protobuf-backed workflow contracts, YAML workflow definitions, existing NyxID proxy client.

---

## File Map

- Create `src/Aevatar.AI.ToolProviders.Lark/ILarkKnowledgeClient.cs`: narrow transport contract and request records for Docs/Wiki search, Wiki node resolution, and Docx raw-content reads.
- Create `src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeNyxClient.cs`: NyxID-backed Lark endpoint adapter.
- Create `src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeResponseParser.cs`: provider-response normalization into stable internal search/read models.
- Create `src/Aevatar.AI.ToolProviders.Lark/Tools/LarkDocsSearchTool.cs`: bounded composite retrieval tool that produces the evidence envelope.
- Create `src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeAgentToolSource.cs`: discovery source for the knowledge tool.
- Modify `src/Aevatar.AI.ToolProviders.Lark/LarkToolOptions.cs`: add an enable flag for Docs/Wiki retrieval.
- Modify `src/Aevatar.AI.ToolProviders.Lark/ServiceCollectionExtensions.cs`: register the client and tool source.
- Create `test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs`: transport, parsing, retrieval, limits, failures, and registration tests.
- Modify `workflow-templates/enterprise_knowledge_assistant.yaml`: replace supplied-context semantics with the visible Lark retrieval flow.
- Create `test/Aevatar.Workflow.Host.Api.Tests/EnterpriseKnowledgeAssistantTemplateTests.cs`: parse and assert the starter's durable product semantics.
- Keep `docs/superpowers/specs/2026-08-31-lark-docs-knowledge-assistant-design.md` and this plan as implementation records.

### Task 1: Define And Shape The Lark Knowledge Transport

**Files:**

- Create: `test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs`
- Create: `src/Aevatar.AI.ToolProviders.Lark/ILarkKnowledgeClient.cs`
- Create: `src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeNyxClient.cs`

- [ ] **Step 1: Write failing transport tests**

Add focused tests using a recording `HttpMessageHandler`:

```csharp
[Fact]
public async Task SearchAsync_ShouldUseSearchV2WithDocAndWikiFilters()
{
    var (client, handler) = CreateClient("""{"code":0,"data":{"res_units":[]}}""");

    await client.SearchAsync(
        "token-123",
        new LarkKnowledgeSearchRequest("expense policy", 5, []),
        CancellationToken.None);

    handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
    handler.LastRequest.RequestUri!.ToString().Should().EndWith(
        "/api/v1/proxy/s/api-lark-bot/open-apis/search/v2/doc_wiki/search");
    using var body = JsonDocument.Parse(handler.LastBody!);
    body.RootElement.GetProperty("query").GetString().Should().Be("expense policy");
    body.RootElement.GetProperty("doc_filter").GetProperty("doc_types")
        .EnumerateArray().Select(static item => item.GetString()).Should().Contain(["DOCX", "WIKI"]);
}

[Fact]
public async Task ReadDocxRawContentAsync_ShouldUseEscapedDocumentToken()
{
    var (client, handler) = CreateClient("""{"code":0,"data":{"content":"policy text"}}""");

    await client.ReadDocxRawContentAsync("token-123", "doccn/a", CancellationToken.None);

    handler.LastRequest!.RequestUri!.ToString().Should().EndWith(
        "/open-apis/docx/v1/documents/doccn%2Fa/raw_content");
}
```

- [ ] **Step 2: Run the tests and confirm they fail because the client does not exist**

Run:

```bash
dotnet test test/Aevatar.AI.ToolProviders.Lark.Tests/Aevatar.AI.ToolProviders.Lark.Tests.csproj --nologo --filter 'FullyQualifiedName~LarkDocsSearchToolTests'
```

Expected: compilation fails for missing `ILarkKnowledgeClient`, `LarkKnowledgeNyxClient`, and request types.

- [ ] **Step 3: Implement the narrow client contract**

Define only the operations needed by retrieval:

```csharp
public interface ILarkKnowledgeClient
{
    Task<string> SearchAsync(
        string token,
        LarkKnowledgeSearchRequest request,
        CancellationToken cancellationToken);

    Task<string> ResolveWikiNodeAsync(
        string token,
        string wikiToken,
        CancellationToken cancellationToken);

    Task<string> ReadDocxRawContentAsync(
        string token,
        string documentToken,
        CancellationToken cancellationToken);
}

public sealed record LarkKnowledgeSearchRequest(
    string Query,
    int PageSize,
    IReadOnlyList<string> SpaceIds);
```

Implement the endpoints through `NyxIdApiClient.ProxyRequestAsync`:

```csharp
public Task<string> SearchAsync(...)
{
    var filter = new Dictionary<string, object?> { ["doc_types"] = new[] { "DOCX", "WIKI" } };
    var body = new Dictionary<string, object?>
    {
        ["query"] = request.Query,
        ["page_size"] = request.PageSize,
    };
    if (request.SpaceIds.Count == 0)
    {
        body["doc_filter"] = new Dictionary<string, object?>(filter);
        body["wiki_filter"] = new Dictionary<string, object?>(filter);
    }
    else
    {
        var wikiFilter = new Dictionary<string, object?>(filter) { ["space_ids"] = request.SpaceIds };
        body["wiki_filter"] = wikiFilter;
    }
    return _nyxClient.ProxyRequestAsync(
        token, _options.ProviderSlug, "open-apis/search/v2/doc_wiki/search", "POST",
        JsonSerializer.Serialize(body, JsonOptions), null, cancellationToken);
}
```

- [ ] **Step 4: Run the transport tests and confirm they pass**

Run the Task 1 test command. Expected: transport tests pass.

- [ ] **Step 5: Commit the transport slice**

```bash
git add src/Aevatar.AI.ToolProviders.Lark/ILarkKnowledgeClient.cs \
  src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeNyxClient.cs \
  test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs
git commit -m "Add Lark Docs knowledge client"
```

### Task 2: Normalize Search And Document Responses

**Files:**

- Modify: `test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs`
- Create: `src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeResponseParser.cs`

- [ ] **Step 1: Write failing parser tests**

Use representative Search v2, Wiki node, and Docx raw-content payloads:

```csharp
[Fact]
public void ParseSearch_ShouldNormalizeDocxAndWikiCandidates()
{
    const string payload = """
      {"code":0,"data":{"has_more":false,"res_units":[
        {"entity_type":"DOCX","title":"Policy","result_meta":{"token":"doccn_1","url":"https://example/docx/doccn_1"}},
        {"entity_type":"WIKI","title_highlighted":"<h>Runbook</h>","result_meta":{"token":"wikcn_1","url":"https://example/wiki/wikcn_1"}}
      ]}}
      """;

    var result = LarkKnowledgeResponseParser.ParseSearch(payload);

    result.Candidates.Should().HaveCount(2);
    result.Candidates[0].ResourceToken.Should().Be("doccn_1");
    result.Candidates[1].Title.Should().Be("Runbook");
}
```

Also cover malformed JSON, nonzero Lark codes, NyxID proxy errors, absent tokens, raw-content parsing, Wiki `obj_token` resolution, and highlight-tag removal.

- [ ] **Step 2: Run the parser tests and confirm they fail**

Run the Task 1 test command. Expected: compilation fails for missing parser/result types.

- [ ] **Step 3: Implement response normalization**

Create focused internal records:

```csharp
internal sealed record LarkKnowledgeSearchResult(
    IReadOnlyList<LarkKnowledgeCandidate> Candidates,
    bool HasMore);

internal sealed record LarkKnowledgeCandidate(
    string SourceKind,
    string Title,
    string Url,
    string ResourceToken,
    string? DocumentToken);

internal sealed record LarkWikiNodeResult(string ObjectType, string ObjectToken);
```

Parse only stable fields required by the tool. Reuse `LarkProxyResponseParser.TryParseError` for provider failures, reject invalid JSON, and never expose raw response bodies in normalized errors.

- [ ] **Step 4: Run the parser tests and confirm they pass**

Run the Task 1 test command. Expected: parser and transport tests pass.

- [ ] **Step 5: Commit the normalization slice**

```bash
git add src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeResponseParser.cs \
  test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs
git commit -m "Normalize Lark Docs evidence"
```

### Task 3: Build And Register The Read-Only Retrieval Tool

**Files:**

- Modify: `test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs`
- Create: `src/Aevatar.AI.ToolProviders.Lark/Tools/LarkDocsSearchTool.cs`
- Create: `src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeAgentToolSource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Lark/LarkToolOptions.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Lark/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Write failing tool tests**

Cover the successful evidence envelope and safety behavior:

```csharp
[Fact]
public async Task ExecuteAsync_ShouldSearchReadAndReturnCitableEvidence()
{
    var client = new StubKnowledgeClient
    {
        SearchResponse = SearchResponseWithDocx("doccn_1", "Policy", "https://example/docx/doccn_1"),
        RawContentByToken = { ["doccn_1"] = """{"code":0,"data":{"content":"Expense limit is 100."}}""" },
    };
    using var context = new AgentToolRequestMetadataScope("token-123");

    var json = await new LarkDocsSearchTool(client).ExecuteAsync(
        """{"query":"expense policy","max_sources":5}""");

    using var result = JsonDocument.Parse(json);
    result.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    result.RootElement.GetProperty("sources")[0].GetProperty("content").GetString()
        .Should().Be("Expense limit is 100.");
    client.LastToken.Should().Be("token-123");
}
```

Add tests for no caller token, blank query, 30-code-point query normalization, max-source bounds, Wiki node resolution, duplicate candidates, empty search, partial read errors, all unreadable matches, per-source truncation, and read-only/no-approval declarations.

- [ ] **Step 2: Run the tool tests and confirm they fail**

Run the Task 1 test command. Expected: compilation fails for the missing tool and source.

- [ ] **Step 3: Implement bounded composite retrieval**

Implement these non-negotiable limits in `LarkDocsSearchTool`:

```csharp
internal const int DefaultMaxSources = 5;
internal const int MaximumMaxSources = 10;
internal const int MaximumQueryRunes = 30;
internal const int MaximumSourceCharacters = 12_000;
internal const int MaximumTotalCharacters = 48_000;
```

The execution order is search, de-duplicate by stable resource identity, then sequentially resolve/read until the source or total-content limit is reached. Return normalized `sources` and `unreadable_sources`; do not throw for one unreadable candidate.

- [ ] **Step 4: Register the source through dependency injection**

Add `EnableDocsSearch = true`, register `ILarkKnowledgeClient` and `LarkKnowledgeAgentToolSource`, and make discovery require the existing NyxID base URL plus provider slug:

```csharp
services.TryAddSingleton<ILarkKnowledgeClient, LarkKnowledgeNyxClient>();
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IAgentToolSource, LarkKnowledgeAgentToolSource>());
```

- [ ] **Step 5: Run the tool tests and confirm they pass**

Run the Task 1 test command. Expected: all `LarkDocsSearchToolTests` pass.

- [ ] **Step 6: Commit the tool slice**

```bash
git add src/Aevatar.AI.ToolProviders.Lark/Tools/LarkDocsSearchTool.cs \
  src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeAgentToolSource.cs \
  src/Aevatar.AI.ToolProviders.Lark/LarkToolOptions.cs \
  src/Aevatar.AI.ToolProviders.Lark/ServiceCollectionExtensions.cs \
  test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs
git commit -m "Add Lark Docs search tool"
```

### Task 4: Replace The Starter Workflow Semantics

**Files:**

- Create: `test/Aevatar.Workflow.Host.Api.Tests/EnterpriseKnowledgeAssistantTemplateTests.cs`
- Modify: `workflow-templates/enterprise_knowledge_assistant.yaml`

- [ ] **Step 1: Write the failing starter contract tests**

Assert observable semantics, not only strings:

```csharp
[Fact]
public void Starter_ShouldSearchLarkBeforeProducingTheResult()
{
    var definition = ParseStarter();

    definition.Steps.Select(static step => (step.Id, step.Type)).Should().Equal(
        ("capture_knowledge_request", "assign"),
        ("plan_lark_search", "llm_call"),
        ("search_lark_docs_and_wiki", "tool_call"),
        ("answer_or_extract", "llm_call"),
        ("record_knowledge_result", "assign"));
    definition.Steps[2].Parameters["tool"].Should().Be("lark_docs_search");
    definition.Steps[2].Parameters["arguments"].Should().Contain("${json(input)}");
}

[Fact]
public void Starter_ShouldSupportAnswersExtractionsAndCitationsWithoutSuppliedContextClaims()
{
    var yaml = File.ReadAllText(StarterPath());

    yaml.Should().Contain("structured extraction");
    yaml.Should().Contain("missing_fields");
    yaml.Should().Contain("source");
    yaml.Should().NotContain("context supplied with the run");
    yaml.Should().NotContain("does not connect to a knowledge base");
}
```

Also run `WorkflowValidator.Validate` and assert the answer role has no allowed tools.

- [ ] **Step 2: Run the starter tests and confirm they fail against the old flow**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter 'FullyQualifiedName~EnterpriseKnowledgeAssistantTemplateTests'
```

Expected: semantic assertions fail because the old template contains only answer and assign steps.

- [ ] **Step 3: Replace the YAML workflow**

Use the five-step flow and safe JSON interpolation:

```yaml
  - id: search_lark_docs_and_wiki
    type: tool_call
    parameters:
      tool: lark_docs_search
      arguments: '{"query":"${json(input)}","max_sources":5}'
    next: answer_or_extract
```

The search-planner role returns one query of at most 30 characters. The answer role receives `${knowledge_request}` and the retrieval output, treats document content as untrusted evidence, and follows the answer/extraction rules from the design.

- [ ] **Step 4: Run the starter tests and confirm they pass**

Run the Task 4 test command. Expected: starter parsing, validation, ordering, retrieval, mode, and citation assertions pass.

- [ ] **Step 5: Commit the starter migration**

```bash
git add workflow-templates/enterprise_knowledge_assistant.yaml \
  test/Aevatar.Workflow.Host.Api.Tests/EnterpriseKnowledgeAssistantTemplateTests.cs
git commit -m "Make knowledge starter search Lark Docs"
```

### Task 5: Focused Verification And Pull Request Update

**Files:**

- Review all task files and the two design records.

- [ ] **Step 1: Run the complete affected Lark provider test project**

```bash
dotnet test test/Aevatar.AI.ToolProviders.Lark.Tests/Aevatar.AI.ToolProviders.Lark.Tests.csproj --nologo
```

Expected: all tests pass.

- [ ] **Step 2: Run the focused starter contract tests**

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter 'FullyQualifiedName~EnterpriseKnowledgeAssistantTemplateTests'
```

Expected: all focused tests pass.

- [ ] **Step 3: Run the required test-stability guard**

```bash
bash tools/ci/test_stability_guards.sh
```

Expected: guard passes; the new tests contain no polling delays.

- [ ] **Step 4: Review for semantic drift and formatting errors**

```bash
rg -n -i 'approved context|context supplied with the run|does not connect to a knowledge base' \
  workflow-templates/enterprise_knowledge_assistant.yaml \
  test/Aevatar.Workflow.Host.Api.Tests/EnterpriseKnowledgeAssistantTemplateTests.cs
git diff --check
git status --short
```

Expected: the stale-term search has no matches, `git diff --check` is clean, and only task files plus the pre-existing untracked `.planning/` remain.

- [ ] **Step 5: Commit any final test-only corrections**

```bash
git add src/Aevatar.AI.ToolProviders.Lark/ILarkKnowledgeClient.cs \
  src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeNyxClient.cs \
  src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeResponseParser.cs \
  src/Aevatar.AI.ToolProviders.Lark/Tools/LarkDocsSearchTool.cs \
  src/Aevatar.AI.ToolProviders.Lark/LarkKnowledgeAgentToolSource.cs \
  src/Aevatar.AI.ToolProviders.Lark/LarkToolOptions.cs \
  src/Aevatar.AI.ToolProviders.Lark/ServiceCollectionExtensions.cs \
  test/Aevatar.AI.ToolProviders.Lark.Tests/LarkDocsSearchToolTests.cs \
  workflow-templates/enterprise_knowledge_assistant.yaml \
  test/Aevatar.Workflow.Host.Api.Tests/EnterpriseKnowledgeAssistantTemplateTests.cs
git commit -m "Harden Lark Docs retrieval coverage"
```

Skip this commit when verification required no edits.

- [ ] **Step 6: Push and update the existing pull request**

```bash
git push origin feat/2026-08-27_workflow-starter-templates
gh pr edit 3550 --body '## Problem and solution

The enterprise knowledge starter implied knowledge retrieval but only answered from supplied context. It now searches and reads the caller\'s connected Lark Docs/Wiki content before producing a cited answer or structured extraction.

## Affected paths

- Lark Docs/Wiki search and raw-content tool provider
- Enterprise knowledge assistant starter workflow
- Focused provider and starter contract tests

## Verification

- `dotnet test test/Aevatar.AI.ToolProviders.Lark.Tests/Aevatar.AI.ToolProviders.Lark.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter FullyQualifiedName~EnterpriseKnowledgeAssistantTemplateTests`
- `bash tools/ci/test_stability_guards.sh`

No frontend files changed, so no frontend suite or build was run.'
```

The PR body must describe the semantic mismatch, the live Lark Docs/Wiki retrieval solution, affected paths, exact focused commands and results, and that no full frontend suite/build was applicable because no frontend files changed.
