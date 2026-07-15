using System.Reflection;
using Aevatar.GAgentService.Abstractions.Schedules;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScheduledDispatchGenericAuthContractGuardTests
{
    private static readonly string[] ForbiddenProviderTerms = ["NyxId", "Lark"];

    [Fact]
    public void ScheduledServiceInvocationAuthContracts_ShouldUseGenericIdentityAndCredentialNames()
    {
        var identifiers = ContractIdentifiers(
            typeof(ScheduledServiceInvocationIdentitySubject),
            typeof(ScheduledServiceInvocationIdentityCredentialRole),
            typeof(ScheduledServiceInvocationCredentialSource),
            typeof(ScheduledServiceInvocationIdentityCredentialSource),
            typeof(ScheduledServiceInvocationScopeOwnerCredentialSource),
            typeof(ScheduledServiceInvocationAuth),
            typeof(ScheduledDispatchMutationContext),
            typeof(ScheduledDispatchCredentialAdmissionRequest),
            typeof(ScheduledDispatchCredentialSourceKind),
            typeof(IScheduledServiceInvocationCredentialExchangePort));

        identifiers.Should().NotContain(
            identifier => ForbiddenProviderTerms.Any(term => identifier.Contains(term, StringComparison.Ordinal)));
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
}
