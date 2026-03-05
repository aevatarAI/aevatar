using Aevatar.App.Host.Api.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.App.Host.Api.Tests;

public sealed class AppStartupValidationTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static readonly Dictionary<string, string?> AllRequired = new()
    {
        ["App:Id"] = "test-app",
        ["App:Storage:BucketName"] = "bucket",
        ["Orleans:ClusterId"] = "dev",
        ["Orleans:ServiceId"] = "app",
        ["Firebase:ProjectId"] = "fb-proj",
    };

    [Fact]
    public void AllRequiredPresent_Production_DoesNotThrow()
    {
        var config = BuildConfig(AllRequired);
        var env = new StubEnv("Production");

        var act = () => AppStartupValidation.ValidateRequiredConfiguration(config, env);

        act.Should().NotThrow();
    }

    [Fact]
    public void MissingAppId_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequired) { ["App:Id"] = null };
        var config = BuildConfig(values);
        var env = new StubEnv("Production");

        var act = () => AppStartupValidation.ValidateRequiredConfiguration(config, env);

        act.Should().Throw<InvalidOperationException>().WithMessage("*App:Id*");
    }

    [Fact]
    public void MissingBucket_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequired) { ["App:Storage:BucketName"] = null };
        var config = BuildConfig(values);
        var env = new StubEnv("Production");

        var act = () => AppStartupValidation.ValidateRequiredConfiguration(config, env);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Bucket*");
    }

    [Fact]
    public void MissingOrleans_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequired) { ["Orleans:ClusterId"] = null };
        var config = BuildConfig(values);
        var env = new StubEnv("Production");

        var act = () => AppStartupValidation.ValidateRequiredConfiguration(config, env);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ClusterId*");
    }

    [Fact]
    public void NonDev_MissingFirebase_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequired) { ["Firebase:ProjectId"] = null };
        var config = BuildConfig(values);
        var env = new StubEnv("Production");

        var act = () => AppStartupValidation.ValidateRequiredConfiguration(config, env);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Firebase*");
    }

    [Fact]
    public void Development_MissingFirebase_DoesNotThrow()
    {
        var values = new Dictionary<string, string?>(AllRequired) { ["Firebase:ProjectId"] = null };
        var config = BuildConfig(values);
        var env = new StubEnv("Development");

        var act = () => AppStartupValidation.ValidateRequiredConfiguration(config, env);

        act.Should().NotThrow();
    }

    [Fact]
    public void MultipleMissingKeys_ListsAllInMessage()
    {
        var values = new Dictionary<string, string?>
        {
            ["App:Storage:BucketName"] = "b",
        };
        var config = BuildConfig(values);
        var env = new StubEnv("Production");

        var act = () => AppStartupValidation.ValidateRequiredConfiguration(config, env);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*App:Id*")
            .WithMessage("*Orleans:ClusterId*")
            .WithMessage("*Orleans:ServiceId*")
            .WithMessage("*Firebase:ProjectId*");
    }
}

file sealed class StubEnv : IHostEnvironment
{
    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "TestApp";
    public string ContentRootPath { get; set; } = "/";
    public IFileProvider ContentRootFileProvider { get; set; } = null!;

    public StubEnv(string environmentName) => EnvironmentName = environmentName;
}
