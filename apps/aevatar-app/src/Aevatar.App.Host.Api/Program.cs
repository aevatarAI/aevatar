using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Concurrency;
using Aevatar.App.Application.Errors;
using Aevatar.App.Application.Projection.DependencyInjection;
using Aevatar.App.Application.Services;
using Aevatar.App.Host.Api.Endpoints;
using Aevatar.App.Host.Api.Hosting;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Extensions.Hosting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
ConfigureFallbackConfiguration(builder.Configuration);
using var startupLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
AppStartupValidation.ValidateRequiredConfiguration(
    builder.Configuration,
    builder.Environment,
    startupLoggerFactory.CreateLogger("Startup"));

// ── Aevatar Runtime (Orleans Silo) ──
builder.AddAevatarDefaultHost(configureHost: options =>
{
    options.ServiceName = "Aevatar.App.Host.Api";
    options.EnableWebSockets = true;
    options.EnableConnectorBootstrap = true;
    options.EnableActorRestoreOnStartup = true;
});
builder.AddAppDistributedOrleansHost();
builder.AddWorkflowCapabilityWithAIDefaults();

// ── Auth ──
builder.Services.AddScoped<IAppAuthContextAccessor, AppAuthContextAccessor>();
builder.Services.Configure<AppAuthOptions>(options =>
{
    options.FirebaseProjectId =
        builder.Configuration["Firebase:ProjectId"] ?? string.Empty;
    options.TrialTokenSecret =
        builder.Configuration["App:TrialTokenSecret"]
        ?? "dev-secret-32-chars-minimum-here!";
    options.TrialAuthEnabled = builder.Environment.IsDevelopment()
        && builder.Configuration.GetValue<bool>("App:TrialAuthEnabled");
});
builder.Services.AddSingleton<IAppAuthService, AppAuthService>();
var authBuilder = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = AppAuthSchemeProvider.AppAuthScheme;
        options.DefaultChallengeScheme = AppAuthSchemeProvider.AppAuthScheme;
    })
    .AddPolicyScheme(AppAuthSchemeProvider.AppAuthScheme, "App auth scheme", options =>
    {
        options.ForwardDefaultSelector = context => AppAuthSchemeProvider.SelectScheme(context);
    })
    .AddScheme<AuthenticationSchemeOptions, FirebaseAuthHandler>(AppAuthSchemeProvider.FirebaseScheme, _ => { });
if (builder.Environment.IsDevelopment())
    authBuilder.AddScheme<AuthenticationSchemeOptions, TrialAuthHandler>(AppAuthSchemeProvider.TrialScheme, _ => { });

// ── AI / Storage / Concurrency ──
builder.Services.AddOptions<AppQuotaOptions>()
    .Bind(builder.Configuration.GetSection("App:Quota"));
builder.Services.PostConfigure<AppQuotaOptions>(options => options.Normalize());

