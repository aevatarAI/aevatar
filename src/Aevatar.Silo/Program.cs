// ─────────────────────────────────────────────────────────────
// Aevatar.Silo — Standalone Orleans Silo Host (Kafka transport)
//
// Deployment model: Independent Silo process.
// The API (Client) process connects via IClusterClient.
//
// Usage:
//   dotnet run --project src/Aevatar.Silo
//
// Environment variables:
//   KAFKA_BOOTSTRAP     - Kafka bootstrap servers (default: localhost:9092)
//   ORLEANS_CLUSTER_ID  - Cluster ID (default: aevatar-dev)
//   ORLEANS_SERVICE_ID  - Service ID (default: aevatar)
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Consumers;
using Aevatar.Orleans.DependencyInjection;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// ─── Configuration ───
var kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "localhost:9092";
var clusterId = Environment.GetEnvironmentVariable("ORLEANS_CLUSTER_ID") ?? "aevatar-dev";
var serviceId = Environment.GetEnvironmentVariable("ORLEANS_SERVICE_ID") ?? "aevatar";
var topicName = Aevatar.Orleans.Constants.AgentEventEndpoint;
var consumerGroup = $"{serviceId}-silo";

// ─── Orleans Silo ───
builder.Services.AddOrleans(siloBuilder =>
{
    siloBuilder
        // Development: localhost clustering + in-memory storage
        .UseLocalhostClustering()
        .AddMemoryGrainStorageAsDefault()
        .AddMemoryGrainStorage(Aevatar.Orleans.Constants.GrainStorageName)
        .Configure<Orleans.Configuration.ClusterOptions>(opts =>
        {
            opts.ClusterId = clusterId;
            opts.ServiceId = serviceId;
        })
        // Register all Aevatar Silo services
        .AddAevatarOrleansSilo();
});

// ─── MassTransit (Kafka Rider) ───
builder.Services.AddMassTransit(x =>
{
    // Base transport: in-memory (Kafka is a Rider, not a transport)
    x.UsingInMemory();

    x.AddRider(rider =>
    {
        // Consumer: bridges Kafka messages → Orleans Grain
        rider.AddConsumer<AgentEventConsumer>();

        // Producer: registered as ITopicProducer<AgentEventMessage>
        rider.AddProducer<AgentEventMessage>(topicName);

        rider.UsingKafka((ctx, k) =>
        {
            k.Host(kafkaBootstrap);

            // Topic endpoint: consume AgentEventMessage from Kafka topic
            k.TopicEndpoint<AgentEventMessage>(topicName, consumerGroup, e =>
            {
                e.ConfigureConsumer<AgentEventConsumer>(ctx);
            });
        });
    });
});

// ─── IAgentEventSender: Kafka-backed implementation ───
builder.Services.AddSingleton<IAgentEventSender, KafkaAgentEventSender>();

// ─── Logging ───
builder.Logging.AddConsole();

// ─── Build & Run ───
var host = builder.Build();

Console.WriteLine($"""
    ╔═══════════════════════════════════════════════╗
    ║         Aevatar Orleans Silo (Kafka)          ║
    ║  Cluster: {clusterId,-35} ║
    ║  Service: {serviceId,-35} ║
    ║  Kafka:   {kafkaBootstrap,-35} ║
    ║  Topic:   {topicName,-35} ║
    ╚═══════════════════════════════════════════════╝
    """);

await host.RunAsync();

// ─── Kafka-backed IAgentEventSender ───
/// <summary>
/// Sends <see cref="AgentEventMessage"/> to a Kafka topic via
/// MassTransit's <see cref="ITopicProducer{T}"/>.
/// </summary>
internal sealed class KafkaAgentEventSender(
    ITopicProducer<AgentEventMessage> producer) : IAgentEventSender
{
    public Task SendAsync(AgentEventMessage message, CancellationToken ct = default)
        => producer.Produce(message, ct);
}
