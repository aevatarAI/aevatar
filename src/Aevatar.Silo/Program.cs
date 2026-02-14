// ─────────────────────────────────────────────────────────────
// Aevatar.Silo — Standalone Orleans Silo Host (Kafka transport)
//
// Deployment model: Independent Silo process.
// The API (Client) process connects via Orleans Client.
//
// Usage:
//   dotnet run --project src/Aevatar.Silo
//
// Environment variables:
//   KAFKA_BOOTSTRAP     - Kafka bootstrap servers (default: localhost:9092)
//   ORLEANS_CLUSTER_ID  - Cluster ID (default: aevatar-dev)
//   ORLEANS_SERVICE_ID  - Service ID (default: aevatar)
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// ─── Configuration ───
var kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "localhost:9092";
var clusterId = Environment.GetEnvironmentVariable("ORLEANS_CLUSTER_ID") ?? "aevatar-dev";
var serviceId = Environment.GetEnvironmentVariable("ORLEANS_SERVICE_ID") ?? "aevatar";

// ─── Orleans Silo ───
builder.Services.AddOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddMemoryGrainStorageAsDefault()
        .AddMemoryGrainStorage(Aevatar.Orleans.Constants.GrainStorageName)
        .Configure<Orleans.Configuration.ClusterOptions>(opts =>
        {
            opts.ClusterId = clusterId;
            opts.ServiceId = serviceId;
        })
        .AddAevatarOrleansSilo();
});

// ─── MassTransit + Kafka (Silo: Consumer + Producer) ───
var consumerGroup = $"{serviceId}-silo";
builder.Services.AddAevatarKafkaSilo(kafkaBootstrap, consumerGroup: consumerGroup);

// ─── Logging ───
builder.Logging.AddConsole();

// ─── Build & Run ───
var topicName = Aevatar.Orleans.Constants.AgentEventEndpoint;
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
