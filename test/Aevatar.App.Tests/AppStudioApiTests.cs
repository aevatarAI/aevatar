namespace Aevatar.App.Tests;

[Collection("AppHost")]
public sealed class AppStudioApiTests
{
    private readonly AppHostFixture _fixture;

    public AppStudioApiTests(AppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Workspace_Editor_Settings_AndExecutionEndpoints_ShouldServeExpectedResponses()
    {
        using var workspace = await _fixture.GetJsonAsync("/api/workspace");
        workspace.RootElement.GetProperty("runtimeBaseUrl").GetString().Should().Be(_fixture.BaseUrl);
        workspace.RootElement.GetProperty("directories").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        using var workflows = await _fixture.GetJsonAsync("/api/workspace/workflows");
        workflows.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        workflows.RootElement.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var extraDirectoryPath = Path.Combine(_fixture.TempRootDirectory, "workspace-extra");
        Directory.CreateDirectory(extraDirectoryPath);
        using var updatedWorkspace = await _fixture.PostJsonAsync("/api/workspace/directories", new
        {
            path = extraDirectoryPath,
            label = "Scratch",
        });
        var scratchDirectoryId = updatedWorkspace.RootElement
            .GetProperty("directories")
            .EnumerateArray()
            .Single(item => string.Equals(item.GetProperty("label").GetString(), "Scratch", StringComparison.Ordinal))
            .GetProperty("directoryId")
            .GetString();
        scratchDirectoryId.Should().NotBeNullOrWhiteSpace();

        using var savedWorkflow = await _fixture.PostJsonAsync("/api/workspace/workflows", new
        {
            workflowId = (string?)null,
            directoryId = scratchDirectoryId,
            workflowName = "smoke-workflow",
            fileName = "smoke-workflow.yaml",
            yaml = _fixture.SeedWorkflowYaml,
            layout = (object?)null,
        });
        var workflowId = savedWorkflow.RootElement.GetProperty("workflowId").GetString();
        workflowId.Should().NotBeNullOrWhiteSpace();
        savedWorkflow.RootElement.GetProperty("yaml").GetString().Should().Be(_fixture.SeedWorkflowYaml);

        using var listedAfterSave = await _fixture.GetJsonAsync("/api/workspace/workflows");
        listedAfterSave.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("workflowId").GetString())
            .Should().Contain(workflowId);

        using var loadedWorkflow = await _fixture.GetJsonAsync($"/api/workspace/workflows/{Uri.EscapeDataString(workflowId!)}");
        loadedWorkflow.RootElement.GetProperty("yaml").GetString().Should().Be(_fixture.SeedWorkflowYaml);

        using var parsed = await _fixture.PostJsonAsync("/api/editor/parse-yaml", new
        {
            yaml = _fixture.SeedWorkflowYaml,
            availableWorkflowNames = Array.Empty<string>(),
        });
        parsed.RootElement.GetProperty("document").ValueKind.Should().Be(JsonValueKind.Object);
        parsed.RootElement.GetProperty("graph").ValueKind.Should().Be(JsonValueKind.Object);
        parsed.RootElement.GetProperty("findings").EnumerateArray()
            .Select(static item => item.GetProperty("level").GetInt32())
            .Should().NotContain(2);

        var document = parsed.RootElement.GetProperty("document").Clone();
        using var validated = await _fixture.PostJsonAsync("/api/editor/validate", new
        {
            document,
            availableWorkflowNames = Array.Empty<string>(),
        });
        validated.RootElement.GetProperty("findings").EnumerateArray()
            .Select(static item => item.GetProperty("level").GetInt32())
            .Should().NotContain(2);

        using var normalized = await _fixture.PostJsonAsync("/api/editor/normalize", new
        {
            document,
            availableWorkflowNames = Array.Empty<string>(),
        });
        normalized.RootElement.GetProperty("yaml").GetString().Should().NotBeNullOrWhiteSpace();
        normalized.RootElement.GetProperty("findings").EnumerateArray()
            .Select(static item => item.GetProperty("level").GetInt32())
            .Should().NotContain(2);

        using var serialized = await _fixture.PostJsonAsync("/api/editor/serialize-yaml", new
        {
            document,
            availableWorkflowNames = Array.Empty<string>(),
        });
        serialized.RootElement.GetProperty("yaml").GetString().Should().NotBeNullOrWhiteSpace();
        serialized.RootElement.GetProperty("findings").EnumerateArray()
            .Select(static item => item.GetProperty("level").GetInt32())
            .Should().NotContain(2);

        using var diff = await _fixture.PostJsonAsync("/api/editor/diff", new
        {
            beforeYaml = _fixture.SeedWorkflowYaml,
            afterYaml = normalized.RootElement.GetProperty("yaml").GetString(),
        });
        diff.RootElement.GetProperty("lines").GetArrayLength().Should().BeGreaterThan(0);

        using var settings = await _fixture.GetJsonAsync("/api/settings");
        settings.RootElement.GetProperty("runtimeBaseUrl").GetString().Should().Be(_fixture.BaseUrl);
        settings.RootElement.GetProperty("providerTypes").GetArrayLength().Should().BeGreaterThan(0);

        using var runtimeProbe = await _fixture.PostJsonAsync("/api/settings/runtime/test", new
        {
            runtimeBaseUrl = _fixture.BaseUrl,
        });
        runtimeProbe.RootElement.GetProperty("reachable").GetBoolean().Should().BeTrue();
        runtimeProbe.RootElement.GetProperty("statusCode").GetInt32().Should().Be(200);

        using var executions = await _fixture.GetJsonAsync("/api/executions");
        executions.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        executions.RootElement.GetArrayLength().Should().Be(0);

        using var missingExecution = await _fixture.Client.GetAsync("/api/executions/missing");
        missingExecution.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var removedWorkspace = await _fixture.Client.DeleteAsync($"/api/workspace/directories/{Uri.EscapeDataString(scratchDirectoryId!)}");
        removedWorkspace.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SettingsSave_ShouldSwitchContextToRemoteRuntimeTarget()
    {
        const string remoteRuntimeBaseUrl = "https://api.aevatar.ai";

        try
        {
            using var savedSettings = await _fixture.PutJsonAsync("/api/settings", new
            {
                runtimeBaseUrl = remoteRuntimeBaseUrl,
            });
            savedSettings.RootElement.GetProperty("runtimeBaseUrl").GetString().Should().Be(remoteRuntimeBaseUrl);

            using var workspace = await _fixture.GetJsonAsync("/api/workspace");
            workspace.RootElement.GetProperty("runtimeBaseUrl").GetString().Should().Be(remoteRuntimeBaseUrl);

            using var context = await _fixture.GetJsonAsync("/api/app/context");
            context.RootElement.GetProperty("mode").GetString().Should().Be("proxy");

            using var workflowGenerator = await _fixture.PostJsonAsync("/api/app/workflow-generator", new
            {
                prompt = "build a simple workflow",
            }, HttpStatusCode.BadRequest);
            workflowGenerator.RootElement.GetProperty("code").GetString().Should().Be("WORKFLOW_GENERATOR_UNAVAILABLE");
        }
        finally
        {
            _ = await _fixture.PutJsonAsync("/api/settings", new
            {
                runtimeBaseUrl = _fixture.BaseUrl,
            });
        }
    }

    [Fact]
    public async Task ConnectorEndpoints_ShouldSupportCatalogDraftAndImportRoundTrips()
    {
        using var initialCatalog = await _fixture.GetJsonAsync("/api/connectors");
        initialCatalog.RootElement.GetProperty("connectors").ValueKind.Should().Be(JsonValueKind.Array);

        using var savedCatalog = await _fixture.PutJsonAsync("/api/connectors", new
        {
            connectors = new object[]
            {
                AppTestData.CreateConnector("sample-http"),
            },
        });
        savedCatalog.RootElement.GetProperty("connectors").GetArrayLength().Should().Be(1);
        savedCatalog.RootElement.GetProperty("connectors")[0].GetProperty("name").GetString().Should().Be("sample-http");

        using var savedDraft = await _fixture.PutJsonAsync("/api/connectors/draft", new
        {
            draft = AppTestData.CreateConnector("draft-http"),
        });
        savedDraft.RootElement.GetProperty("draft").GetProperty("name").GetString().Should().Be("draft-http");

        using var loadedDraft = await _fixture.GetJsonAsync("/api/connectors/draft");
        loadedDraft.RootElement.GetProperty("draft").GetProperty("name").GetString().Should().Be("draft-http");

        using (var deleteDraft = await _fixture.Client.DeleteAsync("/api/connectors/draft"))
        {
            deleteDraft.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var clearedDraft = await _fixture.GetJsonAsync("/api/connectors/draft");
        clearedDraft.RootElement.TryGetProperty("draft", out _).Should().BeFalse();

        using var importContent = new MultipartFormDataContent();
        var connectorImport = new ByteArrayContent(Encoding.UTF8.GetBytes(AppTestData.CreateConnectorImportJson("imported-http")));
        connectorImport.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        importContent.Add(connectorImport, "file", "connectors.json");

        using var importResponse = await _fixture.Client.PostAsync("/api/connectors/import", importContent);
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var importedCatalog = JsonDocument.Parse(await importResponse.Content.ReadAsStringAsync());
        importedCatalog.RootElement.GetProperty("importedCount").GetInt32().Should().Be(1);
        importedCatalog.RootElement.GetProperty("connectors")[0].GetProperty("name").GetString().Should().Be("imported-http");
    }

    [Fact]
    public async Task RoleEndpoints_ShouldSupportCatalogDraftAndImportRoundTrips()
    {
        using var initialCatalog = await _fixture.GetJsonAsync("/api/roles");
        initialCatalog.RootElement.GetProperty("roles").ValueKind.Should().Be(JsonValueKind.Array);

        using var savedCatalog = await _fixture.PutJsonAsync("/api/roles", new
        {
            roles = new object[]
            {
                AppTestData.CreateRole("support-agent"),
            },
        });
        savedCatalog.RootElement.GetProperty("roles").GetArrayLength().Should().Be(1);
        savedCatalog.RootElement.GetProperty("roles")[0].GetProperty("id").GetString().Should().Be("support-agent");

        using var savedDraft = await _fixture.PutJsonAsync("/api/roles/draft", new
        {
            draft = AppTestData.CreateRole("draft-support-agent"),
        });
        savedDraft.RootElement.GetProperty("draft").GetProperty("id").GetString().Should().Be("draft-support-agent");

        using var loadedDraft = await _fixture.GetJsonAsync("/api/roles/draft");
        loadedDraft.RootElement.GetProperty("draft").GetProperty("id").GetString().Should().Be("draft-support-agent");

        using (var deleteDraft = await _fixture.Client.DeleteAsync("/api/roles/draft"))
        {
            deleteDraft.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var clearedDraft = await _fixture.GetJsonAsync("/api/roles/draft");
        clearedDraft.RootElement.TryGetProperty("draft", out _).Should().BeFalse();

        using var importContent = new MultipartFormDataContent();
        var roleImport = new ByteArrayContent(Encoding.UTF8.GetBytes(AppTestData.CreateRoleImportJson("imported-support-agent")));
        roleImport.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        importContent.Add(roleImport, "file", "roles.json");

        using var importResponse = await _fixture.Client.PostAsync("/api/roles/import", importContent);
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var importedCatalog = JsonDocument.Parse(await importResponse.Content.ReadAsStringAsync());
        importedCatalog.RootElement.GetProperty("importedCount").GetInt32().Should().Be(1);
        importedCatalog.RootElement.GetProperty("roles")[0].GetProperty("id").GetString().Should().Be("imported-support-agent");
    }
}
