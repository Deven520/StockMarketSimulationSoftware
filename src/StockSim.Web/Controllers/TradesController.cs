using Microsoft.AspNetCore.Mvc;
using StockSim.Application.Dtos;
using StockSim.Application.Services;
using StockSim.Domain.Enums;
using StockSim.Web.Models;

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

    public async Task<IActionResult> Index(string? symbol, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        ViewData["Title"] = "成交明细";
        ViewBag.Symbol = symbol;
        await _trading.GetOrCreateDefaultAccountAsync(ct);
        var all = await _trading.GetTradesAsync(null, null, symbol, ct);
        return View(PagedResult<TradeDto>.From(all, page, pageSize));
    }

    public async Task<IActionResult> Positions(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        ViewData["Title"] = "持仓";
        await _trading.GetOrCreateDefaultAccountAsync(ct);
        var all = await _trading.GetPositionsAsync(ct);
        return View(PagedResult<PositionDto>.From(all, page, pageSize));
    }

    [HttpPost]
    public async Task<IActionResult> Manual(string symbol, OrderSide side, int quantity, decimal price, CancellationToken ct)
    {
        var result = await _trading.PlaceManualOrderAsync(new ManualOrderRequest(symbol, side, quantity, price, null), ct);
        TempData["Msg"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> PnL(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        ViewData["Title"] = "盈亏分析";
        var all = await _pnl.QueryAsync(DateTime.Today.AddDays(-90), DateTime.Today, ct);
        return View(PagedResult<DailyPnLDto>.From(all, page, pageSize));
    }

    [HttpPost]
    public async Task<IActionResult> Settle(CancellationToken ct)
    {
        await _pnl.SettleAsync(DateTime.Today, ct);
        TempData["Msg"] = "日终结算完成";
        return RedirectToAction(nameof(PnL));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteTrade(Guid id, CancellationToken ct)
    {
        var r = await _trading.DeleteTradeAsync(id, ct);
        TempData["Msg"] = r.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ClearPosition(Guid id, CancellationToken ct)
    {
        var r = await _trading.ClearPositionAsync(id, ct);
        TempData["Msg"] = r.Message;
        return RedirectToAction(nameof(Positions));
    }

    [HttpPost]
    public async Task<IActionResult> DeletePnL(Guid id, string? returnUrl, CancellationToken ct)
    {
        var r = await _pnl.DeleteDailyPnLAsync(id, ct);
        TempData["Msg"] = r.Message;
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction(nameof(PnL));
    }
}
