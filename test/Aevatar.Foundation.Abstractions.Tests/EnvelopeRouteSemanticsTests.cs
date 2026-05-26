using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class EnvelopeRouteSemanticsTests
{
    [Fact]
    public void CreateMethods_ShouldNormalizeNullInputs()
    {
        var topology = EnvelopeRouteSemantics.CreateTopologyPublication(null!, TopologyAudience.Children);
        topology.PublisherActorId.ShouldBeEmpty();
        topology.GetTopologyAudience().ShouldBe(TopologyAudience.Children);

        var direct = EnvelopeRouteSemantics.CreateDirect(null!, null!);
        direct.PublisherActorId.ShouldBeEmpty();
        direct.GetTargetActorId().ShouldBeEmpty();

        var observer = EnvelopeRouteSemantics.CreateObserverPublication(null!, ObserverAudience.CommittedFacts);
        observer.PublisherActorId.ShouldBeEmpty();
        observer.GetObserverAudience().ShouldBe(ObserverAudience.CommittedFacts);
    }

    [Fact]
    public void AccessorsAndPredicates_ShouldHandleNullAndMismatchedRoutes()
    {
        EnvelopeRoute? nullRoute = null;
        nullRoute.GetTopologyAudience().ShouldBe(TopologyAudience.Unspecified);
        nullRoute.GetObserverAudience().ShouldBe(ObserverAudience.Unspecified);
        nullRoute.GetTargetActorId().ShouldBeEmpty();
        nullRoute.IsPublication().ShouldBeFalse();
        nullRoute.IsTopologyPublication().ShouldBeFalse();
        nullRoute.IsObserverPublication().ShouldBeFalse();
        nullRoute.IsDirect().ShouldBeFalse();

        var direct = EnvelopeRouteSemantics.CreateDirect("publisher", "target");
        direct.IsDirect().ShouldBeTrue();
        direct.IsPublication().ShouldBeFalse();
        direct.IsTopologyPublication().ShouldBeFalse();
        direct.IsObserverPublication().ShouldBeFalse();
        direct.GetTopologyAudience().ShouldBe(TopologyAudience.Unspecified);
        direct.GetObserverAudience().ShouldBe(ObserverAudience.Unspecified);
        direct.GetTargetActorId().ShouldBe("target");

        var topology = EnvelopeRouteSemantics.CreateTopologyPublication("publisher", TopologyAudience.ParentAndChildren);
        topology.IsPublication().ShouldBeTrue();
        topology.IsTopologyPublication().ShouldBeTrue();
        topology.IsObserverPublication().ShouldBeFalse();
        topology.IsDirect().ShouldBeFalse();
        topology.GetTopologyAudience().ShouldBe(TopologyAudience.ParentAndChildren);
        topology.GetObserverAudience().ShouldBe(ObserverAudience.Unspecified);
        topology.GetTargetActorId().ShouldBeEmpty();

        var observer = EnvelopeRouteSemantics.CreateObserverPublication("publisher", ObserverAudience.CommittedFacts);
        observer.IsPublication().ShouldBeTrue();
        observer.IsObserverPublication().ShouldBeTrue();
        observer.IsTopologyPublication().ShouldBeFalse();
        observer.IsDirect().ShouldBeFalse();
        observer.GetObserverAudience().ShouldBe(ObserverAudience.CommittedFacts);
        observer.GetTopologyAudience().ShouldBe(TopologyAudience.Unspecified);
        observer.GetTargetActorId().ShouldBeEmpty();
    }

    [Fact]
    public void Describe_ShouldCoverAllRouteShapes()
    {
        EnvelopeRouteSemantics.CreateDirect("publisher", "target")
            .Describe()
            .ShouldBe(nameof(DirectRoute));

        EnvelopeRouteSemantics.CreateTopologyPublication("publisher", TopologyAudience.Self)
            .Describe()
            .ShouldBe(TopologyAudience.Self.ToString());

        EnvelopeRouteSemantics.CreateObserverPublication("publisher", ObserverAudience.CommittedFacts)
            .Describe()
            .ShouldBe(ObserverAudience.CommittedFacts.ToString());

        new EnvelopeRoute
        {
            Publication = new PublicationRoute(),
        }.Describe().ShouldBe(nameof(PublicationRoute));

        ((EnvelopeRoute?)null).Describe().ShouldBe(TopologyAudience.Unspecified.ToString());
    }
}
