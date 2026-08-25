using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class NyxIdRequestSelectorContractTests
{
    [Fact]
    public void Selector_ShouldExposeTypedRiskAttestation()
    {
        var field = NyxIdRequestSelector.Descriptor.FindFieldByName("risk");

        field.Should().NotBeNull();
        field!.EnumType.Name.Should().Be(nameof(NyxIdOperationRisk));
    }

    [Fact]
    public void TryNormalize_ShouldPreserveExplicitReadOnlyPostRisk()
    {
        var selector = ValidSelector("/api/query");
        selector.Method = NyxIdRequestMethod.Post;
        selector.BodyMode = NyxIdRequestBodyMode.Json;
        selector.BodyRequired = true;
        selector.Risk = NyxIdOperationRisk.ReadOnly;

        var succeeded = NyxIdRequestSelectorContract.TryNormalize(
            selector,
            out var normalized,
            out var error);

        succeeded.Should().BeTrue(error);
        normalized.Risk.Should().Be(NyxIdOperationRisk.ReadOnly);
    }

    [Fact]
    public void IsDurableEligible_ShouldAcceptExplicitReadOnlyPost()
    {
        var selector = ValidSelector("/api/query");
        selector.Method = NyxIdRequestMethod.Post;
        selector.BodyMode = NyxIdRequestBodyMode.Json;
        selector.BodyRequired = true;
        selector.Risk = NyxIdOperationRisk.ReadOnly;

        NyxIdRequestSelectorContract.TryResolveRisk(
            selector.Method,
            selector.Risk,
            out var effectiveRisk).Should().BeTrue();
        NyxIdRequestSelectorContract.SupportsDurableExecution(
            selector.Method,
            effectiveRisk).Should().BeTrue();
    }

    [Fact]
    public void RequestContractDigest_ShouldBindDeclaredRisk()
    {
        var methodDerived = ValidSelector();
        var explicitlyReadOnly = methodDerived.Clone();
        explicitlyReadOnly.Risk = NyxIdOperationRisk.ReadOnly;

        WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(methodDerived)
            .Should().NotBe(WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(explicitlyReadOnly));
    }

    [Theory]
    [InlineData(NyxIdRequestMethod.Put)]
    [InlineData(NyxIdRequestMethod.Patch)]
    [InlineData(NyxIdRequestMethod.Delete)]
    public void TryNormalize_ShouldRejectReadOnlyRiskForMutatingMethods(NyxIdRequestMethod method)
    {
        var selector = ValidSelector("/api/resources/{resource_id}");
        selector.Method = method;
        selector.BodyMode = method == NyxIdRequestMethod.Delete
            ? NyxIdRequestBodyMode.None
            : NyxIdRequestBodyMode.Json;
        selector.Risk = NyxIdOperationRisk.ReadOnly;

        var succeeded = NyxIdRequestSelectorContract.TryNormalize(
            selector,
            out _,
            out var error);

        succeeded.Should().BeFalse();
        error.Should().Contain("risk");
    }

    [Theory]
    [InlineData("Accept")]
    [InlineData("accept")]
    [InlineData("If-Match")]
    [InlineData("IF-MATCH")]
    [InlineData("If-None-Match")]
    [InlineData("if-none-match")]
    public void TryNormalize_ShouldAllowWorkflowSafeHeadersCaseInsensitively(string headerName)
    {
        var selector = ValidSelector();
        selector.HeaderParameters.Add(headerName);

        var succeeded = NyxIdRequestSelectorContract.TryNormalize(
            selector,
            out var normalized,
            out var error);

        succeeded.Should().BeTrue(error);
        normalized.HeaderParameters.Should().Equal(headerName);
    }

    [Theory]
    [InlineData("X-Business-Control")]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    public void TryNormalize_ShouldRejectHeadersOutsideWorkflowSafeAllowlist(string headerName)
    {
        var selector = ValidSelector();
        selector.HeaderParameters.Add(headerName);

        var succeeded = NyxIdRequestSelectorContract.TryNormalize(
            selector,
            out _,
            out var error);

        succeeded.Should().BeFalse();
        error.Should().Contain("headers");
    }

    [Theory]
    [InlineData("/api/{resource_id}", "resource_id")]
    [InlineData("/api/{_resource1}", "_resource1")]
    [InlineData("/api/{A1}", "A1")]
    public void TryNormalize_ShouldAllowAsciiPathPlaceholderNames(
        string pathTemplate,
        string expectedPlaceholder)
    {
        var selector = ValidSelector(pathTemplate);

        var succeeded = NyxIdRequestSelectorContract.TryNormalize(
            selector,
            out var normalized,
            out var error);

        succeeded.Should().BeTrue(error);
        NyxIdRequestSelectorContract.PathParameters(normalized)
            .Should().Equal(expectedPlaceholder);
    }

    [Theory]
    [InlineData("/api/{\u8D44\u6E90}")]
    [InlineData("/api/{\u00E9clair}")]
    [InlineData("/api/{resource_\u03C0}")]
    public void TryNormalize_ShouldRejectNonAsciiPathPlaceholderNames(string pathTemplate)
    {
        var selector = ValidSelector(pathTemplate);

        var succeeded = NyxIdRequestSelectorContract.TryNormalize(
            selector,
            out _,
            out var error);

        succeeded.Should().BeFalse();
        error.Should().Contain("placeholders");
    }

    private static NyxIdRequestSelector ValidSelector(
        string pathTemplate = "/api/resources/{resource_id}") =>
        new()
        {
            UserServiceId = "usvc-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = pathTemplate,
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
}
