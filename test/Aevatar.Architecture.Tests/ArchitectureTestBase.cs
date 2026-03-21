using System.Reflection;
using ArchUnitNET.Loader;

namespace Aevatar.Architecture.Tests;

public static class ArchitectureTestBase
{
    private static readonly Lazy<ArchitectureModel> LazyArchitecture = new(() =>
    {
        // Load all Aevatar assemblies from the test output directory.
        // This ensures we pick up all referenced production assemblies
        // regardless of whether they have been JIT-loaded by the CLR.
        var outputDir = Path.GetDirectoryName(typeof(ArchitectureTestBase).Assembly.Location)!;
        var aevatarDlls = Directory.GetFiles(outputDir, "Aevatar.*.dll")
            .Where(f => !Path.GetFileName(f).Contains(".Tests."))
            .ToArray();

        var assemblies = aevatarDlls
            .Select(dll =>
            {
                try { return Assembly.LoadFrom(dll); }
                catch { return null; }
            })
            .Where(a => a != null)
            .Cast<Assembly>()
            .ToArray();

        return new ArchLoader()
            .LoadAssemblies(assemblies)
            .Build();
    });

    public static ArchitectureModel ProductionArchitecture => LazyArchitecture.Value;
}
