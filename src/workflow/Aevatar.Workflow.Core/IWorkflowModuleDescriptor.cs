using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Registration descriptor for one workflow module implementation with one or more aliases.
/// </summary>
public interface IWorkflowModuleDescriptor
{
    IReadOnlyList<string> Names { get; }

    IEventModule Create();
}
