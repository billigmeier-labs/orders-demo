using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orders.Console.Status;

namespace Orders.Console.Pages;

public sealed class IndexModel(StatusService status, ConsoleOptions options) : PageModel
{
    public StatusSnapshot Snapshot { get; private set; } = default!;

    public int RefreshSeconds => options.StatusRefreshSeconds;

    public async Task OnGetAsync(CancellationToken ct) => Snapshot = await status.GetAsync(ct);

    public async Task<IActionResult> OnGetLiveAsync(CancellationToken ct) => Partial("_StatusLive", await status.GetAsync(ct));
}
