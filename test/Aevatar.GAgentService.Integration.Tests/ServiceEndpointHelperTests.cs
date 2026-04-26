using System.Reflection;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Hosting.DependencyInjection;
using Aevatar.GAgentService.Hosting.Endpoints;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ServiceEndpointHelperTests
{
    [Fact]
    public void ServiceEndpoints_ShouldNormalizeIdentity_AndParseImplementationAndEndpointKinds()
    {
        var toIdentity = typeof(ServiceEndpoints).GetMethod(
            "ToIdentity",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var parseImplementationKind = typeof(ServiceEndpoints).GetMethod(
            "ParseImplementationKind",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var parseEndpointKind = typeof(ServiceEndpoints).GetMethod(
            "ParseEndpointKind",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var identity = (ServiceIdentity)toIdentity.Invoke(null, [" tenant ", " app ", " ns ", " service "])!;
        var staticKind = (ServiceImplementationKind)parseImplementationKind.Invoke(null, [" static "])!;
        var scriptingKind = (ServiceImplementationKind)parseImplementationKind.Invoke(null, ["SCRIPTING"])!;
        var workflowKind = (ServiceImplementationKind)parseImplementationKind.Invoke(null, ["workflow"])!;
        var commandKind = (ServiceEndpointKind)parseEndpointKind.Invoke(null, ["command"])!;
        var chatKind = (ServiceEndpointKind)parseEndpointKind.Invoke(null, [" chat "])!;
        var fallbackKind = (ServiceEndpointKind)parseEndpointKind.Invoke(null, ["unsupported"])!;
        Action invalidImplementation = () => parseImplementationKind.Invoke(null, ["unsupported"]);

        identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "service",
        });
        staticKind.Should().Be(ServiceImplementationKind.Static);
        scriptingKind.Should().Be(ServiceImplementationKind.Scripting);
        workflowKind.Should().Be(ServiceImplementationKind.Workflow);
        commandKind.Should().Be(ServiceEndpointKind.Command);
        chatKind.Should().Be(ServiceEndpointKind.Chat);
        fallbackKind.Should().Be(ServiceEndpointKind.Command);
        invalidImplementation.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Unsupported implementation kind*");
    }

    [Fact]
    public void ServiceBindingEndpoints_ShouldMapAllBindingKinds_AndRejectUnsupportedBindingKind()
    {
        var bindingEndpointType = typeof(ServiceCollectionExtensions).Assembly.GetType(
            "Aevatar.GAgentService.Governance.Hosting.Endpoints.ServiceBindingEndpoints")!;
        var parseBindingKind = bindingEndpointType.GetMethod(
            "ParseBindingKind",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var toSpec = bindingEndpointType.GetMethod(
            "ToSpec",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var requestType = bindingEndpointType.GetNestedType(
            "ServiceBindingHttpRequest",
            BindingFlags.Public)!;
        var boundServiceType = bindingEndpointType.GetNestedType(
            "BoundServiceHttpRequest",
            BindingFlags.Public)!;
        var boundConnectorType = bindingEndpointType.GetNestedType(
            "BoundConnectorHttpRequest",
            BindingFlags.Public)!;
        var boundSecretType = bindingEndpointType.GetNestedType(
            "BoundSecretHttpRequest",
            BindingFlags.Public)!;

        var serviceRequest = Activator.CreateInstance(
            requestType,
            "tenant",
            "app",
            "ns",
            "binding-service",
            "Service dependency",
            "service",
            Activator.CreateInstance(boundServiceType, "orders", "submit", null, null, null),
            null,
            null,
            new[] { "policy-a" })!;
        var connectorRequest = Activator.CreateInstance(
            requestType,
            "tenant",
            "app",
            "ns",
            "binding-connector",
            "Connector dependency",
            "connector",
            null,
            Activator.CreateInstance(boundConnectorType, "mcp", "connector-a"),
            null,
            null)!;
        var secretRequest = Activator.CreateInstance(
            requestType,
            "tenant",
            "app",
            "ns",
            "binding-secret",
            "Secret dependency",
            "secret",
            null,
            null,
            Activator.CreateInstance(boundSecretType, "secret-a"),
            null)!;
        var ownerContext = new ServiceIdentityContext("tenant", "app", "ns", "test");

        var serviceKind = (ServiceBindingKind)parseBindingKind.Invoke(null, ["service"])!;
        var connectorKind = (ServiceBindingKind)parseBindingKind.Invoke(null, [" connector "])!;
        var secretKind = (ServiceBindingKind)parseBindingKind.Invoke(null, ["SECRET"])!;
        var serviceSpec = InvokeToSpec(toSpec, serviceRequest, "binding-service", serviceKind, ownerContext);
        var connectorSpec = InvokeToSpec(toSpec, connectorRequest, "binding-connector", connectorKind, ownerContext);
        var secretSpec = InvokeToSpec(toSpec, secretRequest, "binding-secret", secretKind, ownerContext);
        Action invalidBindingKind = () => parseBindingKind.Invoke(null, ["unsupported"]);

        serviceKind.Should().Be(ServiceBindingKind.Service);
        connectorKind.Should().Be(ServiceBindingKind.Connector);
        secretKind.Should().Be(ServiceBindingKind.Secret);

        serviceSpec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "checkout",
        });
        serviceSpec.BindingKind.Should().Be(ServiceBindingKind.Service);
        serviceSpec.PolicyIds.Should().Equal("policy-a");
        serviceSpec.ServiceRef.Should().NotBeNull();
        serviceSpec.ServiceRef!.Identity.ServiceId.Should().Be("orders");
        serviceSpec.ServiceRef.EndpointId.Should().Be("submit");

        connectorSpec.BindingKind.Should().Be(ServiceBindingKind.Connector);
        connectorSpec.ConnectorRef.Should().NotBeNull();
        connectorSpec.ConnectorRef!.ConnectorType.Should().Be("mcp");
        connectorSpec.ConnectorRef.ConnectorId.Should().Be("connector-a");

        secretSpec.BindingKind.Should().Be(ServiceBindingKind.Secret);
        secretSpec.SecretRef.Should().NotBeNull();
        secretSpec.SecretRef!.SecretName.Should().Be("secret-a");

        invalidBindingKind.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Unsupported binding kind*");
    }

    private static ServiceBindingSpec InvokeToSpec(
        MethodInfo toSpec,
        object request,
        string bindingId,
        ServiceBindingKind bindingKind,
        ServiceIdentityContext ownerContext) =>
        (ServiceBindingSpec)toSpec.Invoke(null, ["checkout", request, bindingId, bindingKind, ownerContext, null])!;

    [Fact]
    public void ServiceServingEndpoints_ShouldParseServingState_AndMapTargetsAndStages()
    {
        var parseServingState = typeof(ServiceEndpoints).GetMethod(
            "ParseServingState",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var toServingTargetSpec = typeof(ServiceEndpoints).GetMethod(
            "ToServingTargetSpec",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var toRolloutStageSpec = typeof(ServiceEndpoints).GetMethod(
            "ToRolloutStageSpec",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var paused = (ServiceServingState)parseServingState.Invoke(null, ["paused"])!;
        var draining = (ServiceServingState)parseServingState.Invoke(null, [" draining "])!;
        var disabled = (ServiceServingState)parseServingState.Invoke(null, ["DISABLED"])!;
        var active = (ServiceServingState)parseServingState.Invoke(null, ["unexpected"])!;

        var targetRequest = new ServiceEndpoints.ServiceServingTargetHttpRequest(
            "rev-2",
            35,
            "draining",
            ["chat", "run"]);
        var targetSpec = (ServiceServingTargetSpec)toServingTargetSpec.Invoke(null, [targetRequest])!;
        var stageRequest = new ServiceEndpoints.ServiceRolloutStageHttpRequest(
            "stage-a",
            [targetRequest]);
        var stageSpec = (ServiceRolloutStageSpec)toRolloutStageSpec.Invoke(null, [stageRequest])!;

        paused.Should().Be(ServiceServingState.Paused);
        draining.Should().Be(ServiceServingState.Draining);
        disabled.Should().Be(ServiceServingState.Disabled);
        active.Should().Be(ServiceServingState.Active);

        targetSpec.RevisionId.Should().Be("rev-2");
        targetSpec.AllocationWeight.Should().Be(35);
        targetSpec.ServingState.Should().Be(ServiceServingState.Draining);
        targetSpec.EnabledEndpointIds.Should().Equal("chat", "run");

        stageSpec.StageId.Should().Be("stage-a");
        stageSpec.Targets.Should().ContainSingle();
        stageSpec.Targets[0].RevisionId.Should().Be("rev-2");
        stageSpec.Targets[0].ServingState.Should().Be(ServiceServingState.Draining);
    }
}
