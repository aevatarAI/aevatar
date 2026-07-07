using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCapabilityHmacSecretValidationTests
{
    private const string ShortSecret = "too-short-secret";      // 16 bytes < 32
    private const string SufficientSecret = "0123456789abcdef0123456789abcdef"; // 32 bytes

    [Fact]
    public void WebhookIngress_WhenEnabledBindingSecretTooShort_ShouldThrowOnStartupValidation()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{WorkflowWebhookIngressOptions.SectionName}:Enabled"] = "true",
            [$"{WorkflowWebhookIngressOptions.SectionName}:Bindings:0:RouteKey"] = "primary",
            [$"{WorkflowWebhookIngressOptions.SectionName}:Bindings:0:HmacSecret"] = ShortSecret,
        });

        var act = () => BuildAndValidate(configuration);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{WorkflowWebhookIngressOptions.SectionName}*");
    }

    [Fact]
    public void WebhookIngress_WhenEnabledBindingSecretSufficient_ShouldPassStartupValidation()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{WorkflowWebhookIngressOptions.SectionName}:Enabled"] = "true",
            [$"{WorkflowWebhookIngressOptions.SectionName}:Bindings:0:RouteKey"] = "primary",
            [$"{WorkflowWebhookIngressOptions.SectionName}:Bindings:0:HmacSecret"] = SufficientSecret,
        });

        var act = () => BuildAndValidate(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void WebhookIngress_WhenDisabled_ShouldNotValidateShortSecret()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{WorkflowWebhookIngressOptions.SectionName}:Enabled"] = "false",
            [$"{WorkflowWebhookIngressOptions.SectionName}:Bindings:0:RouteKey"] = "primary",
            [$"{WorkflowWebhookIngressOptions.SectionName}:Bindings:0:HmacSecret"] = ShortSecret,
        });

        var act = () => BuildAndValidate(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void ExternalApprovalCallback_WhenEnabledBindingSecretTooShort_ShouldThrowOnStartupValidation()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{WorkflowExternalApprovalCallbackOptions.SectionName}:Enabled"] = "true",
            [$"{WorkflowExternalApprovalCallbackOptions.SectionName}:Bindings:0:RouteKey"] = "primary",
            [$"{WorkflowExternalApprovalCallbackOptions.SectionName}:Bindings:0:HmacSecret"] = ShortSecret,
        });

        var act = () => BuildAndValidate(configuration);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{WorkflowExternalApprovalCallbackOptions.SectionName}*");
    }

    [Fact]
    public void ExternalApprovalCallback_WhenEnabledBindingSecretSufficient_ShouldPassStartupValidation()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{WorkflowExternalApprovalCallbackOptions.SectionName}:Enabled"] = "true",
            [$"{WorkflowExternalApprovalCallbackOptions.SectionName}:Bindings:0:RouteKey"] = "primary",
            [$"{WorkflowExternalApprovalCallbackOptions.SectionName}:Bindings:0:HmacSecret"] = SufficientSecret,
        });

        var act = () => BuildAndValidate(configuration);

        act.Should().NotThrow();
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void BuildAndValidate(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddWorkflowCapability(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }
}
