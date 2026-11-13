using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StockSim.Application.Dtos;
using StockSim.Application.Services;
using StockSim.Application.Services.Alerts;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;

namespace StockSim.Web.Controllers;

public class PoolsController : Controller
{
    private readonly StockPoolAppService _pools;
    private readonly StockDetailAppService _stockDetails;
    private readonly PerStockStrategyService _perStock;
    private readonly AiQueueAppService _aiQueue;
    private readonly DownsideRiskAppService _downsideRisk;
    private readonly DailyTReviewAppService _dailyTReviews;
    private readonly NextDayPickReviewAppService _nextDayPickReviews;
    private readonly IStockSuggestProvider _suggest;
    private readonly ITradeAlertNotifier _alerts;
    private readonly IStockSimDataStore _db;

    public PoolsController(
        StockPoolAppService pools,
        StockDetailAppService stockDetails,
        PerStockStrategyService perStock,
        AiQueueAppService aiQueue,
        DownsideRiskAppService downsideRisk,
        DailyTReviewAppService dailyTReviews,
        NextDayPickReviewAppService nextDayPickReviews,
        IStockSuggestProvider suggest,
        ITradeAlertNotifier alerts,
        IStockSimDataStore db)
    {
        _pools = pools;
        _stockDetails = stockDetails;
        _perStock = perStock;
        _aiQueue = aiQueue;
        _downsideRisk = downsideRisk;
        _dailyTReviews = dailyTReviews;
        _nextDayPickReviews = nextDayPickReviews;
        _suggest = suggest;
        _alerts = alerts;
        _db = db;
    }

