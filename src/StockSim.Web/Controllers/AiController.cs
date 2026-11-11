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

    public AiController(
        AiAppService ai,
        InvestmentGoalAppService goals,
        StockPoolAppService pools,
        AiQueueAppService queue)
    {
        _ai = ai;
        _goals = goals;
        _pools = pools;
        _queue = queue;
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
