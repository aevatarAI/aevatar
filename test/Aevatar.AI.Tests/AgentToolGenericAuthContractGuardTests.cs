using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentToolGenericAuthContractGuardTests
{
    private static readonly string[] ForbiddenProviderTerms = ["NyxId", "Lark"];

    [Fact]
    public void AgentToolCredentialContracts_ShouldUseGenericIdentityAndCredentialNames()
    {
        var credentialSourceDescriptor = AgentToolCredentialsPayload.Descriptor.File.EnumTypes.Single(
            descriptor => descriptor.Name == nameof(AgentToolCredentialSourcePayload));
        var identifiers = ContractIdentifiers(
                typeof(AgentToolCredentials),
                typeof(AgentToolCredentialSource),
                typeof(AgentToolCredentialsPayload),
                typeof(AgentToolCredentialSourcePayload))
            .Concat(CredentialMemberIdentifiers(typeof(AgentToolRequestContext)))
            .Concat(AgentToolCredentialsPayload.Descriptor.Fields.InFieldNumberOrder().Select(field => field.Name))
            .Concat(credentialSourceDescriptor.Values.Select(value => value.Name));

        identifiers.Should().NotContain(
            identifier => ForbiddenProviderTerms.Any(term => identifier.Contains(term, StringComparison.Ordinal)));

        AgentToolCredentialsPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .Should()
            .Equal("access_token", "organization_token", "sender_access_token");

        credentialSourceDescriptor.Values
            .Select(value => value.Name)
            .Should()
            .Contain("AGENT_TOOL_CREDENTIAL_SOURCE_PAYLOAD_IDENTITY_ASSERTION");
    }

    private static IEnumerable<string> ContractIdentifiers(params Type[] types)
    {
        foreach (var type in types)
        {
            yield return type.Name;

            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static))
            {
                if (member.DeclaringType == typeof(object))
                    continue;

                yield return member.Name;
            }

            foreach (var parameter in type.GetConstructors().SelectMany(constructor => constructor.GetParameters()))
                yield return parameter.Name ?? string.Empty;

            if (type.IsEnum)
            {
                foreach (var name in Enum.GetNames(type))
                    yield return name;
            }
        }
    }

    private static IEnumerable<string> CredentialMemberIdentifiers(Type type)
    {
        yield return type.Name;

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name.Contains("Token", StringComparison.Ordinal)
                || property.Name.Contains("Credential", StringComparison.Ordinal))
            {
                yield return property.Name;
            }
        }

        foreach (var parameter in type.GetConstructors().SelectMany(constructor => constructor.GetParameters()))
        {
            var name = parameter.Name ?? string.Empty;
            if (name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Credential", StringComparison.OrdinalIgnoreCase))
            {
                yield return name;
            }
        }
    }
}
