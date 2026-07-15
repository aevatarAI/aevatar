using System.Reflection;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowScheduleGenericAuthContractGuardTests
{
    private static readonly string[] ForbiddenProviderTerms = ["NyxId", "Lark"];

    [Fact]
    public void WorkflowScheduleAuthContracts_ShouldUseGenericIdentityAndCredentialNames()
    {
        var identifiers = ContractIdentifiers(
            typeof(WorkflowScheduleMutationContext),
            typeof(WorkflowScheduleIdentitySubject),
            typeof(WorkflowScheduleIdentityCredentialSource),
            typeof(WorkflowScheduleScopeOwnerIdentityCredentialSource),
            typeof(WorkflowScheduleAuth));

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
        }
    }
}
