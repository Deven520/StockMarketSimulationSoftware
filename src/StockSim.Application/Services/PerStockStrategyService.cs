using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Application.Services;

/// <summary>
/// 一股一策：入池自动 AI 生成；夜间结合当日数据更新专属策略；并维护次日高低价预测与准确率。
/// </summary>
public class PerStockStrategyService
{
    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly IStockDetailProvider _details;
    private readonly IMarketNewsProvider _news;
    private readonly StrategyAppService _strategies;
    private readonly DailyTReviewAppService _dailyTReviews;
    private readonly ILogger<PerStockStrategyService> _logger;

    public PerStockStrategyService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        IStockDetailProvider details,
        IMarketNewsProvider news,
        StrategyAppService strategies,
        DailyTReviewAppService dailyTReviews,
        ILogger<PerStockStrategyService> logger)
    {
        _db = db;
        _ai = ai;
        _details = details;
        _news = news;
        _strategies = strategies;
        _dailyTReviews = dailyTReviews;
        _logger = logger;
    }

    /// <summary>入池即时：创建默认参数专属策略（不调 AI）。</summary>
    public async Task<(bool Ok, string Message)> EnsureDefaultStrategyAsync(Guid poolItemId, CancellationToken ct = default)
    {
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct);
        if (item is null) return (false, "池成分不存在");
        if (item.BoundStrategyId.HasValue) return (true, "已有专属策略");

        var created = await _strategies.CreateAsync(new Dtos.CreateStrategyRequest(
            DefaultStrategyName(item.Name),
            $"池内专属策略 | {item.Symbol} {item.Name}",
            StrategyParameters.Default().ToJson(),
            "入池默认策略"), ct);

        await BindSymbolAsync(created.Id, item.StockPoolId, item.Symbol, ct);
        item.BoundStrategyId = created.Id;
        item.StrategyGeneratedAt = DateTime.UtcNow;
        item.StrategyUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (true, $"已为 {item.Name} 创建默认策略");
    }

    /// <summary>新股票入池后 AI 生成/刷新（可后台调用）。</summary>
    public Task<Dtos.StrategyAiRunResult> GenerateForNewItemAsync(Guid poolItemId, CancellationToken ct = default)
        => GenerateOrRefreshAsync(poolItemId, isNightlyUpdate: false, ct);

    /// <summary>夜间刷新：在原有策略上更新参数版本。</summary>
    public Task<Dtos.StrategyAiRunResult> RefreshItemAsync(Guid poolItemId, CancellationToken ct = default)
        => GenerateOrRefreshAsync(poolItemId, isNightlyUpdate: true, ct);

    /// <summary>清理假行情污染的 Pending 预测与策略内 forecast（打开个股页/刷新前可调用）。</summary>
    public Task PurgeImplausibleForecastArtifactsAsync(string symbol, CancellationToken ct = default)
        => InvalidateImplausiblePendingForecastsAsync(symbol, ct);

    /// <summary>
    /// 东财详情失败但轻量报价成功时，拼最小真实包，避免 AI 刷新被「模拟」误拒。
    /// </summary>
    private static StockDetailBundle BuildMinimalRealBundleFromLightQuote(
        LightStockQuote light,
        StockDetailBundle? maybeSimulated,
        string? dataSourceNote = null)
    {
        var last = light.Last;
        var pre = light.PreClose is > 0 ? light.PreClose.Value : last;
        var bars = maybeSimulated is { DailyBars.Count: > 0 } && !maybeSimulated.IsSimulated
            ? maybeSimulated.DailyBars
            : Array.Empty<DailyBar>();
        // 模拟日K价位与真价差一个数量级，绝不能带进 AI
        if (maybeSimulated?.IsSimulated == true)
            bars = Array.Empty<DailyBar>();

        return new StockDetailBundle
        {
            Board = new StockQuoteBoard
            {
                Symbol = light.Symbol,
                Name = light.Name,
                Last = last,
                Open = last,
                High = last,
                Low = last,
                PreClose = pre,
                Change = last - pre,
                ChangePercent = pre > 0 ? (last - pre) / pre * 100m : light.ChangePercent,
                Volume = 0,
                Amount = 0,
                TurnoverRate = 0,
                AsOf = light.AsOf
            },
            Fundamentals = new StockFundamentals
            {
                PeTtm = light.PeTtm,
                Pb = light.Pb,
                Industry = light.Industry
            },
            FundFlow = new FundFlowSnapshot { AsOf = light.AsOf },
            DailyBars = bars,
            MinuteBars = Array.Empty<MinuteBar>(),
            DataSourceNote = dataSourceNote
                ?? "数据来源：轻量真价恢复（详情分时/日K不完整，已跳过模拟回退）。",
            IsSimulated = false
        };
    }

    /// <summary>刷新全部池内已绑定策略的股票（每晚 22:00）。</summary>
    public async Task<(int Ok, int Fail)> RefreshAllBoundItemsAsync(CancellationToken ct = default)
    {
        var ids = await _db.StockPoolItems
            .Where(i => i.BoundStrategyId != null)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var ok = 0;
        var fail = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await RefreshItemAsync(id, ct);
                if (result.Ok) ok++;
                else
                {
                    fail++;
                    _logger.LogWarning("Nightly strategy refresh failed for {ItemId}: {Msg}", id, result.Message);
                }
            }
            catch (Exception ex)
            {
                fail++;
                _logger.LogError(ex, "Nightly strategy refresh exception for {ItemId}", id);
            }
        }

        return (ok, fail);
    }

    /// <summary>为尚无专属策略的池成分补建默认策略（兼容旧数据，不调 AI）。</summary>
    public async Task<(int Ok, int Fail)> EnsureMissingStrategiesAsync(CancellationToken ct = default)
    {
        var ids = await _db.StockPoolItems
            .Where(i => i.BoundStrategyId == null)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var ok = 0;
        var fail = 0;
        foreach (var id in ids)
        {
            var r = await EnsureDefaultStrategyAsync(id, ct);
            if (r.Ok) ok++;
            else fail++;
        }

        return (ok, fail);
    }

    public async Task<IReadOnlyList<Dtos.PriceRangeForecastDto>> GetForecastHistoryAsync(
        string symbol,
        int take = 30,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        var rows = await _db.PriceRangeForecasts
            .Where(f => f.Symbol == symbol)
            .OrderByDescending(f => f.ForecastForDate)
            .ThenByDescending(f => f.PredictedAt)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<(bool Ok, string Message)> DeleteForecastAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.PriceRangeForecasts.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null) return (false, "预测记录不存在");
        _db.PriceRangeForecasts.Remove(row);
        await _db.SaveChangesAsync(ct);
        return (true, "已删除预测记录");
    }

    private async Task<Dtos.StrategyAiRunResult> GenerateOrRefreshAsync(
        Guid poolItemId,
        bool isNightlyUpdate,
        CancellationToken ct)
    {
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct);
        if (item is null) return new Dtos.StrategyAiRunResult(false, "池成分不存在");

        // AI 路径与健康检查对齐：先轻量真实报价；详情/日K 尽力而为，失败不挡预测
        LightStockQuote? light = null;
        StockDetailBundle? detail = null;
        try
        {
            light = await _details.GetLightQuoteAsync(item.Symbol, item.Name, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "AI refresh light quote soft-fail for {Symbol}", item.Symbol);
        }

        try
        {
            detail = await _details.GetDetailAsync(item.Symbol, item.Name, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "AI refresh GetDetail soft-fail for {Symbol}", item.Symbol);
        }

        if ((detail is null || detail.IsSimulated) && light is { IsSimulated: false, Last: > 0 })
        {
            _logger.LogWarning(
                "AI refresh {Symbol}: detail missing/simulated but light quote ok (Last={Last}); using minimal real bundle",
                item.Symbol, light.Last);
            detail = BuildMinimalRealBundleFromLightQuote(light, detail);
        }
        else if (detail is { IsSimulated: false }
                 && (detail.DailyBars is null || detail.DailyBars.Count == 0)
                 && light is { IsSimulated: false, Last: > 0 })
        {
            // 有报价无日K：用轻量包覆盖 Board，保留非模拟标记
            detail = BuildMinimalRealBundleFromLightQuote(light, detail);
        }

        // 行情源全挂：用库内最近真实价作锚点降级预测（仍拒绝纯模拟价）
        var usedDbAnchor = false;
        if (detail is null || detail.IsSimulated)
        {
            var refPx = await ResolveLastKnownRealPriceAsync(item.Symbol, ct);
            if (refPx is > 0)
            {
                usedDbAnchor = true;
                _logger.LogWarning(
                    "AI refresh {Symbol}: market feeds empty; using DB last-known real price {Px} as degraded anchor",
                    item.Symbol, refPx);
                detail = BuildMinimalRealBundleFromLightQuote(
                    new LightStockQuote
                    {
                        Symbol = item.Symbol,
                        Name = item.Name,
                        Last = refPx.Value,
                        ChangePercent = 0,
                        PreClose = refPx.Value,
                        AsOf = DateTime.Now,
                        IsSimulated = false
                    },
                    detail,
                    "数据来源：库内最近真实价锚点（行情源暂不可用；降级预测且不计入准确率）。");
            }
        }

        // 先作废价位离谱的 Pending（假数据污染），无论本次是否继续
        await InvalidateImplausiblePendingForecastsAsync(item.Symbol, ct);

        if (detail is null || detail.IsSimulated)
        {
            var why = detail is null
                ? "行情详情为空（东财/公开备用/库内锚点均不可用）"
                : $"行情为模拟/回退（{detail.DataSourceNote}）";
            _logger.LogWarning(
                "Refuse AI strategy refresh for {Symbol}: {Why} — will not write fake-price forecasts",
                item.Symbol, why);
            return new Dtos.StrategyAiRunResult(
                false,
                $"已拒绝 AI 刷新 {item.Symbol}：{why}。请待真实行情恢复后再点「AI刷新预测」，以免写入假价格。");
        }

        // 无日K 或库内锚点：仍可预测，但不计入准确率（降级）
        var degradedNoDaily = usedDbAnchor
                              || detail.DailyBars is null
                              || detail.DailyBars.Count == 0;

        var daily21 = (detail.DailyBars ?? Array.Empty<DailyBar>())
            .OrderByDescending(d => d.Date)
            .Take(21)
            .OrderBy(d => d.Date)
            .Select(d => new
            {
                date = d.Date.ToString("yyyy-MM-dd"),
                d.Open,
                d.High,
                d.Low,
                d.Close,
                d.Volume,
                d.Amount
            })
            .ToList();

        // 夜更：先对账「今日（及未结）预测 vs 实际高低」
        object? forecastEvaluation = null;
        if (isNightlyUpdate)
        {
            forecastEvaluation = await EvaluatePendingForecastsAsync(item, detail, ct);
        }

        var hotNews = await _news.GetHotNewsAsync(12, ct);
        var stockNews = await _news.GetStockNewsAsync(item.Symbol, 8, ct);

        object? currentParams = null;
        object? todayTrades = Array.Empty<object>();
        if (item.BoundStrategyId.HasValue)
        {
            var strategy = await _db.Strategies.Include(s => s.Versions)
                .FirstOrDefaultAsync(s => s.Id == item.BoundStrategyId, ct);
            var current = strategy?.Versions.FirstOrDefault(v => v.Id == strategy.CurrentVersionId)
                          ?? strategy?.Versions.MaxBy(v => v.VersionNumber);
            currentParams = current?.ParametersJson;
            todayTrades = await _db.Trades
                .Where(t => t.Symbol == item.Symbol && t.TradeDate == DateTime.Today)
                .OrderByDescending(t => t.CreationTime)
                .Take(50)
                .Select(t => new { t.Side, t.Quantity, t.Price, t.TotalFee, t.CreationTime })
                .ToListAsync(ct);
        }

        var mode = isNightlyUpdate ? "NightlyRefresh" : "OnPoolEntry";
        var recentDecisions = await _db.StrategyDecisionLogs
            .Where(d => d.Symbol == item.Symbol && d.TradeDate == DateTime.Today)
            .OrderByDescending(d => d.DecidedAt)
            .Take(40)
            .Select(d => new { d.State, d.Action, d.Reason, d.DeviationPct, d.BlockedBy, d.DecidedAt })
            .ToListAsync(ct);

        var userGoal = await ResolveUserGoalAsync(item, ct);
        var goalClauseStrategy = userGoal is null
            ? "用户未绑定有效投资目标时，按稳健做T边界输出。"
            : $"用户预期目标：预期收益 {userGoal.ExpectedReturnPct}% 、预期周期 {userGoal.HorizonDays} 天"
              + (string.IsNullOrWhiteSpace(userGoal.ThemePreference) ? "" : $"、题材偏好「{userGoal.ThemePreference.Trim()}」")
              + "。请在策略边界（aggression/triggers/sizing/risk）中体现该目标节奏，勿承诺收益；禁止为凑预期收益改写已锁定的高低价。";

        var asOfLocal = DateTime.Now;
        var forecastTargetDate = PriceForecastMetrics.ResolveForecastTargetDate(asOfLocal);
        var forecastIsSameDay = PriceForecastMetrics.IsSameDayForecastWindow(asOfLocal);
        var forecastDayLabel = forecastIsSameDay ? "当日（收盘前）" : "下一交易日";
        var forecastScopeKey = forecastIsSameDay ? "sameTradingDay" : "nextTradingDay";
        var bias = await LoadAiBiasStatsAsync(item.Symbol, ct);
        var meanHighDev = bias.MeanHighDev;
        var meanLowDev = bias.MeanLowDev;
        var avgAiAcc = bias.AvgCombined;
        var biasSamples = bias.SampleCount;
        var limitCapHint = ResolveLimitCapPct(item.Symbol);
        var atrBaseline = PriceForecastMetrics.TryBuildAtrBaseline(detail?.DailyBars, forecastTargetDate);
        var constraints = PriceForecastMetrics.BuildForecastConstraints(
            detail?.DailyBars,
            forecastTargetDate,
            limitCapHint,
            detail?.Board?.PreClose,
            detail?.Board?.Last);
        var errorCard = await BuildForecastErrorCardAsync(item.Symbol, ct);
        var newsCatalyst = SuspectNewsCatalyst(hotNews, stockNews);
        var recentTReviews = await _dailyTReviews.LoadRecentReviewsForPromptAsync(item.Symbol, 5, ct);
        var reviewHint = recentTReviews is null
            ? "尚无每日复盘结论；按行情与对账独立迭代。"
            : "须优先消化 recentDailyTReviews 中的 bad / strategyHints 可执行开关：" +
              "forceFlatExcessShouldNotOpenT=true、preferCloseOpenTBeforeNewSell=true、" +
              "minRoundTripRequiresTwoXFees=true；若 tightenMinRoundTripEdge 则抬高 risk.minRoundTripEdgeBps；" +
              "若 penalizeZeroCycleWithFees 则收紧新开腿与 ForceFlat 买回；" +
              "只调整 triggers/path/session/sizing/risk，禁止改写 lockedForecast。KPI 看费后净 Cycle（cycle−fees）。";

        var countTowardAcc = !degradedNoDaily;
        var calibrationHint = biasSamples < PriceForecastMetrics.SoftHighTightenMinSamples || avgAiAcc is null
            ? $"尚无足够 AI 对账样本（需≥{PriceForecastMetrics.SoftHighTightenMinSamples}）；按 forecastConstraints 数字硬顶独立给高低价，禁止 ref±固定% 占位。"
            : $"近史AI校准池({bias.HorizonNote}) n={biasSamples} 中位偏差 高={meanHighDev:F2}% 低={meanLowDev:F2}%，综合准确率={avgAiAcc:F1}，上沿高估日占比={bias.HighOverPct:F0}%；请按主错误纠偏，禁止占位区间。";

        var horizonHint = forecastIsSameDay
            ? BuildSameDayForecastHint(detail?.Board)
            : "【NextDay】隔夜跳空不确定；上沿优先靠近 ref+ATR×1.0（勿轻易顶满 capHigh）；predictedLow 勿过度低于 floorLow 内侧。";

        var degradeHint = degradedNoDaily || constraints is { Degraded: true }
            ? "降级/弱约束：须仍给出 predictedHigh/predictedLow；countsTowardAccuracy=false；勿编造虚假 ATR。"
            : "";

        var forecastPrompt =
            $"【ForecastOnly】为 {item.Symbol} {item.Name} 预测{forecastDayLabel} {forecastTargetDate:yyyy-MM-dd} 最高/最低价（元）。" +
            $"本次视野={forecastScopeKey}。{horizonHint}" +
            $"必须严格遵守 payload.forecastConstraints：predictedHigh∈[refClose,capHigh]，predictedLow∈[floorLow,refClose]，width∈[minWidth,maxWidth]（degraded 时可略放宽但仍须有理）。" +
            $"输出 JSON：{{analysis, forecast:{{forDate,predictedHigh,predictedLow,source:ai,countsTowardAccuracy:{(countTowardAcc ? "true" : "false")},softBandMode:softPrefer,capHighByLimitPct:{limitCapHint:0.#},work:{{ref,atr,capHigh,floorLow,chosenHigh,chosenLow}},rationale}}}}。" +
            $"rationale 必须引用 constraints 中的数字；禁止「震荡上沿/下沿」空话。" +
            (newsCatalyst
                ? "新闻疑似硬催化：上沿可相对 ATR 略放宽，但仍 ≤ capHigh。"
                : "无明显硬催化：禁止把上沿拉近涨停，必须落在 constraints 带宽内。") +
            "目标收益仅供风险偏好参考，禁止为凑收益拉宽区间。" +
            calibrationHint +
            (errorCard is null ? "" : "请消化 forecastErrorCard.primaryError 与近史对拍表。") +
            degradeHint;

        var quotePayload = detail.Board is null ? null : new
        {
            detail.Board.Last,
            detail.Board.Open,
            detail.Board.High,
            detail.Board.Low,
            detail.Board.ChangePercent,
            detail.Board.PreClose,
            detail.Board.TurnoverRate
        };

        var forecastCalibrationPayload = new
        {
            meanHighDeviationPct = meanHighDev,
            meanLowDeviationPct = meanLowDev,
            avgAiCombinedAccuracyPct = avgAiAcc,
            sampleCount = biasSamples,
            calibrationStrength = bias.CalibrationStrength,
            highOverestimatePct = bias.HighOverPct,
            horizonNote = bias.HorizonNote,
            atrAvgCombinedAccuracyPct = bias.AtrAvgCombined,
            lookbackDays = PriceForecastMetrics.EvaluationLookbackDays
        };

        var forecastPayload = JsonSerializer.Serialize(new
        {
            mode = "ForecastOnly",
            schema = "PriceForecast/v1",
            symbol = item.Symbol,
            name = item.Name,
            lookbackDays = 21,
            marketDataSimulated = detail.IsSimulated,
            marketDataDegradedNoDaily = degradedNoDaily,
            marketDataNote = detail.DataSourceNote,
            dailyBars = daily21,
            quote = quotePayload,
            hotNews = hotNews.Select(n => new { n.Title, n.Summary, n.Source, n.PublishedAt }),
            stockNews = stockNews.Select(n => new { n.Title, n.Summary, n.Source }),
            newsCatalystSuspected = newsCatalyst,
            forecastEvaluation,
            forecastCalibration = forecastCalibrationPayload,
            forecastConstraints = constraints,
            forecastErrorCard = errorCard,
            atrBaselineHint = atrBaseline,
            forecastTargetDate = forecastTargetDate.ToString("yyyy-MM-dd"),
            forecastScope = forecastScopeKey,
            forecastRule = "交易日且本地时间<15:00预测当日高低；否则预测下一交易日高低；占位不得计入准确率",
            expectedOutput = new
            {
                analysis = "string",
                forecast = new
                {
                    forDate = forecastTargetDate.ToString("yyyy-MM-dd"),
                    predictedHigh = 0,
                    predictedLow = 0,
                    rationale = "string",
                    source = "ai",
                    countsTowardAccuracy = countTowardAcc,
                    softBandMode = "softPrefer",
                    capHighByLimitPct = limitCapHint,
                    work = new { @ref = 0, atr = 0, capHigh = 0, floorLow = 0, chosenHigh = 0, chosenLow = 0 }
                }
            },
            asOf = asOfLocal
        });

        var prompt = $"[Round1 ForecastOnly]\n{forecastPrompt}";
        var payload = forecastPayload;
        var job = new AiJob
        {
            JobType = AiJobType.StrategyUpdate,
            Status = AiJobStatus.Running,
            Prompt = prompt,
            InputPayload = payload
        };
        _db.AiJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        try
        {
            // —— Round1：专轮高低价 ——
            string? forecastOutput = null;
            ForecastPolicy? rawForecast = null;
            try
            {
                forecastOutput = await _ai.RunForecastAsync(forecastPrompt, forecastPayload, ct);
                rawForecast = ParseStrategyOutput(forecastOutput).Forecast;
            }
            catch (Exception ex1)
            {
                _logger.LogWarning(ex1, "ForecastOnly round failed for {Symbol}", item.Symbol);
            }

            var policy = StrategyParameters.Default();
            ApplyOperationalForecast(
                policy,
                rawForecast,
                atrBaseline,
                bias,
                forecastTargetDate,
                item.Symbol);
            if ((degradedNoDaily || constraints is { Degraded: true }) && policy.Forecast is not null)
            {
                policy.Forecast.CountsTowardAccuracy = false;
                var tag = "降级预测(弱约束/无日K，不计入准确率)";
                policy.Forecast.Rationale = string.IsNullOrWhiteSpace(policy.Forecast.Rationale)
                    ? tag
                    : $"{policy.Forecast.Rationale} | {tag}";
            }

            if (policy.Forecast is null || policy.Forecast.PredictedHigh <= 0 || policy.Forecast.PredictedLow <= 0)
            {
                if (atrBaseline is not null)
                    policy.Forecast = atrBaseline;
            }

            var lockedForecast = policy.Forecast;

            // —— Round2：策略画像（禁止改写 lockedForecast）——
            var strategyPrompt = isNightlyUpdate
                ? $"【StrategyPolicy】为 {item.Symbol} {item.Name} 输出 AdaptiveIntradayT StrategyPolicy(version=2)。{goalClauseStrategy}" +
                  "结合近21日行情、今日成交、决策日志、热点新闻与每日复盘迭代；只定边界与偏好（triggers/sizing/risk/session/style/path），不要输出盘中下单指令。" +
                  "payload.lockedForecast 已定稿：必须原样写入 parameters.forecast（或根级 forecast），禁止改写 predictedHigh/predictedLow。" +
                  "另须输出 path（enable=true、confirmUntil=10:15、breakAvoidSigma、pullbackSellPct，可选 pathPriors）。" +
                  reviewHint
                : $"【StrategyPolicy】为 {item.Symbol} {item.Name} 生成 AdaptiveIntradayT StrategyPolicy(version=2)：style/aggression/session/triggers/sizing/risk/factors/path/aiNotes。" +
                  $"{goalClauseStrategy}payload.lockedForecast 已定稿，必须原样放入 forecast，禁止改写高低价。用于状态机做T，参数是边界不是死脚本。" +
                  reviewHint;

            var strategyPayload = JsonSerializer.Serialize(new
            {
                mode,
                schema = "StrategyPolicy/v2",
                round = 2,
                symbol = item.Symbol,
                name = item.Name,
                poolId = item.StockPoolId,
                userGoal = userGoal is null ? null : new
                {
                    goalId = userGoal.Id,
                    expectedReturnPct = userGoal.ExpectedReturnPct,
                    horizonDays = userGoal.HorizonDays,
                    themePreference = userGoal.ThemePreference,
                    status = userGoal.Status.ToString(),
                    startDate = userGoal.StartDate?.ToString("yyyy-MM-dd"),
                    targetEndDate = userGoal.TargetEndDate?.ToString("yyyy-MM-dd")
                },
                lookbackDays = 21,
                marketDataNote = detail.DataSourceNote,
                dailyBars = daily21,
                quote = quotePayload,
                fundamentals = detail?.Fundamentals,
                hotNews = hotNews.Select(n => new { n.Title, n.Summary, n.Source, n.PublishedAt }),
                stockNews = stockNews.Select(n => new { n.Title, n.Summary, n.Source }),
                currentParameters = currentParams,
                todayTrades,
                recentDecisions,
                recentDailyTReviews = recentTReviews,
                forecastEvaluation,
                lockedForecast,
                forecastTargetDate = forecastTargetDate.ToString("yyyy-MM-dd"),
                forecastScope = forecastScopeKey,
                expectedOutput = new
                {
                    analysis = "string",
                    steps = new[] { "string" },
                    parameters = StrategyParameters.Default(),
                    forecast = lockedForecast,
                    changeNote = "string",
                    risk = "string"
                },
                asOf = asOfLocal
            });

            job.Prompt = $"[Round1 ForecastOnly]\n{forecastPrompt}\n\n[Round2 StrategyPolicy]\n{strategyPrompt}";
            job.InputPayload = JsonSerializer.Serialize(new
            {
                round1 = new { prompt = forecastPrompt, payload = JsonSerializer.Deserialize<JsonElement>(forecastPayload) },
                round2 = new { prompt = strategyPrompt, payload = JsonSerializer.Deserialize<JsonElement>(strategyPayload) }
            }, new JsonSerializerOptions { WriteIndented = true });
            await _db.SaveChangesAsync(ct);

            var output = await _ai.RunStrategyUpdateAsync(strategyPrompt, strategyPayload, ct);
            job.OutputPayload = JsonSerializer.Serialize(new
            {
                round1 = forecastOutput,
                round2 = output
            }, new JsonSerializerOptions { WriteIndented = true });
            var parsed = ParseStrategyOutput(output);
            job.AnalysisSummary = parsed.Analysis;
            job.AnalysisStepsJson = parsed.StepsJson;

            var parametersJson = parsed.ParametersJson ?? StrategyParameters.Default().ToJson();
            policy = StrategyParameters.FromJson(parametersJson);
            // 强制锁定 Round1 运营后的高低价，防止第二轮覆盖
            if (lockedForecast is not null)
                policy.Forecast = lockedForecast;
            else
            {
                ApplyOperationalForecast(
                    policy,
                    parsed.Forecast,
                    atrBaseline,
                    bias,
                    forecastTargetDate,
                    item.Symbol);
            }

            await AbsorbExecutableReviewHintsAsync(policy, item.Symbol, ct);
            parametersJson = policy.ToJson();
            prompt = job.Prompt ?? prompt;
            payload = job.InputPayload ?? strategyPayload;

            Guid strategyId;
            Guid? versionId = null;
            if (!item.BoundStrategyId.HasValue)
            {
                var created = await _strategies.CreateAsync(new Dtos.CreateStrategyRequest(
                    DefaultStrategyName(item.Name),
                    $"池内专属策略 | {item.Symbol} {item.Name}",
                    parametersJson,
                    parsed.ChangeNote ?? "入池AI自动生成"), ct);

                await BindSymbolAsync(created.Id, item.StockPoolId, item.Symbol, ct);

                item.BoundStrategyId = created.Id;
                item.StrategyGeneratedAt = DateTime.UtcNow;
                item.StrategyUpdatedAt = DateTime.UtcNow;
                strategyId = created.Id;
                versionId = await GetCurrentVersionIdAsync(strategyId, ct);
            }
            else
            {
                await _strategies.UpdateParamsAsync(
                    item.BoundStrategyId.Value,
                    new Dtos.UpdateStrategyParamsRequest(
                        parametersJson,
                        parsed.ChangeNote ?? (isNightlyUpdate ? "夜间AI自动更新" : "AI刷新")),
                    ct);
                item.StrategyUpdatedAt = DateTime.UtcNow;
                if (!item.StrategyGeneratedAt.HasValue)
                {
                    item.StrategyGeneratedAt = DateTime.UtcNow;
                }

                await BindSymbolAsync(item.BoundStrategyId.Value, item.StockPoolId, item.Symbol, ct);
                strategyId = item.BoundStrategyId.Value;
                versionId = await GetCurrentVersionIdAsync(strategyId, ct);
            }

            // AI/校准预测：占位不落准确率表；ATR 基线始终落库对照
            if (policy.Forecast is not null
                && policy.Forecast.PredictedHigh > 0
                && policy.Forecast.PredictedLow > 0
                && !PriceForecastMetrics.IsPlaceholderForecast(policy.Forecast))
            {
                await UpsertPendingForecastAsync(
                    item,
                    policy.Forecast,
                    strategyId,
                    versionId,
                    job.Id,
                    prompt,
                    forecastTargetDate,
                    ct);
            }

            if (atrBaseline is not null)
            {
                await UpsertPendingForecastAsync(
                    item,
                    atrBaseline,
                    strategyId,
                    versionId,
                    job.Id,
                    "[ATR baseline]",
                    forecastTargetDate,
                    ct);
            }

            var suggestion = new AiSuggestion
            {
                AiJobId = job.Id,
                PayloadJson = parsed.RawSuggestionJson ?? output,
                Symbol = item.Symbol,
                Name = item.Name,
                Reason = parsed.ChangeNote ?? parsed.Analysis,
                Risk = parsed.Risk,
                IsAccepted = true,
                ReviewedAt = DateTime.UtcNow
            };
            _db.AiSuggestions.Add(suggestion);

            job.Status = AiJobStatus.Accepted;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new Dtos.StrategyAiRunResult(
                true,
                isNightlyUpdate
                    ? $"已更新 {item.Symbol} 专属策略"
                    : $"已为 {item.Symbol} 生成专属策略",
                job.Id,
                prompt,
                payload,
                output);
        }
        catch (Exception ex)
        {
            job.Status = AiJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            if (!item.BoundStrategyId.HasValue)
            {
                try
                {
                    var created = await _strategies.CreateAsync(new Dtos.CreateStrategyRequest(
                        DefaultStrategyName(item.Name),
                        $"池内专属策略(默认参数) | AI失败: {Truncate(ex.Message, 80)}",
                        StrategyParameters.Default().ToJson(),
                        "AI失败回退默认参数"), ct);
                    await BindSymbolAsync(created.Id, item.StockPoolId, item.Symbol, ct);
                    item.BoundStrategyId = created.Id;
                    item.StrategyGeneratedAt = DateTime.UtcNow;
                    item.StrategyUpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    return new Dtos.StrategyAiRunResult(
                        true,
                        $"AI失败，已用默认参数创建 {item.Symbol} 策略: {ex.Message}",
                        job.Id,
                        prompt,
                        payload,
                        job.OutputPayload ?? ex.Message);
                }
                catch (Exception inner)
                {
                    return new Dtos.StrategyAiRunResult(
                        false,
                        $"创建策略失败: {inner.Message}",
                        job.Id,
                        prompt,
                        payload,
                        job.OutputPayload ?? ex.Message);
                }
            }

            return new Dtos.StrategyAiRunResult(
                false,
                ex.Message,
                job.Id,
                prompt,
                payload,
                job.OutputPayload ?? ex.Message);
        }
    }

    /// <summary>
    /// 解析该股关联的用户投资目标（优先 Active，其次同代码最近目标；回退到最新 Active 目标）。
    /// </summary>
    private async Task<InvestmentGoal?> ResolveUserGoalAsync(StockPoolItem item, CancellationToken ct)
    {
        var linkedGoalIds = await _db.InvestmentGoalStocks
            .Where(s => s.Symbol == item.Symbol || s.PoolItemId == item.Id)
            .Select(s => s.InvestmentGoalId)
            .Distinct()
            .ToListAsync(ct);

        if (linkedGoalIds.Count > 0)
        {
            var linked = await _db.InvestmentGoals
                .Where(g => linkedGoalIds.Contains(g.Id)
                            && g.Status != InvestmentGoalStatus.Cancelled
                            && g.Status != InvestmentGoalStatus.Draft)
                .OrderByDescending(g => g.Status == InvestmentGoalStatus.Active)
                .ThenByDescending(g => g.CreationTime)
                .FirstOrDefaultAsync(ct);
            if (linked is not null) return linked;
        }

        // 单用户场景：未直接关联时，取最新进行中目标，保证提示词仍带预期收益/周期
        return await _db.InvestmentGoals
            .Where(g => g.Status == InvestmentGoalStatus.Active)
            .OrderByDescending(g => g.CreationTime)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<object?> EvaluatePendingForecastsAsync(
        StockPoolItem item,
        StockDetailBundle? detail,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var now = DateTime.Now;

        // 模拟/回退行情不得写入准确率（会把演示价当成 Actual）
        if (detail is null || detail.IsSimulated)
        {
            _logger.LogWarning(
                "Skip forecast evaluation for {Symbol}: detail {Reason}",
                item.Symbol,
                detail is null ? "missing" : "simulated/fallback");
            return null;
        }

        var lookbackFrom = today.AddDays(-PriceForecastMetrics.EvaluationLookbackDays);
        // Pending / Skipped 需对账；已 Evaluated 在拿到真日K后允许纠错重算（修复 Board/模拟误写）
        var rows = await _db.PriceRangeForecasts
            .Where(f => f.Symbol == item.Symbol
                        && f.ForecastForDate <= today
                        && f.ForecastForDate >= lookbackFrom
                        && (f.Status == ForecastStatus.Pending
                            || f.Status == ForecastStatus.Skipped
                            || f.Status == ForecastStatus.Evaluated))
            .OrderBy(f => f.ForecastForDate)
            .ToListAsync(ct);

        if (rows.Count == 0) return null;

        var results = new List<object>();
        foreach (var row in rows)
        {
            var target = row.ForecastForDate.Date;
            if (!PriceForecastMetrics.CanFinalizeForecast(target, now))
                continue;

            var actual = PriceForecastMetrics.ResolveActualHighLowFromDailyBars(detail.DailyBars, target);
            if (actual is null)
            {
                // 超过 5 个自然日仍无日K则跳过，避免永久挂起
                if (row.Status == ForecastStatus.Pending
                    && (today - target).TotalDays > 5)
                {
                    row.Status = ForecastStatus.Skipped;
                    row.EvaluatedAt = now;
                    row.Notes = "超时未取到目标日日K高低价，已跳过";
                    results.Add(new
                    {
                        forecastId = row.Id,
                        symbol = row.Symbol,
                        name = row.Name,
                        forecastForDate = target.ToString("yyyy-MM-dd"),
                        status = "Skipped",
                        reason = row.Notes
                    });
                }

                continue;
            }

            var (highDev, lowDev, highAcc, lowAcc, combined, rangeHit, _) = PriceForecastMetrics.Evaluate(
                row.PredictedHigh,
                row.PredictedLow,
                actual.Value.High,
                actual.Value.Low);

            var counts = PriceForecastMetrics.ShouldCountTowardAccuracy(row.Source);
            var outlier = PriceForecastMetrics.IsOutlierEvaluation(highDev, lowDev);
            if (outlier) counts = false;

            // 已对账、日K一致且得分未变则跳过；容差/公式变更时仍重写
            if (row.Status == ForecastStatus.Evaluated
                && row.ActualHigh == actual.Value.High
                && row.ActualLow == actual.Value.Low
                && row.CombinedAccuracyPct == combined
                && row.HighAccuracyPct == highAcc
                && row.LowAccuracyPct == lowAcc
                && row.RangeHit == rangeHit
                && row.CountsTowardAccuracy == counts)
                continue;

            var reevaluated = row.Status == ForecastStatus.Evaluated
                              || row.Status == ForecastStatus.Skipped;

            row.ActualHigh = actual.Value.High;
            row.ActualLow = actual.Value.Low;
            row.HighDeviationPct = highDev;
            row.LowDeviationPct = lowDev;
            row.HighAccuracyPct = highAcc;
            row.LowAccuracyPct = lowAcc;
            row.CombinedAccuracyPct = combined;
            row.RangeHit = rangeHit;
            row.EvaluatedAt = now;
            row.Status = ForecastStatus.Evaluated;
            row.CountsTowardAccuracy = counts;
            row.Notes = outlier
                ? $"异常样本(|偏|≥{PriceForecastMetrics.OutlierAbsDeviationPct}%)，不计入准确率；来源 {row.Source}"
                : reevaluated
                    ? $"来源 {row.Source}；容差 {PriceForecastMetrics.DefaultTolerancePct}%；已按日K重算"
                    : $"来源 {row.Source}；容差 {PriceForecastMetrics.DefaultTolerancePct}%";

            results.Add(new
            {
                forecastId = row.Id,
                symbol = row.Symbol,
                name = row.Name,
                forecastForDate = target.ToString("yyyy-MM-dd"),
                predictedHigh = row.PredictedHigh,
                predictedLow = row.PredictedLow,
                actualHigh = row.ActualHigh,
                actualLow = row.ActualLow,
                highDeviationPct = highDev,
                lowDeviationPct = lowDev,
                highAccuracyPct = highAcc,
                lowAccuracyPct = lowAcc,
                combinedAccuracyPct = combined,
                rangeHit,
                reevaluated,
                status = "Evaluated"
            });
        }

        await _db.SaveChangesAsync(ct);
        return results.Count == 0 ? null : results;
    }

    /// <summary>
    /// 删除价位离谱的预测（含已 Evaluated 的假价对账），并清掉策略参数里的假 forecast。
    /// 参考价优先成交/持仓成本，避免被假 Actual 二次污染。
    /// </summary>
    private async Task InvalidateImplausiblePendingForecastsAsync(string symbol, CancellationToken ct)
    {
        var refPx = await ResolveLastKnownRealPriceAsync(symbol, ct);
        if (refPx is null or <= 0) return;

        var rows = await _db.PriceRangeForecasts
            .Where(f => f.Symbol == symbol)
            .ToListAsync(ct);

        var removed = 0;
        foreach (var row in rows)
        {
            var predBad = !PriceForecastMetrics.IsForecastPriceLevelPlausible(
                row.PredictedHigh, row.PredictedLow, refPx.Value);
            var actBad = row.ActualHigh is > 0 && row.ActualLow is > 0
                         && !PriceForecastMetrics.IsForecastPriceLevelPlausible(
                             row.ActualHigh.Value, row.ActualLow.Value, refPx.Value);
            var noteFake = !string.IsNullOrWhiteSpace(row.Notes)
                           && (row.Notes.Contains("模拟", StringComparison.Ordinal)
                               || row.Notes.Contains("假预测", StringComparison.Ordinal)
                               || row.Notes.Contains("价位离谱", StringComparison.Ordinal));
            if (!predBad && !actBad && !noteFake) continue;

            _db.PriceRangeForecasts.Remove(row);
            removed++;
        }

        var clearedStrategy = await ClearImplausibleStrategyForecastAsync(symbol, refPx.Value, ct);

        if (removed > 0 || clearedStrategy)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Purged implausible forecasts for {Symbol}: deleted={Deleted}, strategyCleared={Cleared}, ref={Ref:F2}",
                symbol, removed, clearedStrategy, refPx);
        }
    }

    /// <summary>若当前策略版本 forecast 价位离谱（假数据），清除以免盘中 softBand 用错锚。</summary>
    private async Task<bool> ClearImplausibleStrategyForecastAsync(
        string symbol, decimal refPx, CancellationToken ct)
    {
        var item = await _db.StockPoolItems
            .FirstOrDefaultAsync(i => i.Symbol == symbol && i.BoundStrategyId != null, ct);
        if (item?.BoundStrategyId is null) return false;

        var strategy = await _db.Strategies.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == item.BoundStrategyId, ct);
        if (strategy is null) return false;

        var version = strategy.CurrentVersionId != Guid.Empty
            ? await _db.StrategyVersions.FirstOrDefaultAsync(v => v.Id == strategy.CurrentVersionId, ct)
            : await _db.StrategyVersions
                .Where(v => v.StrategyDefinitionId == strategy.Id)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync(ct);
        if (version is null || string.IsNullOrWhiteSpace(version.ParametersJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(version.ParametersJson);
            if (!doc.RootElement.TryGetProperty("forecast", out var fc))
                return false;
            var high = fc.TryGetProperty("predictedHigh", out var h) && h.TryGetDecimal(out var hv) ? hv : 0m;
            var low = fc.TryGetProperty("predictedLow", out var l) && l.TryGetDecimal(out var lv) ? lv : 0m;
            if (high <= 0 || low <= 0) return false;
            if (PriceForecastMetrics.IsForecastPriceLevelPlausible(high, low, refPx))
                return false;

            var policy = StrategyParameters.FromJson(version.ParametersJson);
            policy.Forecast = null;
            policy.AiNotes = (policy.AiNotes ?? string.Empty)
                + $" | 已清除疑似模拟行情假预测(中枢相对真价{refPx:F2}离谱)，待真实行情下重新AI刷新";
            version.ParametersJson = policy.ToJson();
            version.ChangeNote = (version.ChangeNote ?? string.Empty)
                + " | 清除假预测";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClearImplausibleStrategyForecast parse skip for {Symbol}", symbol);
            return false;
        }
    }

    private async Task<decimal?> ResolveLastKnownRealPriceAsync(string symbol, CancellationToken ct)
    {
        // 成交/成本优先：Evaluated Actual 可能来自模拟日K，不能当真价锚
        var tradePx = await _db.Trades.AsNoTracking()
            .Where(t => t.Symbol == symbol && t.Price > 0)
            .OrderByDescending(t => t.CreationTime)
            .Select(t => (decimal?)t.Price)
            .FirstOrDefaultAsync(ct);
        if (tradePx is > 0) return tradePx;

        var pos = await _db.Positions.AsNoTracking()
            .Where(p => p.Symbol == symbol && p.AvgCost > 0)
            .Select(p => (decimal?)p.AvgCost)
            .FirstOrDefaultAsync(ct);
        if (pos is > 0) return pos;

        var evaluated = await _db.PriceRangeForecasts.AsNoTracking()
            .Where(f => f.Symbol == symbol
                        && f.Status == ForecastStatus.Evaluated
                        && f.ActualHigh != null
                        && f.ActualLow != null
                        && f.ActualHigh > 0
                        && f.ActualLow > 0)
            .OrderByDescending(f => f.ForecastForDate)
            .Select(f => new { f.ActualHigh, f.ActualLow })
            .Take(8)
            .ToListAsync(ct);
        if (evaluated.Count == 0) return null;

        // 取 Actual 中枢的中位数，降低单条假对账污染
        var mids = evaluated
            .Select(e => (e.ActualHigh!.Value + e.ActualLow!.Value) / 2m)
            .OrderBy(x => x)
            .ToList();
        return mids[mids.Count / 2];
    }

    private async Task UpsertPendingForecastAsync(
        StockPoolItem item,
        ForecastPolicy forecast,
        Guid strategyId,
        Guid? versionId,
        Guid aiJobId,
        string? promptText,
        DateTime forecastTargetDate,
        CancellationToken ct)
    {
        // 以系统规则解析的目标日为准，避免 AI 写错 forDate
        var forDate = forecastTargetDate.Date;
        var high = Math.Max(forecast.PredictedHigh, forecast.PredictedLow);
        var low = Math.Min(forecast.PredictedHigh, forecast.PredictedLow);
        if (high <= 0 || low <= 0) return;

        var source = PriceForecastMetrics.ParseSource(forecast.Source, forecast.CountsTowardAccuracy);
        if (source == ForecastSource.Placeholder) return; // 双保险：占位不入库

        var refPx = await ResolveLastKnownRealPriceAsync(item.Symbol, ct);
        if (refPx is > 0
            && !PriceForecastMetrics.IsForecastPriceLevelPlausible(high, low, refPx.Value))
        {
            _logger.LogWarning(
                "Skip upsert forecast for {Symbol}: mid={Mid:F2} implausible vs ref={Ref:F2} (likely simulated)",
                item.Symbol, (high + low) / 2m, refPx);
            return;
        }

        var counts = forecast.CountsTowardAccuracy
                     ?? PriceForecastMetrics.ShouldCountTowardAccuracy(source);

        var existing = await _db.PriceRangeForecasts.FirstOrDefaultAsync(
            f => f.Symbol == item.Symbol
                 && f.ForecastForDate == forDate
                 && f.Source == source
                 && f.Status == ForecastStatus.Pending,
            ct);

        if (existing is not null)
        {
            existing.Name = item.Name;
            existing.PredictedHigh = high;
            existing.PredictedLow = low;
            existing.PredictedAt = DateTime.Now;
            existing.Source = source;
            existing.CountsTowardAccuracy = counts;
            existing.StrategyDefinitionId = strategyId;
            existing.StrategyVersionId = versionId;
            existing.AiJobId = aiJobId;
            existing.Prompt = promptText;
            existing.Notes = forecast.Rationale;
        }
        else
        {
            _db.PriceRangeForecasts.Add(new PriceRangeForecast
            {
                Symbol = item.Symbol,
                Name = item.Name,
                ForecastForDate = forDate,
                PredictedAt = DateTime.Now,
                PredictedHigh = high,
                PredictedLow = low,
                Status = ForecastStatus.Pending,
                Source = source,
                CountsTowardAccuracy = counts,
                StrategyDefinitionId = strategyId,
                StrategyVersionId = versionId,
                AiJobId = aiJobId,
                Prompt = promptText,
                Notes = forecast.Rationale
            });
        }
    }

    private async Task<Guid?> GetCurrentVersionIdAsync(Guid strategyId, CancellationToken ct)
    {
        var strategy = await _db.Strategies.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == strategyId, ct);
        return strategy?.CurrentVersionId;
    }

    /// <summary>
    /// 硬吸收近一日复盘可执行 hints：抬高 MinRoundTripEdge；其余开关已在评估器内落地。
    /// </summary>
    private async Task AbsorbExecutableReviewHintsAsync(
        StrategyParameters policy, string symbol, CancellationToken ct)
    {
        var hintsJson = await _db.DailyTReviews.AsNoTracking()
            .Where(r => r.Symbol == symbol
                        && r.Status == DailyTReviewStatus.Succeeded
                        && r.StrategyHintsJson != null)
            .OrderByDescending(r => r.TradeDate)
            .Select(r => r.StrategyHintsJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(hintsJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(hintsJson);
            var root = doc.RootElement;
            var tighten = root.TryGetProperty("tightenMinRoundTripEdge", out var t)
                          && t.ValueKind == JsonValueKind.True;
            var penalize = root.TryGetProperty("penalizeZeroCycleWithFees", out var p)
                           && p.ValueKind == JsonValueKind.True;
            if (!tighten && !penalize) return;

            var floor = penalize ? 20m : 18m;
            if (policy.Risk.MinRoundTripEdgeBps < floor)
            {
                policy.Risk.MinRoundTripEdgeBps = floor;
                policy.AiNotes = (policy.AiNotes ?? string.Empty)
                    + $" | 复盘hints抬高minRoundTripEdgeBps→{floor:0}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AbsorbExecutableReviewHints parse skip for {Symbol}", symbol);
        }
    }

    /// <summary>
    /// 运营用预测写入 Policy：AI→(分桶校准)→ATR可达硬钳→与 ATR 融合；无 AI 则 ATR；禁止占位污染。
    /// 低准确率时下调 aggression。
    /// </summary>
    private static void ApplyOperationalForecast(
        StrategyParameters policy,
        ForecastPolicy? parsedForecast,
        ForecastPolicy? atrBaseline,
        ForecastBiasStats bias,
        DateTime forecastTargetDate,
        string? symbol = null)
    {
        var target = forecastTargetDate.Date.ToString("yyyy-MM-dd");
        ForecastPolicy? operational = null;
        var meanHighDev = bias.MeanHighDev;
        var meanLowDev = bias.MeanLowDev;
        var avgAiAcc = bias.AvgCombined;
        var biasSampleCount = bias.SampleCount;
        var strength = bias.CalibrationStrength;

        var limitCap = ResolveLimitCapPct(symbol);
        decimal? atr = null;
        decimal? refPrice = null;
        if (atrBaseline is { PredictedHigh: > 0, PredictedLow: > 0 })
        {
            atrBaseline.ForDate = target;
            atrBaseline.CountsTowardAccuracy = false;
            atr = Math.Abs(atrBaseline.PredictedHigh - atrBaseline.PredictedLow) / 2m;
            refPrice = (atrBaseline.PredictedHigh + atrBaseline.PredictedLow) / 2m;
        }

        var ai = parsedForecast ?? policy.Forecast;
        if (ai is not null
            && ai.PredictedHigh > 0
            && ai.PredictedLow > 0
            && !PriceForecastMetrics.IsPlaceholderForecast(ai))
        {
            if (ai.PredictedHigh < ai.PredictedLow)
                (ai.PredictedHigh, ai.PredictedLow) = (ai.PredictedLow, ai.PredictedHigh);
            ai.ForDate = target;
            ai.AsOf ??= DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            ai.Source ??= PriceForecastMetrics.ToSourceKey(ForecastSource.Ai);
            ai.CountsTowardAccuracy = true;
            ai.SoftBandMode = string.IsNullOrWhiteSpace(ai.SoftBandMode) ? "softPrefer" : ai.SoftBandMode;
            ai.CapHighByLimitPct = limitCap;
            ai.RecentCombinedAccuracyPct = avgAiAcc;

            if (PriceForecastMetrics.ShouldApplyCalibration(biasSampleCount, meanHighDev, meanLowDev))
                operational = PriceForecastMetrics.ApplyBiasCalibration(
                    ai, meanHighDev, meanLowDev, strength);
            else if (PriceForecastMetrics.ShouldTightenHighOnly(biasSampleCount, meanHighDev))
                operational = PriceForecastMetrics.TightenOverestimatedHigh(ai, meanHighDev, strength);
            else
                operational = ai;

            operational.RecentCombinedAccuracyPct = avgAiAcc;

            refPrice ??= (operational.PredictedHigh + operational.PredictedLow) / 2m;
            if (refPrice is > 0)
                operational = PriceForecastMetrics.ClampForecastToTradingBand(
                    operational, refPrice.Value, atr, limitCap);

            if (atrBaseline is not null)
            {
                // AI 若长期不如 ATR，进一步压 AI 权重
                var w = PriceForecastMetrics.ResolveAiBlendWeight(avgAiAcc);
                if (bias.AtrAvgCombined is { } atrAcc && avgAiAcc is { } aiAcc && aiAcc + 3m < atrAcc)
                    w = Math.Min(w, 0.35m);
                operational = PriceForecastMetrics.BlendAiWithAtr(operational, atrBaseline, w);
                operational = PriceForecastMetrics.ClampForecastToTradingBand(
                    operational, refPrice ?? (operational.PredictedHigh + operational.PredictedLow) / 2m, atr, limitCap);
            }

            operational.RecentCombinedAccuracyPct = avgAiAcc;
        }
        else if (atrBaseline is not null)
        {
            operational = atrBaseline;
            operational.RecentCombinedAccuracyPct = avgAiAcc;
        }

        policy.Forecast = operational;

        var scale = PriceForecastMetrics.ResolveAggressionScale(avgAiAcc);
        if (scale < 1m)
        {
            policy.Aggression = Math.Round(Math.Clamp(policy.Aggression * scale, 0.05m, 1m), 3);
            policy.AiNotes = (policy.AiNotes ?? string.Empty)
                + $" | 近史AI准确率{avgAiAcc:F1}偏低，aggression×{scale:F2}";
        }
    }

    private static decimal ResolveLimitCapPct(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return PriceForecastMetrics.DefaultLimitCapPct;
        var s = symbol.Trim();
        // 创业板/科创板约 20%；其余默认 12%（含主板/中小板近似）
        if (s.StartsWith("300", StringComparison.Ordinal) || s.StartsWith("301", StringComparison.Ordinal)
            || s.StartsWith("688", StringComparison.Ordinal) || s.StartsWith("689", StringComparison.Ordinal))
            return 20m;
        return PriceForecastMetrics.DefaultLimitCapPct;
    }

    private sealed record ForecastBiasStats(
        decimal MeanHighDev,
        decimal MeanLowDev,
        decimal? AvgCombined,
        int SampleCount,
        decimal CalibrationStrength,
        decimal HighOverPct,
        string HorizonNote,
        decimal? AtrAvgCombined);

    private async Task<ForecastBiasStats> LoadAiBiasStatsAsync(string symbol, CancellationToken ct)
    {
        var from = DateTime.Today.AddDays(-PriceForecastMetrics.EvaluationLookbackDays);
        // 只用原始 Ai 作校准训练，避免 CalibratedAi 反馈放大（历史上校准源反而更差）
        var rows = await _db.PriceRangeForecasts.AsNoTracking()
            .Where(f => f.Symbol == symbol
                        && f.Status == ForecastStatus.Evaluated
                        && f.CountsTowardAccuracy
                        && f.ForecastForDate >= from
                        && f.Source == ForecastSource.Ai
                        && f.HighDeviationPct != null
                        && f.LowDeviationPct != null
                        && f.CombinedAccuracyPct != null)
            .OrderByDescending(f => f.ForecastForDate)
            .Take(40)
            .ToListAsync(ct);

        rows = rows
            .Where(r => !PriceForecastMetrics.IsOutlierEvaluation(r.HighDeviationPct!.Value, r.LowDeviationPct!.Value))
            .ToList();

        var atrRows = await _db.PriceRangeForecasts.AsNoTracking()
            .Where(f => f.Symbol == symbol
                        && f.Status == ForecastStatus.Evaluated
                        && f.ForecastForDate >= from
                        && f.Source == ForecastSource.AtrBaseline
                        && f.CombinedAccuracyPct != null)
            .OrderByDescending(f => f.ForecastForDate)
            .Take(40)
            .ToListAsync(ct);
        decimal? atrAvg = atrRows.Count >= 2
            ? Math.Round(atrRows.Average(r => r.CombinedAccuracyPct!.Value), 2)
            : null;

        if (rows.Count < 2)
            return new ForecastBiasStats(0m, 0m, null, rows.Count, 1m, 0m, "样本不足", atrAvg);

        static PriceForecastMetrics.ForecastHorizon HorizonOf(PriceRangeForecast r)
            => PriceForecastMetrics.ClassifyForecastHorizon(r.PredictedAt, r.ForecastForDate);

        var nextDay = rows.Where(r => HorizonOf(r) == PriceForecastMetrics.ForecastHorizon.NextDay).ToList();
        var sameDay = rows.Where(r => HorizonOf(r) == PriceForecastMetrics.ForecastHorizon.SameDay).ToList();

        List<PriceRangeForecast> pool;
        decimal strength;
        string horizonNote;
        if (nextDay.Count >= 2)
        {
            pool = nextDay.Take(20).ToList();
            strength = 1m;
            horizonNote = $"次日n={pool.Count}";
            // 次日样本少时，用降权当日样本补齐到 MinCalibrationSamples
            if (pool.Count < PriceForecastMetrics.MinCalibrationSamples && sameDay.Count > 0)
            {
                var need = PriceForecastMetrics.MinCalibrationSamples - pool.Count;
                pool = pool.Concat(sameDay.Take(need)).ToList();
                strength = 0.75m;
                horizonNote += $"+当日补{Math.Min(need, sameDay.Count)}";
            }
        }
        else
        {
            pool = sameDay.Count > 0 ? sameDay.Take(20).ToList() : rows.Take(20).ToList();
            strength = 0.55m; // 历史几乎全是当日预测，全强度校准会伤次日
            horizonNote = $"当日主导n={pool.Count}";
        }

        var meanHigh = PriceForecastMetrics.Median(pool.Select(r => r.HighDeviationPct!.Value).ToList());
        var meanLow = PriceForecastMetrics.Median(pool.Select(r => r.LowDeviationPct!.Value).ToList());
        var avgAcc = Math.Round(pool.Average(r =>
            PriceForecastMetrics.RescoreCombinedAccuracy(r.HighDeviationPct, r.LowDeviationPct)
            ?? r.CombinedAccuracyPct!.Value), 2);
        var highOverPct = pool.Count == 0
            ? 0m
            : Math.Round(100m * pool.Count(r => r.HighDeviationPct!.Value > 1m) / pool.Count, 1);

        return new ForecastBiasStats(
            meanHigh, meanLow, avgAcc, pool.Count, strength, highOverPct, horizonNote, atrAvg);
    }

    private async Task BindSymbolAsync(Guid strategyId, Guid poolId, string symbol, CancellationToken ct)
    {
        var exists = await _db.StrategyBindings.AnyAsync(
            b => b.StrategyDefinitionId == strategyId
                 && b.StockPoolId == poolId
                 && b.Symbol == symbol, ct);
        if (exists) return;

        _db.StrategyBindings.Add(new StrategyBinding
        {
            StrategyDefinitionId = strategyId,
            StockPoolId = poolId,
            Symbol = symbol,
            Priority = 100,
            IsEnabled = true
        });
        await _db.SaveChangesAsync(ct);
    }

    private static ParsedStrategyAi ParseStrategyOutput(string output)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "{}" : output);
        var root = doc.RootElement;
        var analysis = GetString(root, "analysis") ?? GetString(root, "summary");
        string? stepsJson = null;
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            stepsJson = steps.GetRawText();
        }

        string? parametersJson = null;
        JsonElement? parametersEl = null;
        if (root.TryGetProperty("parameters", out var p) || root.TryGetProperty("policy", out p))
        {
            parametersEl = p;
            parametersJson = p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
        }
        else if (root.TryGetProperty("mode", out _) || root.TryGetProperty("triggers", out _))
        {
            parametersEl = root;
            parametersJson = root.GetRawText();
        }

        var forecast = TryParseForecast(root);
        if (forecast is null && parametersEl is { } pe)
            forecast = TryParseForecast(pe);

        return new ParsedStrategyAi(
            analysis,
            stepsJson,
            parametersJson,
            GetString(root, "changeNote"),
            GetString(root, "risk"),
            root.TryGetProperty("parameters", out _) ? root.GetRawText() : output,
            forecast);
    }

    private static ForecastPolicy? TryParseForecast(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        JsonElement fcEl = default;
        var hasObject = el.TryGetProperty("forecast", out fcEl) && fcEl.ValueKind == JsonValueKind.Object;
        if (!hasObject)
        {
            // 根级扁平字段
            var high = GetDecimal(el, "predictedHigh");
            var low = GetDecimal(el, "predictedLow");
            if (high is null || low is null) return null;
            return new ForecastPolicy
            {
                ForDate = GetString(el, "forDate") ?? GetString(el, "forecastForDate"),
                PredictedHigh = high.Value,
                PredictedLow = low.Value,
                AsOf = GetString(el, "asOf"),
                Rationale = GetString(el, "rationale") ?? GetString(el, "forecastRationale"),
                Source = GetString(el, "source"),
                CountsTowardAccuracy = GetBool(el, "countsTowardAccuracy")
            };
        }

        return new ForecastPolicy
        {
            ForDate = GetString(fcEl, "forDate") ?? GetString(fcEl, "forecastForDate"),
            PredictedHigh = GetDecimal(fcEl, "predictedHigh") ?? 0,
            PredictedLow = GetDecimal(fcEl, "predictedLow") ?? 0,
            AsOf = GetString(fcEl, "asOf"),
            Rationale = GetString(fcEl, "rationale"),
            Source = GetString(fcEl, "source"),
            CountsTowardAccuracy = GetBool(fcEl, "countsTowardAccuracy"),
            SoftBandMode = GetString(fcEl, "softBandMode") ?? "softPrefer",
            CapHighByLimitPct = GetDecimal(fcEl, "capHighByLimitPct") ?? 12m,
            Work = TryParseForecastWork(fcEl)
        };
    }

    private static ForecastWorkNotes? TryParseForecastWork(JsonElement fcEl)
    {
        if (!fcEl.TryGetProperty("work", out var w) || w.ValueKind != JsonValueKind.Object)
            return null;
        return new ForecastWorkNotes
        {
            Ref = GetDecimal(w, "ref") ?? GetDecimal(w, "Ref"),
            Atr = GetDecimal(w, "atr"),
            CapHigh = GetDecimal(w, "capHigh"),
            FloorLow = GetDecimal(w, "floorLow"),
            ChosenHigh = GetDecimal(w, "chosenHigh"),
            ChosenLow = GetDecimal(w, "chosenLow")
        };
    }

    private static string BuildSameDayForecastHint(StockQuoteBoard? board)
    {
        if (board is null || board.Last <= 0)
            return "【SameDay】预测当日全日高低；须说明剩余波幅判断。";
        var parts = new List<string>
        {
            "【SameDay】预测当日全日高低，须兼容已实现盘面："
        };
        if (board.Open > 0) parts.Add($"今开{board.Open:F2}");
        if (board.High > 0) parts.Add($"今高{board.High:F2}（predictedHigh≥今高）");
        if (board.Low > 0) parts.Add($"今低{board.Low:F2}（predictedLow≤今低）");
        parts.Add($"现价{board.Last:F2}");
        return string.Join("；", parts) + "。并说明剩余波幅。";
    }

    private static bool SuspectNewsCatalyst(
        IReadOnlyList<MarketNewsItem> hotNews,
        IReadOnlyList<MarketNewsItem> stockNews)
    {
        static bool Hit(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return false;
            string[] keys =
            [
                "业绩", "预增", "预亏", "重组", "并购", "中标", "政策", "增持", "减持",
                "回购", "立案", "问询", "涨停", "跌停", "财报", "订单", "合同"
            ];
            foreach (var k in keys)
            {
                if (t.Contains(k, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        foreach (var n in stockNews)
        {
            if (Hit(n.Title) || Hit(n.Summary)) return true;
        }

        foreach (var n in hotNews.Take(6))
        {
            if (Hit(n.Title) || Hit(n.Summary)) return true;
        }

        return false;
    }

    private async Task<object?> BuildForecastErrorCardAsync(string symbol, CancellationToken ct)
    {
        var from = DateTime.Today.AddDays(-PriceForecastMetrics.EvaluationLookbackDays);
        var rows = await _db.PriceRangeForecasts.AsNoTracking()
            .Where(f => f.Symbol == symbol
                        && f.Status == ForecastStatus.Evaluated
                        && f.CountsTowardAccuracy
                        && f.Source == ForecastSource.Ai
                        && f.ForecastForDate >= from
                        && f.ActualHigh != null
                        && f.ActualLow != null
                        && f.HighDeviationPct != null
                        && f.LowDeviationPct != null)
            .OrderByDescending(f => f.ForecastForDate)
            .Take(12)
            .ToListAsync(ct);

        rows = rows
            .Where(r => !PriceForecastMetrics.IsOutlierEvaluation(r.HighDeviationPct!.Value, r.LowDeviationPct!.Value))
            .Take(5)
            .ToList();
        if (rows.Count == 0)
            return new { primaryError = "Insufficient", samples = Array.Empty<object>() };

        var overHigh = rows.Count(r => r.HighDeviationPct!.Value > 1m);
        var underLow = rows.Count(r => r.LowDeviationPct!.Value < -1m);
        var tooWide = rows.Count(r =>
        {
            var w = Math.Abs(r.PredictedHigh - r.PredictedLow);
            var aw = Math.Abs(r.ActualHigh!.Value - r.ActualLow!.Value);
            return aw > 0 && w > aw * 1.8m;
        });

        var primary = "Balanced";
        if (overHigh >= underLow && overHigh >= tooWide && overHigh > 0) primary = "OverHigh";
        else if (underLow > overHigh && underLow >= tooWide) primary = "UnderLow";
        else if (tooWide > overHigh && tooWide > underLow) primary = "TooWide";

        return new
        {
            primaryError = primary,
            samples = rows.Select(r => new
            {
                forDate = r.ForecastForDate.ToString("yyyy-MM-dd"),
                predictedHigh = r.PredictedHigh,
                predictedLow = r.PredictedLow,
                actualHigh = r.ActualHigh,
                actualLow = r.ActualLow,
                highDeviationPct = r.HighDeviationPct,
                lowDeviationPct = r.LowDeviationPct,
                rangeHit = r.RangeHit,
                combinedAccuracyPct = r.CombinedAccuracyPct
            }).ToList()
        };
    }

    private static DateTime? ParseForDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d.Date;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d.Date;
        return null;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d))
            return d;
        if (p.ValueKind == JsonValueKind.String
            && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            return d;
        return null;
    }

    private static bool? GetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind is JsonValueKind.True) return true;
        if (p.ValueKind is JsonValueKind.False) return false;
        if (p.ValueKind == JsonValueKind.String
            && bool.TryParse(p.GetString(), out var b))
            return b;
        return null;
    }

    private static string DefaultStrategyName(string? stockName)
    {
        var name = string.IsNullOrWhiteSpace(stockName) ? "未命名" : stockName.Trim();
        return name.EndsWith("策略", StringComparison.Ordinal) ? name : name + "策略";
    }

    private static string Truncate(string? s, int n)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static Dtos.PriceRangeForecastDto ToDto(PriceRangeForecast f)
    {
        decimal? highAbs = f.ActualHigh is { } ah
            ? PriceForecastMetrics.AbsoluteError(f.PredictedHigh, ah)
            : null;
        decimal? lowAbs = f.ActualLow is { } al
            ? PriceForecastMetrics.AbsoluteError(f.PredictedLow, al)
            : null;
        decimal? meanAbs = f.HighDeviationPct is { } hd && f.LowDeviationPct is { } ld
            ? PriceForecastMetrics.MeanAbsDeviationPct(hd, ld)
            : null;
        var combined = PriceForecastMetrics.RescoreCombinedAccuracy(f.HighDeviationPct, f.LowDeviationPct)
                       ?? f.CombinedAccuracyPct;

        return new(
            f.Id,
            f.Symbol,
            f.Name,
            f.ForecastForDate,
            f.PredictedAt,
            f.PredictedHigh,
            f.PredictedLow,
            f.ActualHigh,
            f.ActualLow,
            f.HighDeviationPct,
            f.LowDeviationPct,
            f.HighDeviationPct is { } hDev ? PriceForecastMetrics.AccuracyPct(hDev) : f.HighAccuracyPct,
            f.LowDeviationPct is { } lDev ? PriceForecastMetrics.AccuracyPct(lDev) : f.LowAccuracyPct,
            combined,
            f.RangeHit,
            f.Status.ToString(),
            f.EvaluatedAt,
            f.Notes,
            f.Prompt,
            f.Source.ToString(),
            f.CountsTowardAccuracy,
            highAbs,
            lowAbs,
            meanAbs,
            PriceForecastMetrics.AccuracyGrade(combined));
    }

    private sealed record ParsedStrategyAi(
        string? Analysis,
        string? StepsJson,
        string? ParametersJson,
        string? ChangeNote,
        string? Risk,
        string? RawSuggestionJson,
        ForecastPolicy? Forecast);
}
