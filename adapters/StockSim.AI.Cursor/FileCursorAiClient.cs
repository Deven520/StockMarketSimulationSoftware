using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockSim.Application.Services;

namespace StockSim.AI.Cursor;

/// <summary>
/// 文件交换 / 占位适配（演示与回退）。生产选股请使用 <see cref="CursorSdkAiClient"/>。
/// 写出请求 JSON，优先读取人工回写的响应；若无响应则生成固定占位候选。
/// 选股响应约定字段：analysis、steps、items[{symbol,name,reason,logic,risk}]、note。
/// </summary>
public sealed class FileCursorAiClient : ICursorAiClient
{
    private readonly string _workDir;

    public FileCursorAiClient(IConfiguration configuration)
    {
        _workDir = configuration.GetValue<string>("AI:WorkDirectory")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "ai"));
        Directory.CreateDirectory(_workDir);
    }

    public Task<CursorAiConnectionStatus> GetConnectionStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new CursorAiConnectionStatus(
            false,
            "placeholder-only",
            null,
            "当前为占位适配器，不会调用真实 Cursor Agent。"));

    public async Task<string> RunGoalToPromptAsync(string goalPayloadJson, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var requestPath = Path.Combine(_workDir, $"goalprompt_request_{id}.json");
        var responsePath = Path.Combine(_workDir, $"goalprompt_response_{id}.json");
        await File.WriteAllTextAsync(requestPath, goalPayloadJson ?? "{}", ct);

        if (File.Exists(responsePath))
            return await File.ReadAllTextAsync(responsePath, ct);

        var expected = "?";
        var days = "?";
        var theme = "综合";
        var tradeStyle = "IntradayT";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(goalPayloadJson) ? "{}" : goalPayloadJson);
            if (doc.RootElement.TryGetProperty("expectedReturnPct", out var e)) expected = e.ToString();
            if (doc.RootElement.TryGetProperty("horizonDays", out var d)) days = d.ToString();
            if (doc.RootElement.TryGetProperty("themePreference", out var t) && t.ValueKind == JsonValueKind.String)
                theme = t.GetString() ?? theme;
            if (doc.RootElement.TryGetProperty("tradeStyle", out var ts))
                tradeStyle = ts.ValueKind == JsonValueKind.String ? (ts.GetString() ?? tradeStyle) : ts.ToString();
        }
        catch { /* ignore */ }

        var isNextDay = tradeStyle.Contains("NextDay", StringComparison.OrdinalIgnoreCase)
                        || tradeStyle.Contains("次日", StringComparison.OrdinalIgnoreCase);
        var isTrend = tradeStyle.Contains("Trend", StringComparison.OrdinalIgnoreCase)
                      || tradeStyle.Contains("趋势", StringComparison.OrdinalIgnoreCase);
        string promptText;
        string rationaleText;
        if (isNextDay)
        {
            promptText =
                $"请推荐 3~5 只下一交易日更可能上涨的具体 A 股（必须给出真实 6 位代码与简称）。" +
                $"题材偏好「{theme}」；目标次日涨幅约 {expected}%；" +
                "请结合当日走势与量能、近期历史强弱、题材与近期新闻/热点；排除 ST；" +
                "每只 reason 写清次日看多依据；输出 JSON：analysis、steps、items[{symbol,name,reason,logic,risk}]。禁止只写行业方向而不给代码。";
            rationaleText = "占位：按次日上涨预选目标组装选股提示词（当日+历史+新闻）。";
        }
        else if (isTrend)
        {
            promptText =
                $"请推荐 3~5 只适合 A 股中期趋势/底部反转观察的具体股票（必须给出真实 6 位代码与简称）。" +
                $"题材偏好「{theme}」；希望约 {days} 个交易日内组合收益接近 {expected}%；" +
                "优先基本面改善、底部结构清晰或趋势初成，避免 ST；不做日内高频做T；" +
                "输出 JSON：analysis、steps、items[{symbol,name,reason,logic,risk}]。禁止只写行业方向而不给代码。";
            rationaleText = "占位：按趋势股目标组装选股提示词（底部/中期趋势观察）。";
        }
        else
        {
            promptText =
                $"请推荐 3~5 只适合 A 股模拟日内做T的具体股票（必须给出真实 6 位代码与简称）。" +
                $"题材偏好「{theme}」；希望约 {days} 个交易日内组合收益接近 {expected}%；" +
                "要求流动性好、波动适中、避免纯题材炒作与 ST；" +
                "输出 JSON：analysis、steps、items[{symbol,name,reason,logic,risk}]（logic 为每只股的选取逻辑）。禁止只写行业方向而不给代码。";
            rationaleText = "占位：根据预期收益、周期与题材偏好组装选股提示词，并强制要求输出具体股票代码与选取逻辑。";
        }

        var placeholder = new
        {
            prompt = promptText,
            rationale = rationaleText,
            analysis = "已由目标参数生成选股提示词（占位适配器）。"
        };
        var json = JsonSerializer.Serialize(placeholder, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(responsePath, json, ct);
        return json;
    }

    public async Task<string> RunStockPickAsync(string prompt, string? contextJson, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var requestPath = Path.Combine(_workDir, $"stockpick_request_{id}.json");
        var responsePath = Path.Combine(_workDir, $"stockpick_response_{id}.json");

        var request = new { type = "StockPick", prompt, contextJson, createdAt = DateTime.UtcNow };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }), ct);

        if (File.Exists(responsePath))
        {
            return await File.ReadAllTextAsync(responsePath, ct);
        }

        var placeholder = new
        {
            analysis =
                $"【选股分析说明】\n" +
                $"1. 理解用户提示词：「{prompt}」。\n" +
                "2. 从流动性、估值与行业匹配度三个维度做初筛。\n" +
                "3. 优先保留成交活跃、适合模拟做T的标的，并标注主要风险。\n" +
                "4. 以下为演示占位结果；可用 Cursor Agent 写入同名 response 文件覆盖为真实分析。",
            steps = new[]
            {
                "解析提示词意图与约束",
                "按行业/主题缩小候选范围",
                "评估流动性与波动是否适合做T",
                "生成候选列表并附理由与风险提示"
            },
            items = new[]
            {
                new
                {
                    symbol = "600036",
                    name = "招商银行",
                    reason = "大盘银行股，估值相对稳健，适合模拟做T。",
                    logic = $"选取逻辑：对照提示词「{TruncateForLogic(prompt)}」——流动性好、波动适中；银行板块便于观察高抛低吸；作为占位候选供审阅。",
                    risk = "受利率与监管政策影响；需人工确认基本面与仓位。"
                },
                new
                {
                    symbol = "000858",
                    name = "五粮液",
                    reason = "白酒龙头，关注度高，盘中波动可见。",
                    logic = $"选取逻辑：对照提示词「{TruncateForLogic(prompt)}」——高关注度与日内振幅便于验证做T触发；题材/消费情绪需人工复核。",
                    risk = "消费情绪与估值波动较大，不宜重仓。"
                }
            },
            note = "未检测到 Agent 响应文件，已生成带分析说明的占位建议。"
        };

        var json = JsonSerializer.Serialize(placeholder, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(responsePath, json, ct);
        return json;
    }

    public async Task<string> RunStrategyUpdateAsync(string prompt, string payloadJson, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var requestPath = Path.Combine(_workDir, $"strategy_request_{id}.json");
        var responsePath = Path.Combine(_workDir, $"strategy_response_{id}.json");

        var request = new { type = "StrategyUpdate", prompt, payloadJson, createdAt = DateTime.UtcNow };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }), ct);

        if (File.Exists(responsePath))
        {
            return await File.ReadAllTextAsync(responsePath, ct);
        }

        var asOf = DateTime.Now;
        DateTime forecastTargetDate;
        if (asOf.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && asOf.TimeOfDay < new TimeSpan(9, 30, 0))
        {
            forecastTargetDate = asOf.Date;
        }
        else
        {
            forecastTargetDate = asOf.Date.AddDays(1);
            while (forecastTargetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                forecastTargetDate = forecastTargetDate.AddDays(1);
        }

        // 若请求载荷已带目标日，优先采用
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            if (doc.RootElement.TryGetProperty("forecastTargetDate", out var fd)
                && fd.ValueKind == JsonValueKind.String
                && DateTime.TryParse(fd.GetString(), out var parsed))
            {
                forecastTargetDate = parsed.Date;
            }
        }
        catch
        {
            // ignore
        }

        var last = TryReadLastPrice(payloadJson) ?? 10m;
        var predictedHigh = Math.Round(last * 1.015m, 2);
        var predictedLow = Math.Round(last * 0.985m, 2);

        var placeholder = new
        {
            analysis =
                $"【策略更新分析说明】\n" +
                $"1. 理解优化目标：「{prompt}」。\n" +
                "2. 结合近期盈亏与成交次数，建议略收紧单笔仓位、放宽日内次数上限。\n" +
                "3. 参数调整不得绕过涨跌停/资金等硬风控。\n" +
                $"4. 占位预测目标日 {forecastTargetDate:yyyy-MM-dd} 高低：高 {predictedHigh} / 低 {predictedLow}（相对参考价 {last} ±1.5%；交易日15:00前=当日，否则=下一交易日）。",
            steps = new[]
            {
                "读取当前策略参数与近期盈亏",
                "识别过高触发次数或过大回撤",
                "给出可审阅的参数建议",
                "按开市前/后规则输出目标日最高/最低价预测"
            },
            parameters = new
            {
                mode = "AdaptiveIntradayT",
                version = 2,
                style = "mean_reversion",
                aggression = 0.4,
                session = new
                {
                    prefer = new[] { "10:00-11:20", "13:30-14:40" },
                    avoid = new[] { "09:30-09:45", "14:50-15:00" },
                    forceFlatBefore = "14:55"
                },
                triggers = new
                {
                    buyDeviationPct = new { @base = -0.7, min = -1.4, max = -0.4 },
                    sellDeviationPct = new { @base = 0.7, min = 0.4, max = 1.4 },
                    anchor = "vwap"
                },
                sizing = new
                {
                    positionPctPerTrade = new { @base = 0.15, min = 0.1, max = 0.3 },
                    maxTradesPerDay = 6,
                    maxIntradayExposurePct = 0.4,
                    keepBaseLot = 100
                },
                risk = new
                {
                    dayLossStopPct = -1.2,
                    symbolPauseOnConsecutiveLosses = 3,
                    blockNearLimitPct = 1.5,
                    maxSlippageBps = 20
                },
                factors = new
                {
                    useVolumeConfirm = true,
                    minVolumeRatio = 0.8,
                    trendFilter = "none"
                },
                aiNotes = "占位：偏震荡高抛低吸，避开早盘噪声",
                forecast = new
                {
                    forDate = forecastTargetDate.ToString("yyyy-MM-dd"),
                    predictedHigh,
                    predictedLow,
                    asOf = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    rationale = "占位：按参考价±1.5%给出目标日高低区间（交易日15:00前=当日，否则=下一交易日）",
                    source = "placeholder",
                    countsTowardAccuracy = false
                },
                feeModel = "AShareDefault"
            },
            forecast = new
            {
                forDate = forecastTargetDate.ToString("yyyy-MM-dd"),
                predictedHigh,
                predictedLow,
                asOf = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                rationale = "占位：按参考价±1.5%给出目标日高低区间（交易日15:00前=当日，否则=下一交易日）",
                source = "placeholder",
                countsTowardAccuracy = false
            },
            changeNote = $"AI占位建议：{prompt}",
            risk = "须人工确认后方可生效，不得绕过风控硬约束"
        };

        var json = JsonSerializer.Serialize(placeholder, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(responsePath, json, ct);
        return json;
    }

    private static string TruncateForLogic(string? prompt, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "（空提示词）";
        var t = prompt.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= max ? t : t[..max] + "…";
    }

    private static decimal? TryReadLastPrice(string? payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("quote", out var quote) && quote.ValueKind == JsonValueKind.Object)
            {
                if (quote.TryGetProperty("Last", out var last) && last.TryGetDecimal(out var v) && v > 0)
                    return v;
                if (quote.TryGetProperty("last", out last) && last.TryGetDecimal(out v) && v > 0)
                    return v;
            }

            if (root.TryGetProperty("dailyBars", out var bars) && bars.ValueKind == JsonValueKind.Array && bars.GetArrayLength() > 0)
            {
                var lastBar = bars[bars.GetArrayLength() - 1];
                if (lastBar.TryGetProperty("Close", out var c) && c.TryGetDecimal(out var close) && close > 0)
                    return close;
                if (lastBar.TryGetProperty("close", out c) && c.TryGetDecimal(out close) && close > 0)
                    return close;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
