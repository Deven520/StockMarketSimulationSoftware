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

    /// <summary>池内个股收益折线数据（累计已实现 / 初始资金）。</summary>
    [HttpGet]
    public async Task<IActionResult> ReturnSeries(Guid? poolId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var series = await _pnl.GetPoolReturnSeriesAsync(poolId, from, to, ct);
        return Json(new
        {
            dates = series.Dates,
            series = series.Series.Select(s => new
            {
                symbol = s.Symbol,
                name = s.Name,
                tradeStyle = s.TradeStyle.ToString(),
                initialCash = s.InitialCash,
                points = s.Points.Select(p => new
                {
                    date = p.Date,
                    returnPct = p.ReturnPct,
                    dayRealizedPnL = p.DayRealizedPnL,
                    cumRealizedPnL = p.CumRealizedPnL,
                    dayFees = p.DayFees,
                    tradeCount = p.TradeCount,
                    winRate = p.WinRate,
                    availableCash = p.AvailableCash,
                    positionCost = p.PositionCost
                })
            })
        });
    }
}
