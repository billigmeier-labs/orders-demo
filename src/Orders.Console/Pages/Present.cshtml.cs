using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orders.Console.Scenarios;
using Orders.Console.Status;

namespace Orders.Console.Pages;

public sealed class PresentModel(ScenarioCatalog catalog, ScenarioEngine engine, ScoreboardService scoreboard, StatusService status, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "scenario")]
    public string? ScenarioId { get; set; }

    [BindProperty(SupportsGet = true, Name = "view")]
    public string? ViewStep { get; set; }

    public RunView View { get; private set; } = default!;

    public string PollUrl => $"/present?handler=Live&scenario={Uri.EscapeDataString(View.Scenario.Id)}" + (ViewStep is null ? "" : $"&view={Uri.EscapeDataString(ViewStep)}");

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnGetLiveAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }

        return Partial("_RunLive", View);
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var scenario = ScenarioId is null ? catalog.Tour : catalog.Find(ScenarioId);
        if (scenario is null)
        {
            return false;
        }

        var view = RunView.Build(catalog, scenario, engine.Current, ViewStep, time.GetUtcNow());
        var focus = view.FocusStep.Def;
        var done = view.FocusStep.State == StepStates.Done;
        View = view with
        {
            Scoreboard = focus.Action?.Kind == "scoreboard.show" && done ? scoreboard.Latest : null,
            PastRuns = focus.Action?.Kind == "scoreboard.pastRuns" && done ? scoreboard.LatestPastRuns : null,
            Traffic = focus.Stage == "traffic" ? await status.GetTrafficAsync(ct) : null,
        };
        return true;
    }
}
