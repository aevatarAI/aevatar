using System.Reflection;
using System.Text.Json;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Hosting.CapabilityApi;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting.Tests;

public sealed class ScriptQueryEndpointsHandlerTests
{
    private static readonly IServiceProvider HttpResultServices = new ServiceCollection()
        .AddLogging()
        .AddOptions()
        .Configure<JsonOptions>(_ => { })
        .BuildServiceProvider();

    [Fact]
    public async Task HandleListSnapshots_ShouldNormalizeTakeAndSerializePayload()
    {
        var service = new RecordingQueryService
        {
            ListResult =
            [
                new ScriptReadModelSnapshot(
                    ActorId: "runtime-1",
                    ScriptId: "script-1",
                    DefinitionActorId: "definition-1",
                    Revision: "rev-1",
                    ReadModelTypeUrl: Any.Pack(new Struct()).TypeUrl,
                    ReadModelPayload: Any.Pack(new Struct
                    {
                        Fields = { ["status"] = Google.Protobuf.WellKnownTypes.Value.ForString("ok") },
                    }),
                    StateVersion: 3,
                    LastEventId: "evt-1",
                    UpdatedAt: new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        var result = await InvokeAsync<IResult>("HandleListSnapshots", 0, service, CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastListTake.Should().Be(200);
        response.Json.GetArrayLength().Should().Be(1);
        var item = response.Json.EnumerateArray().Single();
        item.GetProperty("actorId").GetString().Should().Be("runtime-1");
        item.GetProperty("readModelPayloadJson").GetString().Should().Contain("\"status\": \"ok\"");
    }

    [Fact]
    public async Task HandleGetSnapshot_ShouldReturnNotFound_WhenSnapshotMissing()
    {
        var result = await InvokeAsync<IResult>("HandleGetSnapshot", "runtime-missing", new RecordingQueryService(), CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleExecuteDeclaredQuery_ShouldReturnBadRequest_WhenActorIdMissing()
    {
        var result = await InvokeAsync<IResult>(
            "HandleExecuteDeclaredQuery",
            string.Empty,
            new ScriptDeclaredQueryHttpRequest("{}"),
            new RecordingQueryService(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Json.GetProperty("code").GetString().Should().Be("ACTOR_ID_REQUIRED");
    }

    [Fact]
    public async Task HandleExecuteDeclaredQuery_ShouldReturnBadRequest_WhenJsonPayloadIsInvalid()
    {
        var result = await InvokeAsync<IResult>(
            "HandleExecuteDeclaredQuery",
            "runtime-1",
            new ScriptDeclaredQueryHttpRequest("{invalid-json"),
            new RecordingQueryService(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Json.GetProperty("code").GetString().Should().Be("INVALID_QUERY_PAYLOAD");
    }

    [Fact]
    public async Task HandleExecuteDeclaredQuery_ShouldMapReadModelMissing_ToNotFound()
    {
        var service = new RecordingQueryService
        {
            QueryException = new InvalidOperationException("read model missing"),
        };

        var result = await InvokeAsync<IResult>(
            "HandleExecuteDeclaredQuery",
            "runtime-1",
            new ScriptDeclaredQueryHttpRequest("{}"),
            service,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Json.GetProperty("code").GetString().Should().Be("READ_MODEL_NOT_FOUND");
    }

    [Fact]
    public async Task HandleExecuteDeclaredQuery_ShouldSerializeQueryResult()
    {
        var service = new RecordingQueryService
        {
            QueryResult = Any.Pack(new Struct
            {
                Fields = { ["answer"] = Google.Protobuf.WellKnownTypes.Value.ForString("ok") },
            }),
        };

        var result = await InvokeAsync<IResult>(
            "HandleExecuteDeclaredQuery",
            "runtime-1",
            new ScriptDeclaredQueryHttpRequest("{\"q\":\"hello\"}"),
            service,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastQueryActorId.Should().Be("runtime-1");
        service.LastQueryPayload.Should().NotBeNull();
        service.LastQueryPayload!.Is(Struct.Descriptor).Should().BeTrue();
        response.Json.GetProperty("actorId").GetString().Should().Be("runtime-1");
        response.Json.GetProperty("queryResultJson").GetString().Should().Contain("\"answer\": \"ok\"");
    }

    private static async Task<T> InvokeAsync<T>(string methodName, params object[] args)
    {
        var method = typeof(ScriptQueryEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        var task = method!.Invoke(null, args).Should().BeAssignableTo<Task<T>>().Subject;
        return await task;
    }

    private static async Task<(int StatusCode, JsonElement Json)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = HttpResultServices;
        await using var stream = new MemoryStream();
        context.Response.Body = stream;

        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        if (context.Response.Body.Length == 0)
            return (context.Response.StatusCode, JsonDocument.Parse("{}").RootElement.Clone());

        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    private sealed class RecordingQueryService : IScriptReadModelQueryApplicationService
    {
        public int LastListTake { get; private set; }
        public string? LastQueryActorId { get; private set; }
        public Any? LastQueryPayload { get; private set; }
        public IReadOnlyList<ScriptReadModelSnapshot> ListResult { get; init; } = [];
        public ScriptReadModelSnapshot? SnapshotResult { get; init; }
        public Any? QueryResult { get; init; }
        public InvalidOperationException? QueryException { get; init; }

        public Task<ScriptReadModelSnapshot?> GetSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SnapshotResult);
        }

        public Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(int take = 200, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastListTake = take;
            return Task.FromResult(ListResult);
        }

        public Task<Any?> ExecuteDeclaredQueryAsync(string actorId, Any queryPayload, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastQueryActorId = actorId;
            LastQueryPayload = queryPayload;
            if (QueryException != null)
                throw QueryException;

            return Task.FromResult(QueryResult);
        }
    }
}
