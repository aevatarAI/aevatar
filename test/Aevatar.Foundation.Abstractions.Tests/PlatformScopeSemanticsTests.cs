using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class PlatformScopeSemanticsTests
{
    [Fact]
    public void ReservedPlatformScope_ShouldHaveOneFoundationContractOwner()
    {
        var contractType = typeof(EnvelopeRouteSemantics).Assembly.GetType(
            "Aevatar.Foundation.Abstractions.PlatformScopeSemantics");

        contractType.ShouldNotBeNull();
        var field = contractType.GetField("ReservedPlatformScopeId");
        field.ShouldNotBeNull();
        field.IsLiteral.ShouldBeTrue();
        field.GetRawConstantValue().ShouldBe("platform:aevatar");
    }
}
