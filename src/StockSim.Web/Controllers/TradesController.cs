using Microsoft.AspNetCore.Mvc;
using StockSim.Application.Dtos;
using StockSim.Application.Services;
using StockSim.Domain.Enums;

namespace StockSim.Web.Controllers;

public class TradesController : Controller
{
    private readonly TradingAppService _trading;
    private readonly PnLAppService _pnl;

    public TradesController(TradingAppService trading, PnLAppService pnl)
    {
        _trading = trading;
        _pnl = pnl;
    }

    public async Task<IActionResult> Index(string? symbol, CancellationToken ct)
    {
        ViewData["Title"] = "成交明细";
        await _trading.GetOrCreateDefaultAccountAsync(ct);
        return View(await _trading.GetTradesAsync(null, null, symbol, ct));
    }

    public async Task<IActionResult> Positions(CancellationToken ct)
    {
        ViewData["Title"] = "持仓";
        await _trading.GetOrCreateDefaultAccountAsync(ct);
        return View(await _trading.GetPositionsAsync(ct));
    }

    [HttpPost]
    public async Task<IActionResult> Manual(string symbol, OrderSide side, int quantity, decimal price, CancellationToken ct)
    {
        var result = await _trading.PlaceManualOrderAsync(new ManualOrderRequest(symbol, side, quantity, price, null), ct);
        TempData["Msg"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> PnL(CancellationToken ct)
    {
        ViewData["Title"] = "盈亏分析";
        return View(await _pnl.QueryAsync(DateTime.Today.AddDays(-90), DateTime.Today, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Settle(CancellationToken ct)
    {
        await _pnl.SettleAsync(DateTime.Today, ct);
        TempData["Msg"] = "日终结算完成";
        return RedirectToAction(nameof(PnL));
    }
}
