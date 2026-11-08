using Amazon;
using Amazon.AppConfig;
using Amazon.CloudWatch;
using Amazon.CloudWatchLogs;
using Amazon.CostExplorer;
using Amazon.ECS;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Orders.Console;
using Orders.Console.Api;
using Orders.Console.Auth;
using Orders.Console.Events;
using Orders.Console.Integrations;
using Orders.Console.Integrations.Aws;
using Orders.Console.Integrations.Fakes;
using Orders.Console.Integrations.GitHub;
using Orders.Console.Integrations.Http;
using Orders.Console.Scenarios;
using Orders.Console.Status;

var builder = WebApplication.CreateBuilder(args);
var options = ConsoleOptions.FromConfiguration(builder.Configuration);
if (!options.AuthEnabled && !options.Fakes)
{
    throw new InvalidOperationException("CONSOLE_AUTH=off is only allowed with CONSOLE_FAKES=true.");
}

var services = builder.Services;
services.AddSingleton(options);
services.AddSingleton(TimeProvider.System);
services.AddProblemDetails();
services.AddHealthChecks();
services.AddRazorPages(o => o.Conventions.AddPageRoute("/Index", "status"));

// Behind the ALB: trust X-Forwarded-For/Proto from any hop (the ALB's addresses are not fixed).
services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var dataProtection = services.AddDataProtection().SetApplicationName("orders-console");
if (options.DataProtectionKeysPath is not null)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeysPath));
}

services.AddConsoleAuth(options);

if (options.Fakes)
{
    services.AddSingleton<FakeWorld>();
    services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<FakeWorld>());
    services.AddSingleton<IEventSource>(sp => sp.GetRequiredService<FakeWorld>());
    services.AddSingleton<IMonitoringService>(sp => sp.GetRequiredService<FakeWorld>());
    services.AddSingleton<IDeploymentService>(sp => sp.GetRequiredService<FakeWorld>());
    services.AddSingleton<ICostService>(sp => sp.GetRequiredService<FakeWorld>());
    services.AddSingleton<IFeatureFlagService>(sp => sp.GetRequiredService<FakeWorld>());
    services.AddSingleton<ILoadGenerator>(sp => sp.GetRequiredService<FakeWorld>());
    services.AddSingleton<IEnvironmentProbe>(sp => sp.GetRequiredService<FakeWorld>());
}
else
{
    var region = RegionEndpoint.GetBySystemName(options.AwsRegion);
    services.AddSingleton<ETagCache>();
    services.AddTransient<ETagCacheHandler>();
    services.AddHttpClient("github", c => GitHubService.Configure(c, options)).AddHttpMessageHandler<ETagCacheHandler>();
    services.AddHttpClient("probe", c => c.Timeout = TimeSpan.FromSeconds(5));
    services.AddSingleton(sp => ActivatorUtilities.CreateInstance<GitHubService>(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient("github")));
    services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<GitHubService>());
    services.AddSingleton<IEventSource>(sp => sp.GetRequiredService<GitHubService>());

    services.AddSingleton<IAmazonCloudWatch>(_ => new AmazonCloudWatchClient(region));
    services.AddSingleton<IAmazonCloudWatchLogs>(_ => new AmazonCloudWatchLogsClient(region));
    services.AddSingleton<IAmazonECS>(_ => new AmazonECSClient(region));
    services.AddSingleton<IAmazonAppConfig>(_ => new AmazonAppConfigClient(region));
    services.AddSingleton<IAmazonCostExplorer>(_ => new AmazonCostExplorerClient(RegionEndpoint.USEast1));

    services.AddSingleton<AwsMonitoring>();
    services.AddSingleton<IMonitoringService>(sp => sp.GetRequiredService<AwsMonitoring>());
    services.AddSingleton<IEventSource>(sp => sp.GetRequiredService<AwsMonitoring>());
    services.AddSingleton<AwsDeployments>();
    services.AddSingleton<IDeploymentService>(sp => sp.GetRequiredService<AwsDeployments>());
    services.AddSingleton<IEventSource>(sp => sp.GetRequiredService<AwsDeployments>());
    services.AddSingleton<ICostService, AwsCost>();
    services.AddSingleton<IFeatureFlagService, AwsFeatureFlags>();
    services.AddSingleton<ILoadGenerator, AwsLoadGenerator>();
    services.AddSingleton<IEnvironmentProbe>(sp => new EnvironmentProbe(sp.GetRequiredService<IHttpClientFactory>().CreateClient("probe"), options));
}

services.AddSingleton<EventStore>();
services.AddSingleton<SourceHealth>();
services.AddSingleton<StatusService>();
services.AddSingleton<ScoreboardService>();
services.AddSingleton<IScenarioActions, ScenarioActions>();
services.AddSingleton<IConditionEvaluator, ConditionEvaluator>();
services.AddSingleton(sp => new ScenarioCatalog(
    ScenarioCatalog.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "Scenarios", "Definitions")),
    ScenarioActions.KnownKinds,
    sp.GetRequiredService<IConditionEvaluator>().Names));
services.AddSingleton<ScenarioEngine>();
services.AddHostedService<EventCollector>();

var app = builder.Build();

// Fail at startup, not on first use, if a scenario file is invalid.
_ = app.Services.GetRequiredService<ScenarioCatalog>();
if (!options.Fakes && options.GitHubToken is null)
{
    StartupLog.NoGitHubToken(app.Logger);
}

StartupLog.Starting(app.Logger, options.Fakes, options.AuthEnabled, options.GitHubRepository);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseIdentityLogScope();
app.UseAuthorization();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapGet("/signout", async (HttpContext c) =>
{
    await AuthSetup.SignOutAsync(c);
    return Results.Redirect("/");
});
app.MapRazorPages();
app.MapConsoleApi();

app.Run();

public partial class Program;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "GITHUB_TOKEN is not set; GitHub calls are anonymous and rate limited")]
    public static partial void NoGitHubToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Console starting: fakes={Fakes}, auth={Auth}, repo={Repo}")]
    public static partial void Starting(ILogger logger, bool fakes, bool auth, string repo);
}
