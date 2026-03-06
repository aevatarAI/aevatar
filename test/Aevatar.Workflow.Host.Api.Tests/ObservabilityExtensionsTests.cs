using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class ObservabilityExtensionsTests
{
    private const double DefaultRatio = 0.1d;

    private static readonly MethodInfo ResolveSamplingRatioMethod =
        Type.GetType("Aevatar.Workflow.Host.Api.ObservabilityExtensions, Aevatar.Workflow.Host.Api", throwOnError: true)!
            .GetMethod("ResolveSamplingRatio", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void ResolveSamplingRatio_WhenNotConfigured_ShouldReturnDefault()
    {
        var builder = CreateBuilder();

        var ratio = InvokeResolveSamplingRatio(builder, DefaultRatio);

        ratio.Should().Be(DefaultRatio);
    }

    [Theory]
    [InlineData("Observability:Tracing:SampleRatio", "0.25", 0.25)]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "1", 1.0)]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "0", 0.0)]
    public void ResolveSamplingRatio_WhenValidConfigured_ShouldReturnParsedValue(
        string key,
        string value,
        double expected)
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [key] = value,
        });

        var ratio = InvokeResolveSamplingRatio(builder, DefaultRatio);

        ratio.Should().Be(expected);
    }

    [Theory]
    [InlineData("Observability:Tracing:SampleRatio", "NaN")]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "Infinity")]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "-Infinity")]
    [InlineData("Observability:Tracing:SampleRatio", "abc")]
    [InlineData("Observability:Tracing:SampleRatio", "1.5")]
    public void ResolveSamplingRatio_WhenConfiguredInvalid_ShouldThrow(
        string key,
        string value)
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [key] = value,
        });

        var act = () => InvokeResolveSamplingRatio(builder, DefaultRatio);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{value}'*'{key}'*finite number between 0 and 1*");
    }

    private static WebApplicationBuilder CreateBuilder(Dictionary<string, string?>? values = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        if (values is not null)
            builder.Configuration.AddInMemoryCollection(values);

        return builder;
    }

    private static double InvokeResolveSamplingRatio(WebApplicationBuilder builder, double defaultValue)
    {
        try
        {
            return (double)ResolveSamplingRatioMethod.Invoke(null, [builder, defaultValue])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