    /// <summary>股票池即成分页（无池列表主页）。</summary>
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var pool = await _pools.GetOrCreateDefaultPoolAsync(ct);
        // 收盘后打开池页即尝试结算次日预涨（幂等）
        await _pools.SettleNextDayRiseAsync(DateTime.Now, ct);
        ViewData["Title"] = "股票池";
        ViewBag.PoolId = pool.Id;
        ViewBag.PoolName = pool.Name;
        return View("Details", await _stockDetails.GetPoolStockRowsAsync(pool.Id, ct));
    }

    /// <summary>兼容旧链接 /Pools/Details/{id} → 统一进成分页。</summary>
    public Task<IActionResult> Details(Guid? id, CancellationToken ct) => Index(ct);

    /// <summary>股票池轻量行情快照（首屏后异步刷）。</summary>
    [HttpGet]
    public async Task<IActionResult> Quotes(Guid poolId, CancellationToken ct)
    {
        var snaps = await _stockDetails.GetPoolQuotesAsync(poolId, ct);
        return Json(snaps.Select(q => new
        {
            symbol = q.Symbol,
            lastPrice = q.LastPrice,
            changePercent = q.ChangePercent,
            peTtm = q.PeTtm,
            pb = q.Pb,
            marketValue = q.MarketValue,
            unrealizedPnL = q.UnrealizedPnL,
            unrealizedPnLPct = q.UnrealizedPnLPct
        }));
    }

    /// <summary>个股详情面板（同花顺风格摘要：日K/分时/资金/估值 + 策略运行状态）。</summary>
    public async Task<IActionResult> Stock(string symbol, Guid? poolId, CancellationToken ct)
    {
        var page = await _stockDetails.GetStockDetailPageAsync(symbol, poolId, null, ct);
        if (page is null) return NotFound();
        ViewData["Title"] = $"{page.Bundle.Board.Name}({page.Bundle.Board.Symbol})";
        return View(page);
    }

    /// <summary>个股操作记录（成交/拒单）与持仓盈亏。</summary>
    public async Task<IActionResult> Operations(string symbol, Guid? poolId, CancellationToken ct)
    {
        var page = await _stockDetails.GetStockOperationsAsync(symbol, poolId, ct);
        if (page is null) return NotFound();
        ViewData["Title"] = $"{page.Name} 操作明细";
        return View(page);
    }

    /// <summary>日K点击下钻：返回指定交易日分时/分钟K JSON。</summary>
    [HttpGet]
    public async Task<IActionResult> Intraday(string symbol, string date, CancellationToken ct)
    {
        if (!DateTime.TryParse(date, out var tradeDate))
        {
            return BadRequest(new { message = "日期格式无效" });
        }

        var chart = await _stockDetails.GetIntradayChartAsync(symbol, tradeDate, ct);
        if (chart is null || chart.Bars.Count == 0)
        {
            return NotFound(new { message = "该日暂无分时/分K数据" });
        }

        return Json(new
        {
            symbol = chart.Symbol,
            tradeDate = chart.TradeDate,
            open = chart.Open,
            high = chart.High,
            low = chart.Low,
            close = chart.Close,
            preClose = chart.PreClose,
            bars = chart.Bars.Select(b => new
            {
                time = b.Time,
                open = b.Open,
                close = b.Close,
                low = b.Low,
                high = b.High,
                volume = b.Volume
            }),
            trades = (chart.Trades ?? Array.Empty<ChartTradeMarkDto>()).Select(t => new
            {
                date = t.Date,
                time = t.Time,
                kind = t.Kind,
                side = t.Side,
                price = t.Price,
                quantity = t.Quantity,
                isStrategyT = t.IsStrategyT
            })
        });
    }

    /// <summary>股票代码/名称模糊联想。</summary>
    [HttpGet]
    public async Task<IActionResult> Suggest(string q, CancellationToken ct)
    {
        var list = await _suggest.SuggestAsync(q ?? "", 10, ct);
        return Json(list.Select(x => new { symbol = x.Symbol, name = x.Name, market = x.Market }));
    }

    /// <summary>代码或名称填其一即可；默认策略入库后异步 AI 刷新。</summary>
    [HttpPost]
    public async Task<IActionResult> AddItem(
        Guid poolId,
        string? symbol,
        string? name,
        string? tags,
        StockTradeStyle tradeStyle = StockTradeStyle.IntradayT,
        CancellationToken ct = default)
    {
        var (item, msg) = await _pools.AddItemSmartAsync(poolId, symbol, name, tags, tradeStyle: tradeStyle, ct: ct);
        TempData["Msg"] = msg;
        TempData["MsgType"] = item is null ? "danger" : "success";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>手动触发该成分的 AI 策略刷新（入队异步，不阻塞页面）。</summary>
    [HttpPost]
    public async Task<IActionResult> RefreshStrategy(Guid poolId, Guid itemId, string? symbol, string? returnTo, CancellationToken ct)
    {
        var item = await _db.StockPoolItems.FindAsync([itemId], ct);
        if (item is null)
        {
            TempData["Msg"] = "成分不存在";
            TempData["MsgType"] = "danger";
            return RedirectToAction(nameof(Index));
        }

        var q = await _aiQueue.EnqueueStrategyRefreshAsync(item.Id, item.Symbol, item.Name, nightly: true, ct);
        TempData["Msg"] = $"已加入 AI 队列：{q.Title}";
        TempData["MsgType"] = "success";
        if (string.Equals(returnTo, "stock", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(symbol))
            return RedirectToAction(nameof(Stock), new { symbol, poolId });
        return RedirectToAction("Queue", "Ai", new { highlight = q.Id });
    }

    /// <summary>预测下一交易日下跌风险（入队；规则快检 + AI；开市/非开市均可）。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PredictDownsideRisk(Guid poolId, Guid itemId, CancellationToken ct)
    {
        var result = await _downsideRisk.EnqueuePredictAsync(itemId, ct);
        TempData["Msg"] = result.Message;
        TempData["MsgType"] = result.Ok ? "success" : "warning";
        if (result.Ok && result.QueueId.HasValue)
            return RedirectToAction("Queue", "Ai", new { highlight = result.QueueId.Value });
        return RedirectToAction(nameof(Index));
    }

    /// <summary>做T股每日复盘入队（收盘后/手动；force 可重跑）。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnqueueDailyTReview(
        Guid poolId,
        Guid itemId,
        string? symbol,
        string? tradeDate,
        bool force = false,
        CancellationToken ct = default)
    {
        DateTime? day = null;
        if (!string.IsNullOrWhiteSpace(tradeDate) && DateTime.TryParse(tradeDate, out var parsed))
            day = parsed.Date;

        var result = await _dailyTReviews.EnqueueAsync(itemId, day, force, ct);
        TempData["Msg"] = result.Message;
        TempData["MsgType"] = result.Ok ? "success" : "warning";
        if (result.Ok && result.QueueId.HasValue)
            return RedirectToAction("Queue", "Ai", new { highlight = result.QueueId.Value });
        if (!string.IsNullOrWhiteSpace(symbol))
            return RedirectToAction(nameof(Stock), new { symbol, poolId });
        return RedirectToAction(nameof(Index));
    }

    /// <summary>手动全池 AI 策略刷新（入队，由后台串行执行）。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshAllStrategies(Guid poolId, CancellationToken ct)
    {
        var ensured = await _perStock.EnsureMissingStrategiesAsync(ct);
        var n = await _aiQueue.EnqueueStrategyRefreshAllAsync(nightly: true, ct);
        TempData["Msg"] = $"已入队全池策略刷新 {n} 只（补建默认策略 {ensured.Ok}/{ensured.Fail}），请到 AI 队列查看进度。";
        TempData["MsgType"] = "success";
        return RedirectToAction("Queue", "Ai");
    }

    /// <summary>全池做T股每日复盘入队（等同 15:30 Worker；仅 IntradayT）。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnqueueDailyTReviewAll(
        Guid poolId,
        string? tradeDate,
        bool force = false,
        CancellationToken ct = default)
    {
        DateTime? day = null;
        if (!string.IsNullOrWhiteSpace(tradeDate) && DateTime.TryParse(tradeDate, out var parsed))
            day = parsed.Date;

        var (queued, skippedOrDone, total) = await _dailyTReviews.EnqueueAllIntradayTAsync(day, force, ct);
        if (total == 0)
        {
            TempData["Msg"] = "池内无做T股（或周末），未入队复盘。";
            TempData["MsgType"] = "warning";
            return RedirectToAction(nameof(Index));
        }

        TempData["Msg"] =
            $"全池每日复盘：做T {total} 只，新入队 {queued}，已跳过/已有结果 {skippedOrDone}"
            + (force ? "（强制重跑）" : "")
            + "。请到 AI 队列查看进度。";
        TempData["MsgType"] = queued > 0 ? "success" : "warning";
        return queued > 0
            ? RedirectToAction("Queue", "Ai")
            : RedirectToAction(nameof(Details), new { id = poolId });
    }

    /// <summary>已结算次日预涨复盘入队（目标入选股优先；无 Goal 时用池成分）。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnqueueNextDayPickReviewAll(
        Guid poolId,
        string? targetDate,
        bool force = false,
        CancellationToken ct = default)
    {
        // 空日期：扫近两周已结算；指定日：仅该日。先补跑池结算。
        DateTime? day = null;
        if (!string.IsNullOrWhiteSpace(targetDate) && DateTime.TryParse(targetDate, out var parsed))
            day = parsed.Date;

        await _pools.SettleNextDayRiseAsync(DateTime.Now, ct);
        var (queued, skippedOrDone, total) =
            await _nextDayPickReviews.EnqueueSettledForDateAsync(day, force, ct: ct);
        if (total == 0)
        {
            TempData["Msg"] =
                "近两周无已结算的次日预涨记录，未入队复盘。请确认预涨日已收盘且池内「预涨结果」已有成功/失败。";
            TempData["MsgType"] = "warning";
            return RedirectToAction(nameof(Details), new { id = poolId });
        }

        TempData["Msg"] =
            $"次日预涨复盘：候选 {total}，新入队 {queued}，已有/跳过 {skippedOrDone}"
            + (force ? "（强制重跑）" : "")
            + "。请到 AI 队列查看进度。";
        TempData["MsgType"] = queued > 0 ? "success" : "warning";
        return queued > 0
            ? RedirectToAction("Queue", "Ai")
            : RedirectToAction(nameof(Details), new { id = poolId });
    }

    /// <summary>开关监控：策略成交推送通知。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNotifyOnTrade(Guid poolId, Guid itemId, string? notifyOn, CancellationToken ct)
    {
        // 接受 1/0、true/false；缺省视为开启（点「未监控」期望打开）
        var enabled = string.IsNullOrWhiteSpace(notifyOn)
            || notifyOn is "1" or "true" or "True" or "on" or "yes";
        if (notifyOn is "0" or "false" or "False" or "off" or "no")
            enabled = false;

        var result = await _pools.SetNotifyOnTradeAsync(itemId, enabled, ct);
        TempData["Msg"] = result.Message;
        TempData["MsgType"] = result.Ok ? "success" : "danger";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>发送一条测试成交通知（用 Web 的 Alerts 配置）。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestNotify(Guid poolId, Guid? itemId, CancellationToken ct)
    {
        string symbol = "000000";
        string name = "监控测试";
        string? strategyName = "测试推送";

        if (itemId.HasValue)
        {
            var item = await _db.StockPoolItems.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == itemId.Value, ct);
            if (item is null)
            {
                TempData["Msg"] = "成分不存在";
                TempData["MsgType"] = "danger";
                return RedirectToAction(nameof(Index));
            }

            symbol = item.Symbol;
            name = item.Name;
            strategyName = item.NotifyOnTrade ? "测试推送(已监控)" : "测试推送(未监控)";
        }

        var result = await _alerts.SendTestAsync(new TradeAlertMessage(
            symbol,
            name,
            "测试买入",
            100,
            10.00m,
            DateTime.Now,
            strategyName), ct);

        TempData["Msg"] = result.Message;
        TempData["MsgType"] = result.Ok ? "success" : "danger";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> RemoveItem(Guid poolId, Guid itemId, CancellationToken ct)
    {
        await _pools.RemoveItemAsync(itemId, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteForecast(Guid id, string symbol, Guid? poolId, CancellationToken ct)
    {
        var r = await _perStock.DeleteForecastAsync(id, ct);
        TempData["Msg"] = r.Message;
        TempData["MsgType"] = r.Ok ? "success" : "danger";
        return RedirectToAction(nameof(Stock), new { symbol, poolId });
    }

    [HttpPost]
    public async Task<IActionResult> Import(Guid poolId, IFormFile file, CancellationToken ct)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        var csv = await reader.ReadToEndAsync(ct);
        var result = await _pools.ImportCsvAsync(poolId, csv, ct);
        TempData["Msg"] = $"导入成功 {result.Ok} 条，失败 {result.Fail} 条";
        return RedirectToAction(nameof(Index));
    }
}
