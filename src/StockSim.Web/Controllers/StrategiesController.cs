using Microsoft.AspNetCore.Mvc;
using StockSim.Application.Dtos;
using StockSim.Application.Services;

namespace StockSim.Web.Controllers;

public class StrategiesController : Controller
{
    private readonly StrategyAppService _strategies;
    private readonly StockPoolAppService _pools;

    public StrategiesController(StrategyAppService strategies, StockPoolAppService pools)
    {
        _strategies = strategies;
        _pools = pools;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "策略中心";
        ViewBag.Pools = await _pools.GetPoolsAsync(ct);
        return View(await _strategies.ListAsync(ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string name, string? description, CancellationToken ct)
    {
        await _strategies.CreateAsync(new CreateStrategyRequest(name, description, null), ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> UpdateParams(Guid id, string parametersJson, string? changeNote, CancellationToken ct)
    {
        await _strategies.UpdateParamsAsync(id, new UpdateStrategyParamsRequest(parametersJson, changeNote), ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Bind(Guid strategyId, Guid poolId, CancellationToken ct)
    {
        await _strategies.BindPoolAsync(strategyId, poolId, ct: ct);
        TempData["Msg"] = "已绑定股票池";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Toggle(Guid id, bool enabled, CancellationToken ct)
    {
        await _strategies.SetEnabledAsync(id, enabled, ct);
        return RedirectToAction(nameof(Index));
    }
}
