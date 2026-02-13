using Aevatar.EventModules;
using Aevatar.Sample.Maker.Modules;

namespace Aevatar.Sample.Maker;

/// <summary>
/// Sample-scoped module factory for MAKER-specific primitives.
/// Keeps framework-level Cognitive modules generic.
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
