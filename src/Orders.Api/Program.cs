using System.Text.Json.Serialization;
using Amazon.AppConfigData;
using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Features.Customers;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Platform;
using Orders.Api.Features.Products;
using Orders.Api.Features.Search;
using Orders.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var appInfo = AppInfo.From(builder.Configuration);

// Serilog: JSON logs everywhere but a developer's terminal, each line tagged with the release, and during a
// request with its id, journey, flag values and trace id (Infrastructure/Logging.cs).
builder.AddAppLogging(appInfo);

builder.Services.AddDbContext<OrdersDbContext>((services, options) =>
    options.UseNpgsql(OrdersDbContext.ConnectionString(services.GetRequiredService<IConfiguration>())));
builder.Services.AddHealthChecks().AddDbContextCheck<OrdersDbContext>(tags: [PlatformEndpoints.ReadyTag]);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConflictExceptionHandler>();
// DataAnnotations on endpoint parameters and public request records turn bad input into a 400 validation problem.
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(appInfo);
builder.Services.AddSingleton<FaultInjector>();

// Flags come from AppConfig when APPCONFIG_APPLICATION is set (ECS), otherwise from configuration.
if (AppConfigSettings.From(builder.Configuration) is { } appConfig)
{
    builder.Services.AddSingleton(appConfig);
    builder.Services.AddSingleton<IAmazonAppConfigData>(_ => new AmazonAppConfigDataClient());
    builder.Services.AddSingleton<AppConfigFeatureFlags>();
    builder.Services.AddSingleton<IFeatureFlags>(services => services.GetRequiredService<AppConfigFeatureFlags>());
    builder.Services.AddHostedService<AppConfigPoller>();
}
else
{
    builder.Services.AddSingleton<IFeatureFlags, ConfigurationFeatureFlags>();
}

// The app reads flags through Microsoft.FeatureManagement, fed by IFeatureFlags (Infrastructure/FeatureManagementSetup.cs).
builder.Services.AddAppFeatureManagement();

builder.Services.AddOptionalTelemetry(builder.Configuration, appInfo);

var app = builder.Build();

app.UseRequestMetricTags();
// One summary line per request (no query strings), with the request's context on every line it logs.
app.UseAppRequestLogging();
// Unhandled exceptions are logged once, with the stack, by the exception handler; EF Core's own
// failure logs are switched off in appsettings.json so the same error doesn't appear three times.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapPlatform();

var api = app.MapGroup("/api");
api.MapCustomers();
api.MapProducts();
api.MapOrders();
api.MapSearch();

await app.PrepareDatabaseAsync();
await app.RunAsync();
