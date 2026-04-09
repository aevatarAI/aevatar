using System.Globalization;
using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

internal static class AgentCoverageTestSupport
{
    public static ServiceProvider BuildServiceProvider()
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStoreForTests>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    public static void AssignActorId(object agent, string actorId)
    {
        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
    }

    public static T ReadPrivateField<T>(object target, string fieldName) where T : class
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(target)!;
    }

    public static T GetStaticProperty<T>(Assembly assembly, string typeName, string propertyName)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return (T)property.GetValue(null)!;
    }

    public static object CreateNonPublicInstance(Assembly assembly, string typeName, params object[] args)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: CultureInfo.InvariantCulture)!;
    }

    public static bool GetBooleanProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        return (bool)property.GetValue(target)!;
    }

    public static async Task InvokeAsync(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var result = method.Invoke(target, args);
        switch (result)
        {
            case ValueTask valueTask:
                await valueTask;
                break;
            case Task task:
                await task;
                break;
            case null:
                break;
            default:
                throw new InvalidOperationException($"Unsupported async return type {result.GetType().FullName}.");
        }
    }
}

internal sealed class TestRecordingEventPublisher : IEventPublisher
{
    public List<IMessage> Published { get; } = [];

    public Task PublishAsync<TEvent>(
        TEvent evt,
        TopologyAudience direction = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        _ = direction;
        _ = ct;
        _ = sourceEnvelope;
        _ = options;
        Published.Add(evt);
        return Task.CompletedTask;
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        _ = targetActorId;
        return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
    }

    public Task PublishCommittedStateEventAsync(
        CommittedStateEventPublished evt,
        ObserverAudience audience = ObserverAudience.CommittedFacts,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
    {
        _ = audience;
        return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
    }
}

internal sealed class StubChatProviderFactory(
    Func<LLMRequest, CancellationToken, Task<LLMResponse>> onChatAsync)
    : ILLMProviderFactory, ILLMProvider
{
    public string Name => "test-provider";

    public ILLMProvider GetProvider(string name)
    {
        _ = name;
        return this;
    }

    public ILLMProvider GetDefault() => this;

    public IReadOnlyList<string> GetAvailableProviders() => [Name];

    public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default) => onChatAsync(request, ct);

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = request;
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        throw new InvalidOperationException("Streaming path should not be used in this test.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
