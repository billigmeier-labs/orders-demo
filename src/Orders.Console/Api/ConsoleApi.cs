using Orders.Console.Events;
using Orders.Console.Integrations;
using Orders.Console.Scenarios;
using Orders.Console.Status;

namespace Orders.Console.Api;

public sealed record FlagRequest(bool Enabled);

public sealed record LoadRequest(string? Scenario, int? DurationMinutes);

/// <summary>
/// JSON endpoints used by the page's script and by automation. POSTs need the header
/// <c>X-Console-Action: 1</c> (a cross-site form cannot set it), on top of authentication.
/// </summary>
public static class ConsoleApi
{
    public const string ActionHeader = "X-Console-Action";

    public static void MapConsoleApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/whoami", (HttpContext c) => new
        {
            name = c.User.Identity?.IsAuthenticated == true ? c.User.Identity.Name : null,
            authenticated = c.User.Identity?.IsAuthenticated == true,
            method = c.User.FindFirst("auth_method")?.Value ?? (c.User.Identity?.IsAuthenticated == true ? "google" : "none"),
        });

        api.MapGet("/run", (ScenarioEngine engine) => new { run = engine.Current });
        api.MapGet("/events", (EventStore store, int? count) => store.Latest(Math.Clamp(count ?? 100, 1, 1000)));
        api.MapGet("/status", (StatusService status, CancellationToken ct) => status.GetAsync(ct));
        api.MapGet("/scenarios", (ScenarioCatalog catalog) => catalog.All.Select(s => new
        {
            s.Id,
            s.Title,
            s.Summary,
            s.Tour,
            steps = s.Steps.Select(x => new { x.Id, x.Title, action = x.Action?.Label, x.TimeoutSeconds }),
        }));

        var post = api.MapGroup("").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Request.Headers.ContainsKey(ActionHeader)
                ? await next(ctx)
                : Results.Problem($"POSTs need the {ActionHeader} header.", statusCode: StatusCodes.Status400BadRequest));

        post.MapPost("/scenarios/{id}/start", async (string id, ScenarioEngine engine, CancellationToken ct) =>
            ToResult(await engine.StartAsync(id, ct), engine));
        post.MapPost("/scenarios/{id}/steps/{step}/action", async (string id, string step, ScenarioEngine engine, CancellationToken ct) =>
            ToResult(await engine.RunActionAsync(id, step, ct), engine));
        post.MapPost("/scenarios/{id}/steps/{step}/extend", async (string id, string step, ScenarioEngine engine, CancellationToken ct) =>
            ToResult(await engine.ExtendAsync(id, step, ct), engine));
        post.MapPost("/scenarios/{id}/reset", async (string id, ScenarioEngine engine, CancellationToken ct) =>
            ToResult(await engine.ResetAsync(id, ct), engine));

        post.MapPost("/flags/{name}", async (string name, FlagRequest body, IFeatureFlagService flags, StatusService status, CancellationToken ct) =>
        {
            await flags.SetFlagAsync(name, body.Enabled, ct);
            status.Invalidate(StatusService.FlagsPanel);
            return Results.Ok(new { ok = true, lines = new[] { new ActionLine($"{name} turned {(body.Enabled ? "on" : "off")}") } });
        });

        post.MapPost("/load/start", async (LoadRequest body, ILoadGenerator load, StatusService status, CancellationToken ct) =>
        {
            var scenario = string.IsNullOrWhiteSpace(body.Scenario) ? "normal" : body.Scenario;
            var minutes = Math.Clamp(body.DurationMinutes ?? 15, 1, 120);
            await load.StartAsync(scenario, minutes, ct);
            status.Invalidate(StatusService.LoadPanel);
            return Results.Ok(new { ok = true, lines = new[] { new ActionLine($"Load started: {scenario} for {minutes} min") } });
        });

        post.MapPost("/load/stop", async (ILoadGenerator load, StatusService status, CancellationToken ct) =>
        {
            await load.StopAsync(ct);
            status.Invalidate(StatusService.LoadPanel);
            return Results.Ok(new { ok = true, lines = new[] { new ActionLine("Load stopped") } });
        });
    }

    private static IResult ToResult(EngineResult result, ScenarioEngine engine) => result.Ok
        ? Results.Ok(new { ok = true, lines = result.Lines ?? [], run = engine.Current })
        : Results.Problem(result.Error, statusCode: result.StatusCode);
}
