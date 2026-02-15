using System.Text.Json;
using Aevatar.Demos.CaseProjection.Abstractions;
using Aevatar.Demos.CaseProjection.Abstractions.Events;
using Aevatar.Demos.CaseProjection.Abstractions.ReadModels;
using Aevatar.Demos.CaseProjection.DependencyInjection;
using Aevatar.Demos.CaseProjection.Extensions.Sla.Reducers;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Demos.CaseProjection.Host;

internal static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStreamProvider, InMemoryStreamProvider>();

        services.AddCaseProjectionDemo(options =>
        {
            options.Enabled = true;
            options.EnableRunQueryEndpoints = true;
            options.RunProjectionCompletionWaitTimeoutMs = 3000;
        });

        // OCP extension point: load external reducer/projector assembly with no core changes.
        services.AddCaseProjectionExtensionsFromAssembly(typeof(CaseEscalatedEventReducer).Assembly);

        var provider = services.BuildServiceProvider();
        var streamProvider = provider.GetRequiredService<IStreamProvider>();
        var projectionService = provider.GetRequiredService<ICaseProjectionService>();

        const string rootActorId = "case-actor-001";

        var session = await projectionService.StartAsync(
            rootActorId: rootActorId,
            caseId: "CASE-2026-0001",
            caseType: "incident",
            input: "Customer cannot complete payment.");

        var stream = streamProvider.GetStream(rootActorId);

        await stream.ProduceAsync(Wrap(new CaseStartedEvent
        {
            CaseId = "CASE-2026-0001",
            RunId = session.RunId,
            CaseType = "incident",
            Title = "Payment failure in checkout",
            Input = "Customer cannot complete payment.",
        }, rootActorId));

        await stream.ProduceAsync(Wrap(new CaseOwnerAssignedEvent
        {
            CaseId = "CASE-2026-0001",
            RunId = session.RunId,
            OwnerId = "support.oncall",
        }, rootActorId));

        await stream.ProduceAsync(Wrap(new CaseCommentAddedEvent
        {
            CaseId = "CASE-2026-0001",
            RunId = session.RunId,
            AuthorId = "support.oncall",
            Content = "Confirmed issue can be reproduced in production.",
        }, rootActorId));

        // This event is handled by external extension assembly.
        await stream.ProduceAsync(Wrap(new CaseEscalatedEvent
        {
            CaseId = "CASE-2026-0001",
            RunId = session.RunId,
            Level = 2,
            Reason = "Revenue impact exceeds threshold.",
        }, rootActorId));

        await stream.ProduceAsync(Wrap(new CaseResolvedEvent
        {
            CaseId = "CASE-2026-0001",
            RunId = session.RunId,
            Resolved = true,
            Resolution = "Rolled back payment gateway release.",
        }, rootActorId));

        _ = await projectionService.WaitForRunProjectionCompletedAsync(session.RunId);

        var report = await projectionService.CompleteAsync(session,
        [
            new CaseTopologyEdge(rootActorId, "support.oncall"),
            new CaseTopologyEdge(rootActorId, "payments.team"),
        ]);

        if (report == null)
        {
            Console.WriteLine("Projection disabled or no report generated.");
            return;
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Console.WriteLine("=== Case Projection Report ===");
        Console.WriteLine(json);
    }

    private static EventEnvelope Wrap(IMessage evt, string publisherId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = publisherId,
        Direction = EventDirection.Self,
    };
}
