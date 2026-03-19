namespace Aevatar.App.Tests;

[Collection("AppHost")]
public sealed class AppScriptsStudioApiTests
{
    private readonly AppHostFixture _fixture;

    public AppScriptsStudioApiTests(AppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScriptValidationEndpoint_ShouldAcceptValidSource_AndReportCompilerFailures()
    {
        using var valid = await _fixture.PostJsonAsync("/api/app/scripts/validate", new
        {
            scriptId = "smoke-script",
            scriptRevision = "draft-1",
            source = AppTestData.ValidScriptSource,
        });
        valid.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        valid.RootElement.GetProperty("errorCount").GetInt32().Should().Be(0);
        valid.RootElement.GetProperty("primarySourcePath").GetString().Should().NotBeNullOrWhiteSpace();

        using var invalid = await _fixture.PostJsonAsync("/api/app/scripts/validate", new
        {
            scriptId = "broken-script",
            scriptRevision = "draft-1",
            source = AppTestData.InvalidScriptSource,
        });
        invalid.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        invalid.RootElement.GetProperty("errorCount").GetInt32().Should().BeGreaterThan(0);
        invalid.RootElement.GetProperty("diagnostics")[0].GetProperty("severity").GetString().Should().Be("error");
        invalid.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ScriptDraftRun_ShouldRejectBlankSource_AndMaterializeRuntimeSnapshot()
    {
        using var rejected = await _fixture.PostJsonAsync("/api/app/scripts/draft-run", new
        {
            scriptId = "empty-script",
            scriptRevision = "draft-1",
            source = string.Empty,
            input = "ignored",
        }, HttpStatusCode.BadRequest);
        rejected.RootElement.GetProperty("code").GetString().Should().Be("SCRIPT_SOURCE_REQUIRED");

        using var draftRun = await _fixture.PostJsonAsync("/api/app/scripts/draft-run", new
        {
            scriptId = "smoke-script",
            scriptRevision = "draft-1",
            source = AppTestData.ValidScriptSource,
            input = "  hello world  ",
        });

        draftRun.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        var runtimeActorId = draftRun.RootElement.GetProperty("runtimeActorId").GetString();
        var readModelUrl = draftRun.RootElement.GetProperty("readModelUrl").GetString();
        runtimeActorId.Should().NotBeNullOrWhiteSpace();
        readModelUrl.Should().NotBeNullOrWhiteSpace();

        using var snapshot = await _fixture.WaitForJsonAsync(
            readModelUrl!,
            static document =>
            {
                var payloadJson = document.RootElement.GetProperty("readModelPayloadJson").GetString();
                if (string.IsNullOrWhiteSpace(payloadJson))
                    return false;

                using var payload = JsonDocument.Parse(payloadJson);
                return string.Equals(payload.RootElement.GetProperty("status").GetString(), "ok", StringComparison.Ordinal) &&
                       string.Equals(payload.RootElement.GetProperty("output").GetString(), "HELLO WORLD", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(30));

        snapshot.RootElement.GetProperty("actorId").GetString().Should().Be(runtimeActorId);
        snapshot.RootElement.GetProperty("scriptId").GetString().Should().Be("smoke-script");
        snapshot.RootElement.GetProperty("revision").GetString().Should().Be("draft-1");
        snapshot.RootElement.GetProperty("stateVersion").GetInt64().Should().BeGreaterThan(0);

        var readModelPayloadJson = snapshot.RootElement.GetProperty("readModelPayloadJson").GetString();
        readModelPayloadJson.Should().NotBeNullOrWhiteSpace();

        using var payloadDocument = JsonDocument.Parse(readModelPayloadJson!);
        payloadDocument.RootElement.GetProperty("input").GetString().Should().Be("  hello world  ");
        payloadDocument.RootElement.GetProperty("output").GetString().Should().Be("HELLO WORLD");
        payloadDocument.RootElement.GetProperty("status").GetString().Should().Be("ok");
        payloadDocument.RootElement.GetProperty("last_command_id").GetString().Should().NotBeNullOrWhiteSpace();
        payloadDocument.RootElement.GetProperty("notes").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal("trimmed", "uppercased");
    }

    [Fact]
    public async Task ScopedScriptManagementAndDraftRun_ShouldPersistByScope_AndPropagateScopeId()
    {
        var scopeId = $"nyx-user-{Guid.NewGuid():N}";
        var scriptId = $"scoped-script-{Guid.NewGuid():N}";

        using var context = await SendScopedJsonAsync(HttpMethod.Get, "/api/app/context", null, scopeId);
        context.RootElement.GetProperty("scopeId").GetString().Should().Be(scopeId);
        context.RootElement.GetProperty("scopeResolved").GetBoolean().Should().BeTrue();
        context.RootElement.GetProperty("scriptStorageMode").GetString().Should().Be("scope");
        context.RootElement.GetProperty("workflowStorageMode").GetString().Should().Be("scope");

        using var saved = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts",
            new
            {
                scriptId,
                revisionId = "rev-1",
                sourceText = AppTestData.ValidScriptSource,
            },
            scopeId);

        saved.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
        saved.RootElement.GetProperty("scopeId").GetString().Should().Be(scopeId);
        saved.RootElement.GetProperty("script").GetProperty("scriptId").GetString().Should().Be(scriptId);
        saved.RootElement.GetProperty("script").GetProperty("activeRevision").GetString().Should().Be("rev-1");
        saved.RootElement.GetProperty("source").GetProperty("sourceText").GetString().Should().Be(AppTestData.ValidScriptSource);

        using var list = await SendScopedJsonAsync(
            HttpMethod.Get,
            "/api/app/scripts?includeSource=true",
            null,
            scopeId);
        var listedScript = list.RootElement.EnumerateArray().Single(item =>
            string.Equals(item.GetProperty("script").GetProperty("scriptId").GetString(), scriptId, StringComparison.Ordinal));
        listedScript.GetProperty("scopeId").GetString().Should().Be(scopeId);
        listedScript.GetProperty("source").GetProperty("sourceText").GetString().Should().Be(AppTestData.ValidScriptSource);

        using var detail = await SendScopedJsonAsync(
            HttpMethod.Get,
            $"/api/app/scripts/{Uri.EscapeDataString(scriptId)}",
            null,
            scopeId);
        detail.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
        detail.RootElement.GetProperty("scopeId").GetString().Should().Be(scopeId);
        detail.RootElement.GetProperty("script").GetProperty("definitionActorId").GetString().Should().NotBeNullOrWhiteSpace();

        using var draftRun = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts/draft-run",
            new
            {
                scriptId,
                scriptRevision = "rev-1",
                source = AppTestData.ValidScriptSource,
                input = "  hello scope  ",
            },
            scopeId);

        draftRun.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        draftRun.RootElement.GetProperty("scopeId").GetString().Should().Be(scopeId);
        var runtimeActorId = draftRun.RootElement.GetProperty("runtimeActorId").GetString();
        var readModelUrl = draftRun.RootElement.GetProperty("readModelUrl").GetString();
        runtimeActorId.Should().NotBeNullOrWhiteSpace();
        readModelUrl.Should().NotBeNullOrWhiteSpace();

        using var snapshot = await _fixture.WaitForJsonAsync(
            readModelUrl!,
            static document =>
            {
                var payloadJson = document.RootElement.GetProperty("readModelPayloadJson").GetString();
                if (string.IsNullOrWhiteSpace(payloadJson))
                    return false;

                using var payload = JsonDocument.Parse(payloadJson);
                return string.Equals(payload.RootElement.GetProperty("status").GetString(), "ok", StringComparison.Ordinal) &&
                       string.Equals(payload.RootElement.GetProperty("output").GetString(), "HELLO SCOPE", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(30));

        snapshot.RootElement.GetProperty("actorId").GetString().Should().Be(runtimeActorId);
        snapshot.RootElement.GetProperty("scriptId").GetString().Should().Be(scriptId);
        snapshot.RootElement.GetProperty("revision").GetString().Should().Be("rev-1");
    }

    [Fact]
    public async Task ScopedScriptEvolutionProposal_ShouldPromoteWithinResolvedScope()
    {
        var scopeId = $"nyx-user-{Guid.NewGuid():N}";
        var scriptId = $"promoted-script-{Guid.NewGuid():N}";

        _ = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts",
            new
            {
                scriptId,
                revisionId = "rev-1",
                sourceText = AppTestData.ValidScriptSource,
            },
            scopeId);

        using var decision = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts/evolutions/proposals",
            new
            {
                scriptId,
                baseRevision = "rev-1",
                candidateRevision = "rev-2",
                candidateSource = AppTestData.ValidScriptSource,
                reason = "scope rollout",
            },
            scopeId);

        decision.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        decision.RootElement.GetProperty("scriptId").GetString().Should().Be(scriptId);
        decision.RootElement.GetProperty("candidateRevision").GetString().Should().Be("rev-2");
        decision.RootElement.GetProperty("proposalId").GetString().Should().StartWith(scopeId + ":");

        using var detail = await WaitForScopedJsonAsync(
            HttpMethod.Get,
            $"/api/app/scripts/{Uri.EscapeDataString(scriptId)}",
            null,
            scopeId,
            static document =>
                string.Equals(
                    document.RootElement.GetProperty("script").GetProperty("activeRevision").GetString(),
                    "rev-2",
                    StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));
        detail.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
        detail.RootElement.GetProperty("scopeId").GetString().Should().Be(scopeId);
        detail.RootElement.GetProperty("script").GetProperty("activeRevision").GetString().Should().Be("rev-2");
    }

    [Fact]
    public async Task AppRuntimeListEndpoint_ShouldExposeRecentSnapshots()
    {
        var scriptId = $"runtime-list-{Guid.NewGuid():N}";

        using var draftRun = await _fixture.PostJsonAsync("/api/app/scripts/draft-run", new
        {
            scriptId,
            scriptRevision = "draft-1",
            source = AppTestData.ValidScriptSource,
            input = "runtime list",
        });

        var runtimeActorId = draftRun.RootElement.GetProperty("runtimeActorId").GetString();
        runtimeActorId.Should().NotBeNullOrWhiteSpace();

        _ = await _fixture.WaitForJsonAsync(
            $"/api/app/scripts/runtimes/{Uri.EscapeDataString(runtimeActorId!)}/readmodel",
            static document => document.RootElement.GetProperty("stateVersion").GetInt64() > 0,
            TimeSpan.FromSeconds(30));

        using var runtimes = await _fixture.GetJsonAsync("/api/app/scripts/runtimes?take=10");
        var runtime = runtimes.RootElement.EnumerateArray().FirstOrDefault(item =>
            string.Equals(item.GetProperty("actorId").GetString(), runtimeActorId, StringComparison.Ordinal));

        runtime.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        runtime.GetProperty("scriptId").GetString().Should().Be(scriptId);
        runtime.GetProperty("stateVersion").GetInt64().Should().BeGreaterThan(0);
        runtime.GetProperty("readModelPayloadJson").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AppCatalogAndEvolutionDecisionEndpoints_ShouldExposeScopeHistory()
    {
        var scopeId = $"nyx-user-{Guid.NewGuid():N}";
        var scriptId = $"history-script-{Guid.NewGuid():N}";

        _ = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts",
            new
            {
                scriptId,
                revisionId = "rev-1",
                sourceText = AppTestData.ValidScriptSource,
            },
            scopeId);

        using var proposal = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts/evolutions/proposals",
            new
            {
                scriptId,
                baseRevision = "rev-1",
                candidateRevision = "rev-2",
                candidateSource = AppTestData.ValidScriptSource,
                reason = "catalog history",
            },
            scopeId);

        var proposalId = proposal.RootElement.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace();

        using var catalog = await WaitForScopedJsonAsync(
            HttpMethod.Get,
            $"/api/app/scripts/{Uri.EscapeDataString(scriptId)}/catalog",
            null,
            scopeId,
            static document =>
                string.Equals(
                    document.RootElement.GetProperty("activeRevision").GetString(),
                    "rev-2",
                    StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        catalog.RootElement.GetProperty("scriptId").GetString().Should().Be(scriptId);
        catalog.RootElement.GetProperty("activeRevision").GetString().Should().Be("rev-2");
        catalog.RootElement.GetProperty("previousRevision").GetString().Should().Be("rev-1");
        catalog.RootElement.GetProperty("revisionHistory").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Contain(new[] { "rev-1", "rev-2" });
        catalog.RootElement.GetProperty("lastProposalId").GetString().Should().Be(proposalId);

        using var decision = await _fixture.WaitForJsonAsync(
            $"/api/app/scripts/evolutions/{Uri.EscapeDataString(proposalId!)}",
            static document =>
                string.Equals(
                    document.RootElement.GetProperty("candidateRevision").GetString(),
                    "rev-2",
                    StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        decision.RootElement.GetProperty("proposalId").GetString().Should().Be(proposalId);
        decision.RootElement.GetProperty("scriptId").GetString().Should().Be(scriptId);
        decision.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PackageRequests_ShouldValidateSaveAndRunSplitScriptPackage()
    {
        var scopeId = $"nyx-user-{Guid.NewGuid():N}";
        var scriptId = $"package-script-{Guid.NewGuid():N}";
        var package = AppTestData.CreateSplitScriptPackage();

        using var validation = await _fixture.PostJsonAsync("/api/app/scripts/validate", new
        {
            scriptId,
            scriptRevision = "rev-1",
            package,
        });
        validation.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        validation.RootElement.GetProperty("primarySourcePath").GetString().Should().Be("Behavior.cs");

        using var saved = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts",
            new
            {
                scriptId,
                revisionId = "rev-1",
                package,
            },
            scopeId);
        saved.RootElement.GetProperty("source").GetProperty("sourceText").GetString()
            .Should().Contain("\"format\":\"aevatar.scripting.package.v1\"");

        using var draftRun = await SendScopedJsonAsync(
            HttpMethod.Post,
            "/api/app/scripts/draft-run",
            new
            {
                scriptId,
                scriptRevision = "rev-1",
                package,
                input = "  split package  ",
            },
            scopeId);

        draftRun.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        var readModelUrl = draftRun.RootElement.GetProperty("readModelUrl").GetString();
        readModelUrl.Should().NotBeNullOrWhiteSpace();

        using var snapshot = await _fixture.WaitForJsonAsync(
            readModelUrl!,
            static document =>
            {
                var payloadJson = document.RootElement.GetProperty("readModelPayloadJson").GetString();
                if (string.IsNullOrWhiteSpace(payloadJson))
                    return false;

                using var payload = JsonDocument.Parse(payloadJson);
                return string.Equals(payload.RootElement.GetProperty("output").GetString(), "SPLIT PACKAGE", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(30));

        using var payloadDocument = JsonDocument.Parse(snapshot.RootElement.GetProperty("readModelPayloadJson").GetString()!);
        payloadDocument.RootElement.GetProperty("output").GetString().Should().Be("SPLIT PACKAGE");
        payloadDocument.RootElement.GetProperty("notes").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Contain("multi-file");
    }

    private async Task<JsonDocument> SendScopedJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        string scopeId,
        HttpStatusCode expectedStatusCode = HttpStatusCode.OK,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Aevatar-Scope-Id", scopeId);
        if (payload != null)
            request.Content = JsonContent.Create(payload);

        using var response = await _fixture.Client.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(expectedStatusCode);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        content.Should().NotBeNullOrWhiteSpace();
        return JsonDocument.Parse(content);
    }

    private async Task<JsonDocument> WaitForScopedJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        string scopeId,
        Func<JsonDocument, bool> predicate,
        TimeSpan timeout,
        HttpStatusCode expectedStatusCode = HttpStatusCode.OK,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            var document = await SendScopedJsonAsync(
                method,
                path,
                payload,
                scopeId,
                expectedStatusCode,
                timeoutCts.Token);
            if (predicate(document))
                return document;

            document.Dispose();
            await Task.Yield();
        }
    }
}
