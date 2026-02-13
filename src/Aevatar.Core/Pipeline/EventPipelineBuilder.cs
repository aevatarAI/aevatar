// ─────────────────────────────────────────────────────────────
// EventPipelineBuilder - event pipeline builder.
// Merges static handlers and dynamic modules into a priority-sorted array.
// ─────────────────────────────────────────────────────────────

using Aevatar.EventModules;

namespace Aevatar.Pipeline;

/// <summary>
/// Builds unified event pipeline by adapting static handlers and merging dynamic modules.
/// </summary>
internal static class EventPipelineBuilder
{
    /// <summary>Builds full pipeline: static adapters + dynamic modules, sorted by ascending priority.</summary>
    public static IEventModule[] Build(EventHandlerMetadata[] staticHandlers, IEventModule[] dynamicModules, IAgent agent)
    {
        var adapters = new IEventModule[staticHandlers.Length];
        for (var i = 0; i < staticHandlers.Length; i++)
            adapters[i] = new StaticHandlerAdapter(staticHandlers[i], agent);

        var pipeline = new IEventModule[adapters.Length + dynamicModules.Length];
        adapters.CopyTo(pipeline, 0);
        dynamicModules.CopyTo(pipeline, adapters.Length);
        Array.Sort(pipeline, (a, b) => a.Priority.CompareTo(b.Priority));
        return pipeline;
    }
}
