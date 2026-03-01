using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Microsoft.CodeAnalysis;
using System.Text.Json;

namespace Aevatar.Scripting.Infrastructure.Compilation;

internal static class ScriptCompilationMetadataReferences
{
    public static IReadOnlyList<MetadataReference> Build()
    {
        var assemblyLocations = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrWhiteSpace(x.Location))
            .Select(x => x.Location)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        assemblyLocations.Add(typeof(object).Assembly.Location);
        assemblyLocations.Add(typeof(Task).Assembly.Location);
        assemblyLocations.Add(typeof(ValueTask).Assembly.Location);
        assemblyLocations.Add(typeof(Enumerable).Assembly.Location);
        assemblyLocations.Add(typeof(JsonSerializer).Assembly.Location);
        assemblyLocations.Add(typeof(IScriptPackageRuntime).Assembly.Location);
        assemblyLocations.Add(typeof(IMessage).Assembly.Location);

        return assemblyLocations
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
