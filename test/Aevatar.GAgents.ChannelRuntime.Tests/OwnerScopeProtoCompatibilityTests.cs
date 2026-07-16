using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class OwnerScopeProtoCompatibilityTests
{
    private static readonly string[] LegacyOwnerScopeTypeNames =
    [
        "aevatar.gagents.scheduled.OwnerScope",
        "aevatar.chat_routing.v1.ChatRouteCallerScope",
    ];

    [Fact]
    public void OwnerScopeContainingFields_ShouldPreserveWireTags()
    {
        UserAgentCatalogEntry.Descriptor.FindFieldByName("owner_scope")!.FieldNumber.Should().Be(26);
        UserAgentCatalogUpsertCommand.Descriptor.FindFieldByName("owner_scope")!.FieldNumber.Should().Be(18);
        UserAgentCatalogDocument.Descriptor.FindFieldByName("owner_scope")!.FieldNumber.Should().Be(28);
        ChatRouteInput.Descriptor.FindFieldByName("caller_scope")!.FieldNumber.Should().Be(2);
        ChatRoutePolicyState.Descriptor.FindFieldByName("owner_scope")!.FieldNumber.Should().Be(2);
        UpsertChatRoutePolicyRequested.Descriptor.FindFieldByName("owner_scope")!.FieldNumber.Should().Be(1);
        ChatRoutePolicyCurrentStateDocument.Descriptor.FindFieldByName("owner_scope")!.FieldNumber.Should().Be(11);
    }

    [Fact]
    public void OwnerScopeContainingFields_ShouldUseCanonicalFoundationType()
    {
        OwnerScope.Descriptor.FullName.Should().Be("aevatar.OwnerScope");

        FieldMessageFullName(UserAgentCatalogEntry.Descriptor, "owner_scope").Should().Be(OwnerScope.Descriptor.FullName);
        FieldMessageFullName(UserAgentCatalogUpsertCommand.Descriptor, "owner_scope").Should().Be(OwnerScope.Descriptor.FullName);
        FieldMessageFullName(UserAgentCatalogDocument.Descriptor, "owner_scope").Should().Be(OwnerScope.Descriptor.FullName);
        FieldMessageFullName(ChatRouteInput.Descriptor, "caller_scope").Should().Be(OwnerScope.Descriptor.FullName);
        FieldMessageFullName(ChatRoutePolicyState.Descriptor, "owner_scope").Should().Be(OwnerScope.Descriptor.FullName);
        FieldMessageFullName(UpsertChatRoutePolicyRequested.Descriptor, "owner_scope").Should().Be(OwnerScope.Descriptor.FullName);
        FieldMessageFullName(ChatRoutePolicyCurrentStateDocument.Descriptor, "owner_scope").Should().Be(OwnerScope.Descriptor.FullName);
    }

    [Fact]
    public void OwnerScopeFields_ShouldPreserveCanonicalWireTags()
    {
        OwnerScope.Descriptor.FindFieldByName("nyx_user_id")!.FieldNumber.Should().Be(1);
        OwnerScope.Descriptor.FindFieldByName("platform")!.FieldNumber.Should().Be(2);
        OwnerScope.Descriptor.FindFieldByName("registration_scope_id")!.FieldNumber.Should().Be(3);
        OwnerScope.Descriptor.FindFieldByName("sender_id")!.FieldNumber.Should().Be(4);
    }

    [Fact]
    public void LegacyOwnerScopeTypeUrls_ShouldNotAppearInAnyFieldsOrDescriptors()
    {
        var files = CollectFiles(
            UserAgentCatalogEntry.Descriptor.File,
            ChatRouteInput.Descriptor.File,
            ChatRoutePolicyCurrentStateDocument.Descriptor.File,
            OwnerScope.Descriptor.File);

        var descriptorText = string.Join(
            "\n",
            files.Select(file => FileDescriptorProto.Parser.ParseFrom(file.SerializedData).ToString()));

        foreach (var legacyTypeName in LegacyOwnerScopeTypeNames)
        {
            descriptorText.Should().NotContain(legacyTypeName);
            descriptorText.Should().NotContain($"type.googleapis.com/{legacyTypeName}");
        }
    }

    private static string FieldMessageFullName(MessageDescriptor descriptor, string fieldName) =>
        descriptor.FindFieldByName(fieldName)!.MessageType.FullName;

    private static IReadOnlyCollection<FileDescriptor> CollectFiles(params FileDescriptor[] roots)
    {
        var files = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            CollectFile(root, files);
        }

        return files.Values;
    }

    private static void CollectFile(FileDescriptor file, IDictionary<string, FileDescriptor> files)
    {
        if (!files.TryAdd(file.Name, file))
        {
            return;
        }

        foreach (var dependency in file.Dependencies)
        {
            CollectFile(dependency, files);
        }
    }

    private static IEnumerable<MessageDescriptor> CollectMessages(MessageDescriptor descriptor)
    {
        yield return descriptor;

        foreach (var nested in descriptor.NestedTypes)
        {
            foreach (var nestedMessage in CollectMessages(nested))
            {
                yield return nestedMessage;
            }
        }
    }
}
