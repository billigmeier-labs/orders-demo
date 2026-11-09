using System.Text.Json.Serialization;
using Amazon.AppConfigData;
using Microsoft.AspNetCore.HttpLogging;
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

// JSON logs everywhere but a developer's terminal, each line tagged with AppVersion, AppCommit and AppEnvironment.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddAppJsonConsole(appInfo);
}

builder.Services.AddDbContext<OrdersDbContext>((services, options) =>
    options.UseNpgsql(OrdersDbContext.ConnectionString(services.GetRequiredService<IConfiguration>())));
builder.Services.AddHealthChecks().AddDbContextCheck<OrdersDbContext>(tags: [PlatformEndpoints.ReadyTag]);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConflictExceptionHandler>();
// DataAnnotations on endpoint parameters and public request records turn bad input into a 400 validation problem.
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

// One log line per request: method, path, status and duration.
builder.Services.AddHttpLogging(options =>
{
    // No query strings: they're user input, and the incident agent reads these logs.
    options.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath
        | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(appInfo);
builder.Services.AddSingleton<FaultInjector>(services =>
    new(services.GetRequiredService<IFeatureFlags>(), services.GetRequiredService<ILogger<FaultInjector>>()));

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

builder.Services.AddOptionalTelemetry(builder.Configuration, appInfo);

var app = builder.Build();

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpLogging());
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
