using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Channels;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Google.Protobuf.WellKnownTypes;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Aevatar.GAgents.StreamingProxy.StreamingProxyEndpoints;

namespace Aevatar.AI.Tests;

public sealed class StreamingProxyArchitectureRuleTests : StreamingProxyTestBase
{
        [Fact]
        public void StreamingProxyProductionSource_ShouldDeleteSingletonParticipantAuthority()
        {
            var root = GetRepositoryRoot();
            var productionSources = Directory
                .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText);

            productionSources.Should().OnlyContain(source =>
                !source.Contains("IStreamingProxy" + "ParticipantStore", StringComparison.Ordinal) &&
                !source.Contains("ActorBackedStreamingProxy" + "ParticipantStore", StringComparison.Ordinal) &&
                !source.Contains("StreamingProxy" + "ParticipantGAgentState", StringComparison.Ordinal) &&
                !source.Contains("StreamingProxy" + "ParticipantCurrentStateDocument", StringComparison.Ordinal) &&
                !source.Contains("streaming-proxy-" + "participants", StringComparison.Ordinal));
        }

        [Fact]
        public void StreamingProxyRoomSources_ShouldNotIntroduceParallelRoomInteractionPort()
        {
            var root = GetRepositoryRoot();
            var roomSources = Directory
                .EnumerateFiles(
                    Path.Combine(root, "agents/Aevatar.GAgents.StreamingProxy"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Application{Path.DirectorySeparatorChar}Rooms{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                               Path.GetFileName(path).Equals("StreamingProxyEndpoints.cs", StringComparison.Ordinal))
                .Select(File.ReadAllText);

            roomSources.Should().OnlyContain(source =>
                !source.Contains("IStreamingProxyRoomInteractionPort", StringComparison.Ordinal));
            roomSources.Should().OnlyContain(source =>
                !source.Contains("RoomInteractionPort", StringComparison.Ordinal));
        }

        [Fact]
        public void StreamingProxyRoomAndCoordinatorSource_ShouldNotInlineDispatchActorEvents()
        {
            var root = GetRepositoryRoot();
            var roomCommandService = File.ReadAllText(Path.Combine(
                root,
                "agents/Aevatar.GAgents.StreamingProxy/Application/Rooms/StreamingProxyRoomCommandService.cs"));
            var nyxCoordinator = File.ReadAllText(Path.Combine(
                root,
                "agents/Aevatar.GAgents.StreamingProxy/StreamingProxyNyxParticipantCoordinator.cs"));

            roomCommandService.Should().NotContain("actor.HandleEventAsync(");
            roomCommandService.Should().NotContain(".HandleEventAsync(");
            nyxCoordinator.Should().NotContain("actor.HandleEventAsync(");
            nyxCoordinator.Should().NotContain(".HandleEventAsync(");
            nyxCoordinator.Should().NotContain("IActorDispatchPort", "Nyx participant coordination must stay adapter-only.");
            nyxCoordinator.Should().NotContain("GroupChatParticipantJoinedEvent", "Nyx adapter must forward join commands only.");
            nyxCoordinator.Should().NotContain("GroupChatMessageEvent", "Nyx adapter must forward message commands only.");
            nyxCoordinator.Should().NotContain("GroupChatParticipantLeftEvent", "Nyx adapter must forward leave commands only.");
            nyxCoordinator.Should().NotContain("StreamingProxyChatSessionTerminalStateChanged", "Nyx adapter must not mint terminal facts.");
        }
}
