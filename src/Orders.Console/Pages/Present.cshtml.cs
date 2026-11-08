using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orders.Console.Scenarios;

namespace Orders.Console.Pages;

public sealed class PresentModel(ScenarioCatalog catalog, ScenarioEngine engine, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "scenario")]
    public string? ScenarioId { get; set; }

    [BindProperty(SupportsGet = true, Name = "view")]
    public string? ViewStep { get; set; }

    public RunView View { get; private set; } = default!;

    public string PollUrl => $"/present?handler=Live&scenario={Uri.EscapeDataString(View.Scenario.Id)}" + (ViewStep is null ? "" : $"&view={Uri.EscapeDataString(ViewStep)}");

    public IActionResult OnGet()
    {
        if (!Load())
        {
            return NotFound();
        }

        return Page();
    }

    public IActionResult OnGetLive()
    {
        if (!Load())
        {
            return NotFound();
        }

        return Partial("_RunLive", View);
    }

    private bool Load()
    {
        var scenario = ScenarioId is null ? catalog.Tour : catalog.Find(ScenarioId);
        if (scenario is null)
        {
            return false;
        }

        View = RunView.Build(catalog, scenario, engine.Current, ViewStep, time.GetUtcNow());
        return true;
    }
}