builder.Services.AddOptions<FallbackOptions>()
    .Configure<IConfiguration>((options, config) =>
    {
        config.GetSection("fallbacks").Bind(options);
        config.GetSection("App:Fallbacks").Bind(options);
    });

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.AllowTrailingCommas = true;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddSingleton<IActorAccessAppService, ActorAccessAppService>();
builder.Services.AddSingleton<IImageConcurrencyCoordinator>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var maxTotal = cfg.GetValue<int?>("App:ImageConcurrency:MaxTotal") ?? 20;
    var maxQueueSize = cfg.GetValue<int?>("App:ImageConcurrency:MaxQueueSize") ?? 100;
    var queueTimeoutMs = cfg.GetValue<int?>("App:ImageConcurrency:QueueTimeoutMs") ?? 30_000;
    return new ImageConcurrencyCoordinator(maxTotal, maxQueueSize, queueTimeoutMs);
});
var projectionProvider = builder.Configuration["App:Projection:Provider"];
if (string.Equals(projectionProvider, "Elasticsearch", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddAppElasticsearchProjection(builder.Configuration);
builder.Services.AddAppProjection();
builder.Services.AddSingleton<IFallbackContent, FallbackContent>();
builder.Services.AddSingleton<IAIGenerationAppService, AIGenerationAppService>();
builder.Services.AddSingleton<IAuthAppService, AuthAppService>();
builder.Services.AddSingleton<IGenerationAppService, GenerationAppService>();
builder.Services.Configure<Aevatar.App.Application.Completion.CompletionPortOptions>(
    builder.Configuration.GetSection(Aevatar.App.Application.Completion.CompletionPortOptions.SectionName));
if (string.Equals(
        builder.Configuration["ActorRuntime:OrleansPersistenceBackend"], "Garnet",
        StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<Aevatar.App.Application.Completion.ICompletionPort,
        Aevatar.App.Host.Api.Completion.RedisCompletionPort>();
else
    builder.Services.AddSingleton<Aevatar.App.Application.Completion.ICompletionPort,
        Aevatar.App.Application.Completion.InMemoryCompletionPort>();
builder.Services.AddSingleton<ISyncAppService, SyncAppService>();
builder.Services.AddSingleton<IUserAppService, UserAppService>();
builder.Services.AddSingleton<IValidator<Aevatar.App.Application.Contracts.EntityDto>, Aevatar.App.Application.Validation.EntityValidator>();
builder.Services.AddSingleton<IValidator<Aevatar.App.Application.Contracts.SyncRequestDto>, Aevatar.App.Application.Validation.SyncRequestValidator>();
builder.Services.Configure<ImageStorageOptions>(builder.Configuration.GetSection("App:Storage"));
builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ImageStorageOptions>>().Value;
    var s3Config = new Amazon.S3.AmazonS3Config();
    if (!string.IsNullOrWhiteSpace(opts.Region))
        s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(opts.Region);
    if (string.IsNullOrWhiteSpace(opts.AccessKeyId))
        return new Amazon.S3.AmazonS3Client(s3Config);
    return new Amazon.S3.AmazonS3Client(opts.AccessKeyId, opts.SecretAccessKey, s3Config);
});
builder.Services.AddSingleton<IS3StorageClient, AwsS3StorageClient>();
builder.Services.AddSingleton<IImageStorageAppService, ImageStorageAppService>();

// ── Tolt (Referral) ──
builder.Services.Configure<ToltOptions>(builder.Configuration.GetSection(ToltOptions.SectionName));
builder.Services.AddHttpClient<IToltAppService, ToltAppService>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<ToltOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(opts.ApiKey))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
});

// ── RevenueCat Webhook ──
builder.Services.AddSingleton<IRevenueCatWebhookHandler, RevenueCatWebhookHandler>();

// ── CORS ──
var allowedOrigins = builder.Configuration["App:AllowedOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? ["http://localhost:3000"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowCredentials()
              .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
              .WithHeaders("Content-Type", "Authorization", "x-app-user-id");
    });
});

var app = builder.Build();
app.UseCors();
app.UseAevatarDefaultHost();
app.UseAuthentication();

// ── Error handling ──
app.UseMiddleware<AppErrorMiddleware>();

// ── sendBeacon compat (text/plain → application/json) ──
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/sync")
        && context.Request.Method == "POST"
        && context.Request.ContentType?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) == true)
    {
        context.Request.ContentType = "application/json";
    }
    await next();
});

// ── Auth middleware (only for /api/* paths that require auth) ──
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/users")
        || context.Request.Path.StartsWithSegments("/api/state")
        || context.Request.Path.StartsWithSegments("/api/sync")
        || context.Request.Path.StartsWithSegments("/api/generate")
        || context.Request.Path.StartsWithSegments("/api/upload")
        || context.Request.Path.StartsWithSegments("/api/referral/bind"),
    branch => branch.UseMiddleware<AppUserProvisioningMiddleware>());

// Optional auth for public endpoint enhancements.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/remote-config"),
    branch => branch.UseMiddleware<OptionalAuthMiddleware>());

// ── Endpoints ──
app.MapHealthEndpoints();
app.MapConfigEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapStateEndpoints();
app.MapSyncEndpoints();
app.MapGenerateEndpoints();
app.MapUploadEndpoints();
app.MapReferralEndpoints();
app.MapWebhookEndpoints();

app.Run();

static void ConfigureFallbackConfiguration(ConfigurationManager configuration)
{
    var appHome = configuration["AEVATAR_HOME"];
    if (!string.IsNullOrWhiteSpace(appHome))
    {
        configuration.AddJsonFile(
            Path.Combine(appHome, "config", "fallbacks.json"),
            optional: true,
            reloadOnChange: false);
        configuration.AddJsonFile(
            Path.Combine(appHome, "fallbacks.json"),
            optional: true,
            reloadOnChange: false);
        return;
    }

    configuration.AddJsonFile("config/fallbacks.json", optional: true, reloadOnChange: false);
    configuration.AddJsonFile("fallbacks.json", optional: true, reloadOnChange: false);
}

