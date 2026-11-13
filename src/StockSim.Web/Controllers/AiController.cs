using Microsoft.AspNetCore.Mvc;
using StockSim.Application.Dtos;
using StockSim.Application.Services;
using StockSim.Domain.Enums;

namespace StockSim.Web.Controllers;

public class AiController : Controller
{
    private readonly AiAppService _ai;
    private readonly InvestmentGoalAppService _goals;
    private readonly StockPoolAppService _pools;
    private readonly AiQueueAppService _queue;
    private readonly NextDayPickReviewAppService _nextDayPickReviews;

    public AiController(
        AiAppService ai,
        InvestmentGoalAppService goals,
        StockPoolAppService pools,
        AiQueueAppService queue,
        NextDayPickReviewAppService nextDayPickReviews)
    {
        _ai = ai;
        _goals = goals;
        _pools = pools;
        _queue = queue;
        _nextDayPickReviews = nextDayPickReviews;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "AI 工作台";
        ViewBag.CursorStatus = await _ai.GetCursorConnectionStatusAsync(ct);
        ViewBag.Goals = await _goals.ListAsync(ct);
        ViewBag.QueueCounts = await _queue.CountByStatusAsync(ct);
        return View(await _ai.ListJobsAsync(ct));
    }

    /// <summary>目标驱动选股：入队异步执行（Goal→Prompt→选股），立即返回。</summary>
    [HttpPost]
    public async Task<IActionResult> CreateGoal(
        decimal expectedReturnPct,
        int horizonDays,
        string themePreference,
        StockTradeStyle tradeStyle = StockTradeStyle.IntradayT,
        CancellationToken ct = default)
    {
        try
        {
            var (goal, job, queueId) = await _goals.CreateGoalAndPickAsync(
                new CreateInvestmentGoalRequest(expectedReturnPct, horizonDays, themePreference, tradeStyle), ct);
            TempData["Msg"] = $"已提交 AI 队列（任务 {queueId.ToString()[..8]}…）。完成后可在选股结果中勾选入库。";
            TempData["MsgType"] = "success";
            return RedirectToAction(nameof(Queue), new { highlight = queueId });
        }
        catch (Exception ex)
        {
            TempData["Msg"] = ex.Message;
            TempData["MsgType"] = "danger";
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>AI 统一队列：排队中 / 执行中 / 成功失败与入参出参。</summary>
    public async Task<IActionResult> Queue(string? status, Guid? highlight, CancellationToken ct)
    {
        ViewData["Title"] = "AI 队列";
        ViewBag.Highlight = highlight;
        ViewBag.FilterStatus = status;
        ViewBag.Counts = await _queue.CountByStatusAsync(ct);

        AiQueueItemStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<AiQueueItemStatus>(status, true, out var st))
            filter = st;

        return View(await _queue.ListAsync(filter, 150, ct));
    }

    public async Task<IActionResult> QueueDetails(Guid id, CancellationToken ct)
    {
        var detail = await _queue.GetDetailAsync(id, ct);
        if (detail is null) return NotFound();
        ViewData["Title"] = "AI 队列详情";
        return View(detail);
    }

    public async Task<IActionResult> Goals(CancellationToken ct)
    {
        ViewData["Title"] = "投资目标与达成率";
        return View(await _goals.ListAsync(ct));
    }

    public async Task<IActionResult> GoalDetails(Guid id, CancellationToken ct)
    {
        var detail = await _goals.GetDetailAsync(id, ct);
        if (detail is null) return NotFound();
        ViewData["Title"] = "目标详情";
        return View(detail);
    }

    [HttpPost]
    public async Task<IActionResult> RefreshGoalProgress(Guid id, CancellationToken ct)
    {
        await _goals.SettleGoalAsync(id, DateTime.Today, ct);
        await _goals.EvaluateNextDayPicksAsync(id, DateTime.Now, ct);
        TempData["Msg"] = "已刷新今日达成率与次日命中对账";
        return RedirectToAction(nameof(GoalDetails), new { id });
    }

    /// <summary>对已对账的次日预涨入选股入队复盘（供下一批选股注入）。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnqueueNextDayPickReviews(
        Guid? goalId,
        string? targetDate,
        bool force = false,
        CancellationToken ct = default)
    {
        DateTime? day = null;
        if (!string.IsNullOrWhiteSpace(targetDate) && DateTime.TryParse(targetDate, out var parsed))
            day = parsed.Date;

        // 手动入口先补结算/对账，避免「池已结算但 Goal 未写」或漏跑 Worker
        await _pools.SettleNextDayRiseAsync(DateTime.Now, ct);
        await _goals.SettleAllActiveAsync(DateTime.Today, ct);

        if (goalId.HasValue)
        {
            var stocks = (await _goals.GetDetailAsync(goalId.Value, ct))?.Stocks
                ?? Array.Empty<InvestmentGoalStockDto>();
            var queued = 0;
            var done = 0;
            foreach (var s in stocks.Where(x => x.EvaluatedAt.HasValue && x.TargetTradeDate.HasValue))
            {
                if (day.HasValue && s.TargetTradeDate!.Value.Date != day.Value) continue;
                var (ok, _, qid) = await _nextDayPickReviews.EnqueueFromGoalStockAsync(s.Id, force, ct);
                if (ok && qid.HasValue) queued++;
                else if (ok) done++;
            }

            // Goal 侧仍无对账时，回退扫近两周池结算
            if (queued + done == 0)
            {
                var (q2, s2, t2) = await _nextDayPickReviews.EnqueueSettledForDateAsync(
                    day, force, ct: ct);
                queued += q2;
                done += s2;
                if (t2 == 0 && queued + done == 0)
                {
                    TempData["Msg"] = "该目标尚无已对账入选股，且近两周无池内次日预涨结算，未入队。";
                    TempData["MsgType"] = "warning";
                    return RedirectToAction(nameof(GoalDetails), new { id = goalId.Value });
                }
            }

            TempData["Msg"] = $"次日预涨复盘：新入队 {queued}，已有/跳过 {done}" + (force ? "（强制）" : "");
            TempData["MsgType"] = queued > 0 ? "success" : "warning";
            if (queued > 0)
                return RedirectToAction(nameof(Queue));
            return RedirectToAction(nameof(GoalDetails), new { id = goalId.Value });
        }

        var (q, skipped, total) = await _nextDayPickReviews.EnqueueSettledForDateAsync(
            day, force, ct: ct);
        TempData["Msg"] = total == 0
            ? "近两周无已结算的次日预涨记录，未入队复盘。请确认预涨日已收盘结算（池内「预涨结果」有成功/失败）。"
            : $"次日预涨复盘：候选 {total}，新入队 {q}，已有/跳过 {skipped}" + (force ? "（强制）" : "");
        TempData["MsgType"] = q > 0 ? "success" : "warning";
        return q > 0 ? RedirectToAction(nameof(Queue)) : RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(Guid id, Guid? goalId, CancellationToken ct)
    {
        var detail = await _ai.GetJobDetailAsync(id, ct);
        if (detail is null) return NotFound();

        ViewData["Title"] = "AI 选股查阅";
        ViewBag.Pools = await _pools.GetPoolsAsync(ct);
        if (goalId.HasValue)
        {
            ViewBag.GoalId = goalId.Value;
            ViewBag.Goal = await _goals.GetDetailAsync(goalId.Value, ct);
        }
        else
        {
            var goals = await _goals.ListAsync(ct);
            var linked = goals.FirstOrDefault(g => g.StockPickJobId == id);
            if (linked is not null)
            {
                ViewBag.GoalId = linked.Id;
                ViewBag.Goal = await _goals.GetDetailAsync(linked.Id, ct);
            }
        }

        return View(detail);
    }

    public IActionResult Suggestions(Guid jobId) => RedirectToAction(nameof(Details), new { id = jobId });

    /// <summary>勾选多只建议股确认入池并激活目标。</summary>
    [HttpPost]
    public async Task<IActionResult> AcceptGoalPicks(Guid goalId, Guid jobId, Guid[]? suggestionIds, CancellationToken ct)
    {
        var ids = suggestionIds ?? Array.Empty<Guid>();
        var result = await _goals.AcceptSuggestionsAsync(goalId, ids, ct);
        TempData["Msg"] = result.Message;
        TempData["MsgType"] = result.Ok ? "success" : "danger";
        return result.Ok
            ? RedirectToAction(nameof(GoalDetails), new { id = goalId })
            : RedirectToAction(nameof(Details), new { id = jobId, goalId });
    }

    [HttpPost]
    public async Task<IActionResult> Review(
        Guid jobId,
        Guid suggestionId,
        bool accept,
        Guid? poolId,
        Guid? goalId,
        CancellationToken ct)
    {
        if (goalId.HasValue && accept)
        {
            var result = await _goals.AcceptSuggestionsAsync(goalId.Value, [suggestionId], ct);
            TempData["Msg"] = result.Message;
            TempData["MsgType"] = result.Ok ? "success" : "danger";
            return result.Ok
                ? RedirectToAction(nameof(GoalDetails), new { id = goalId })
                : RedirectToAction(nameof(Details), new { id = jobId, goalId });
        }

        var pool = poolId ?? (await _pools.GetOrCreateDefaultPoolAsync(ct)).Id;
        var r = await _ai.ReviewSuggestionAsync(jobId, suggestionId, accept, pool, null, ct: ct);
        TempData["Msg"] = r.Message;
        return RedirectToAction(nameof(Details), new { id = jobId, goalId });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteQueueItem(Guid id, CancellationToken ct)
    {
        var r = await _queue.DeleteAsync(id, ct);
        TempData["Msg"] = r.Message;
        TempData["MsgType"] = r.Ok ? "success" : "danger";
        return RedirectToAction(nameof(Queue));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteGoal(Guid id, CancellationToken ct)
    {
        var r = await _goals.DeleteAsync(id, ct);
        TempData["Msg"] = r.Message;
        TempData["MsgType"] = r.Ok ? "success" : "danger";
        return RedirectToAction(nameof(Goals));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteJob(Guid id, CancellationToken ct)
    {
        var r = await _ai.DeleteJobAsync(id, ct);
        TempData["Msg"] = r.Message;
        TempData["MsgType"] = r.Ok ? "success" : "danger";
        return RedirectToAction(nameof(Index));
    }
}
