using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Features.Customers;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Platform;
using Orders.Api.Features.Products;
using Orders.Api.Features.Search;
using Orders.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders().AddJsonConsole(options => options.IncludeScopes = true);
}

builder.Services.AddDbContext<OrdersDbContext>((services, options) =>
    options.UseNpgsql(OrdersDbContext.ConnectionString(services.GetRequiredService<IConfiguration>())));
builder.Services.AddHealthChecks().AddDbContextCheck<OrdersDbContext>(tags: [PlatformEndpoints.ReadyTag]);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConflictExceptionHandler>();
// DataAnnotations on endpoint parameters and public request records -> 400 validation problem.
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFeatureFlags, ConfigurationFeatureFlags>();
builder.Services.AddSingleton(services => AppInfo.From(services.GetRequiredService<IConfiguration>()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Every log line written while handling a request carries the app version, commit and environment.
var logScope = app.Services.GetRequiredService<AppInfo>().ToLogScope();
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Api");
app.Use(async (context, next) =>
{
    using (requestLogger.BeginScope(logScope))
    {
        await next(context);
    }
});

app.MapPlatform();

var api = app.MapGroup("/api");
api.MapCustomers();
api.MapProducts();
api.MapOrders();
api.MapSearch();

await app.PrepareDatabaseAsync();
await app.RunAsync();
