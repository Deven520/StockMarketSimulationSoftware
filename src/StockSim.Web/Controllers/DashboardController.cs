using Microsoft.AspNetCore.Mvc;
using StockSim.Application.Services;

namespace StockSim.Web.Controllers;

public class DashboardController : Controller
{
    private readonly PnLAppService _pnl;

    public DashboardController(PnLAppService pnl) => _pnl = pnl;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "监控总览";
        var dto = await _pnl.GetDashboardAsync(ct);
        var history = await _pnl.QueryAsync(DateTime.Today.AddDays(-30), DateTime.Today, ct);
        ViewBag.History = history;
        return View(dto);
    }
}
