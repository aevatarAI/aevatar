using Aevatar.Studio.Application;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class AppScopedScriptServiceTests
{
    [Fact]
    public void AppScopedScriptService_ShouldNotDependOnScriptStorageUploadPort()
    {
        var applicationAssembly = typeof(AppScopedScriptService).Assembly;

        applicationAssembly
            .GetTypes()
            .Should()
            .NotContain(type => type.IsInterface &&
                                type.Name.Contains("StoragePort", StringComparison.Ordinal) &&
                                type.Name.Contains("Script", StringComparison.Ordinal));

        var dependencyTypeNames = typeof(AppScopedScriptService)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .Concat(typeof(AppScopedScriptService)
                .GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public)
                .Select(field => field.FieldType.Name))
            .ToArray();

        dependencyTypeNames.Should().NotContain(typeName =>
            typeName.Contains("ScriptStorage", StringComparison.Ordinal) ||
            typeName.Contains("StoragePort", StringComparison.Ordinal));
    }
}
