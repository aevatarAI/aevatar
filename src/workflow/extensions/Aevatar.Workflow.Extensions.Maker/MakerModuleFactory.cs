using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Extensions.Maker.Modules;

namespace Aevatar.Workflow.Extensions.Maker;

/// <summary>
/// Module factory for maker-specific workflow extensions.
/// </summary>
public sealed class MakerModuleFactory : IEventModuleFactory
{
    public bool TryCreate(string name, out IEventModule? module)
    {
        module = name switch
        {
            "maker_vote" => new MakerVoteModule(),
            "maker_recursive" or "maker_recursive_solve" => new MakerRecursiveModule(),
            _ => null,
        };

        return module != null;
    }
}
