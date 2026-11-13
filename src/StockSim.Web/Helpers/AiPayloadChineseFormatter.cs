using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StockSim.Web.Helpers;

/// <summary>将 AI 队列入参/出参 JSON 中可识别的英文键与枚举值转为中文展示。</summary>
public static class AiPayloadChineseFormatter
{
    private static readonly Dictionary<string, string> KeyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["symbol"] = "代码",
        ["name"] = "名称",
        ["expectedReturnPct"] = "预期收益%",
        ["horizonDays"] = "周期(天)",
        ["themePreference"] = "题材偏好",
        ["tradeStyle"] = "选股类型",
        ["tradeStyleLabel"] = "选股类型说明",
        ["asOf"] = "截至时间",
        ["goalId"] = "目标Id",
        ["queued"] = "已入队",
        ["prompt"] = "提示词",
        ["rationale"] = "生成说明",
        ["analysis"] = "分析",
        ["steps"] = "步骤",
        ["items"] = "候选列表",
        ["reason"] = "理由",
        ["logic"] = "选取逻辑",
        ["fallback"] = "回退模板",
        ["error"] = "错误",
        ["poolItemId"] = "池成分Id",
        ["nightly"] = "夜更",
        ["parameters"] = "策略参数",
        ["changeNote"] = "变更说明",
        ["mode"] = "模式",
        ["version"] = "版本",
        ["style"] = "风格",
        ["aggression"] = "激进度",
        ["session"] = "时段",
        ["triggers"] = "触发条件",
        ["sizing"] = "仓位",
        ["risk"] = "风险/风控",
        ["factors"] = "因子",
        ["aiNotes"] = "AI备注",
        ["forecast"] = "高低价预测",
        ["baseBuild"] = "建底仓",
        ["buyTriggerPct"] = "买入触发%",
        ["sellTriggerPct"] = "卖出触发%",
        ["positionPctPerTrade"] = "单笔仓位%",
        ["maxTradesPerDay"] = "日最大成交次数",
        ["stopLossPct"] = "止损%",
        ["takeProfitPct"] = "止盈%",
        ["tradingSession"] = "交易时段",
        ["feeModel"] = "费用模型",
        ["prefer"] = "偏好时段",
        ["avoid"] = "回避时段",
        ["forceFlatBefore"] = "强制平仓前",
        ["buyDeviationPct"] = "买入偏离%",
        ["sellDeviationPct"] = "卖出偏离%",
        ["anchor"] = "锚点",
        ["maxIntradayExposurePct"] = "日内最大敞口%",
        ["keepBaseLot"] = "保留底仓手数",
        ["dayLossStopPct"] = "日亏停机%",
        ["symbolPauseOnConsecutiveLosses"] = "连亏暂停次数",
        ["blockNearLimitPct"] = "近涨跌停屏蔽%",
        ["maxSlippageBps"] = "最大滑点(bp)",
        ["useVolumeConfirm"] = "量能确认",
        ["minVolumeRatio"] = "最小量比",
        ["trendFilter"] = "趋势过滤",
        ["enabled"] = "启用",
        ["maxCashPct"] = "最大资金占比",
        ["minCashReserve"] = "最低留存现金",
        ["minChangePct"] = "涨跌幅下限%",
        ["maxChangePct"] = "涨跌幅上限%",
        ["requirePreferWindow"] = "仅偏好时段",
        ["forDate"] = "预测目标日",
        ["predictedHigh"] = "预测最高价",
        ["predictedLow"] = "预测最低价",
        ["source"] = "来源",
        ["countsTowardAccuracy"] = "计入准确率",
        ["base"] = "基准",
        ["min"] = "最小",
        ["max"] = "最大",
        ["enqueue"] = "入队上下文",
        ["runContext"] = "执行上下文",
        ["output"] = "输出",
        ["result"] = "结果",
        ["ok"] = "成功",
        ["message"] = "消息",
        ["aiJobId"] = "AI任务Id",
        ["inputPayload"] = "输入载荷",
        ["outputPayload"] = "输出载荷",
        ["forecastTargetDate"] = "预测目标日",
        ["forecastScope"] = "预测范围",
        ["forecastRule"] = "预测规则",
        ["forecastEvaluation"] = "预测对账",
        ["forecastCalibration"] = "预测校准",
        ["atrBaselineHint"] = "ATR基线提示",
        ["hotNews"] = "热点新闻",
        ["stockNews"] = "个股新闻",
        ["title"] = "标题",
        ["summary"] = "摘要",
        ["publishedAt"] = "发布时间",
        ["meanHighDeviationPct"] = "高价平均偏差%",
        ["meanLowDeviationPct"] = "低价平均偏差%",
        ["avgAiAccuracyPct"] = "AI平均准确率%",
        ["requestedAt"] = "请求时间",
        ["createdAt"] = "创建时间",
        ["type"] = "类型",
        ["contextJson"] = "上下文JSON",
        ["strategyId"] = "策略Id",
        ["intent"] = "意图",
        ["recentPnL"] = "近期盈亏",
        ["tradeDate"] = "交易日",
        ["realizedPnL"] = "已实现盈亏",
        ["fees"] = "费用",
        ["tradeCount"] = "成交笔数",
        ["winRate"] = "胜率",
        ["status"] = "状态",
        ["pending"] = "待处理",
        ["running"] = "执行中",
        ["succeeded"] = "成功",
        ["failed"] = "失败",
        ["quantity"] = "数量",
        ["price"] = "价格",
        ["side"] = "方向",
        ["notes"] = "备注",
        ["selectionLogic"] = "选取逻辑",
        ["expectedOutput"] = "期望输出结构",
        ["parametersJson"] = "参数JSON",
        ["policy"] = "策略画像",
        ["board"] = "行情面板",
        ["lastPrice"] = "最新价",
        ["changePercent"] = "涨跌幅%",
        ["open"] = "开盘",
        ["high"] = "最高",
        ["low"] = "最低",
        ["close"] = "收盘",
        ["volume"] = "成交量",
        ["preClose"] = "昨收",
        ["dailyBars"] = "日K",
        ["minuteBars"] = "分钟K",
        ["decisionLogs"] = "决策日志",
        ["tradesToday"] = "今日成交",
        ["position"] = "持仓",
        ["avgCost"] = "成本",
        ["baseQuantity"] = "底仓",
        ["sellableQuantity"] = "可卖",
        ["availableCash"] = "可用资金",
        ["initialCash"] = "初始资金",
        ["allowTFromDate"] = "做T起始日",
        ["notifyOnTrade"] = "成交通知",
        ["boundStrategyId"] = "绑定策略Id",
        ["stockPoolId"] = "股票池Id",
        ["tags"] = "标签",
        ["remark"] = "备注",
        ["correlationId"] = "关联Id",
        ["workerId"] = "Worker",
        ["startedAt"] = "开始时间",
        ["completedAt"] = "完成时间",
        ["errorMessage"] = "错误信息",
        ["jobType"] = "任务类型",
        ["taskType"] = "队列任务类型",
        ["sameTradingDay"] = "当日",
        ["nextTradingDay"] = "下一交易日"
    };

    private static readonly Dictionary<string, string> ValueLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IntradayT"] = "做T股",
        ["TrendSwing"] = "趋势股",
        ["NextDayRise"] = "次日上涨预选",
        ["nextTradingDay"] = "下一交易日",
        ["marketContext"] = "市场上下文",
        ["hotNews"] = "热点新闻",
        ["AdaptiveIntradayT"] = "自适应日内做T",
        ["true"] = "是",
        ["false"] = "否",
        ["sameTradingDay"] = "当日",
        ["nextTradingDay"] = "下一交易日",
        ["ai"] = "AI",
        ["atr_baseline"] = "ATR基线",
        ["placeholder"] = "占位",
        ["calibrated_ai"] = "校准AI",
        ["Ai"] = "AI",
        ["AtrBaseline"] = "ATR基线",
        ["Placeholder"] = "占位",
        ["CalibratedAi"] = "校准AI",
        ["vwap"] = "VWAP",
        ["open"] = "开盘价",
        ["none"] = "无",
        ["mean_reversion"] = "均值回归",
        ["trend"] = "趋势",
        ["Buy"] = "买",
        ["Sell"] = "卖",
        ["Pending"] = "待处理",
        ["Running"] = "执行中",
        ["Succeeded"] = "成功",
        ["Failed"] = "失败",
        ["Created"] = "已创建",
        ["StockPick"] = "选股",
        ["StrategyUpdate"] = "策略更新",
        ["GoalToPrompt"] = "目标→提示词",
        ["StrategyRefresh"] = "策略刷新",
        ["DailyTReview"] = "每日复盘",
        ["DownsideRiskPredict"] = "下跌风险预测",
        ["AShareDefault"] = "A股默认费用"
    };

    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "（空）";
        var t = raw.Trim();
        if ((t.StartsWith('{') && t.EndsWith('}')) || (t.StartsWith('[') && t.EndsWith(']')))
        {
            try
            {
                using var doc = JsonDocument.Parse(t);
                var sb = new StringBuilder();
                WriteElement(sb, doc.RootElement, indent: 0, keyHint: null);
                return sb.ToString().TrimEnd();
            }
            catch
            {
                /* fall through */
            }
        }

        return TranslateScalar(t);
    }

    private static void WriteElement(StringBuilder sb, JsonElement el, int indent, string? keyHint)
    {
        var pad = new string(' ', indent * 2);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.EnumerateObject().Any() == false)
                {
                    sb.AppendLine($"{pad}{{ }}");
                    return;
                }

                foreach (var prop in el.EnumerateObject())
                {
                    var label = TranslateKey(prop.Name);
                    var child = prop.Value;
                    if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        sb.AppendLine($"{pad}{label}:");
                        WriteElement(sb, child, indent + 1, prop.Name);
                    }
                    else
                    {
                        sb.AppendLine($"{pad}{label}: {FormatScalar(child, prop.Name)}");
                    }
                }

                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    i++;
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        sb.AppendLine($"{pad}- [{i}]");
                        WriteElement(sb, item, indent + 1, keyHint);
                    }
                    else
                    {
                        sb.AppendLine($"{pad}- {FormatScalar(item, keyHint)}");
                    }
                }

                if (i == 0) sb.AppendLine($"{pad}（空列表）");
                break;

            default:
                sb.AppendLine($"{pad}{FormatScalar(el, keyHint)}");
                break;
        }
    }

    private static string TranslateKey(string key)
    {
        if (KeyLabels.TryGetValue(key, out var cn))
            return $"{cn}（{key}）";
        return key;
    }

    private static string FormatScalar(JsonElement el, string? keyHint)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => TranslateScalar(el.GetString() ?? "", keyHint),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Null => "空",
            JsonValueKind.Number => el.TryGetDecimal(out var d)
                ? d.ToString(CultureInfo.InvariantCulture)
                : el.GetRawText(),
            _ => el.GetRawText()
        };
    }

    private static string TranslateScalar(string value, string? keyHint = null)
    {
        if (string.IsNullOrEmpty(value)) return "（空）";

        // GUID / 股票代码等保持原样
        if (Guid.TryParse(value, out _)) return value;
        if (value.Length == 6 && value.All(char.IsDigit)) return value;

        if (ValueLabels.TryGetValue(value, out var mapped))
            return mapped;

        // tradeStyle 等字段的字符串枚举
        if (!string.IsNullOrEmpty(keyHint)
            && keyHint.Equals("tradeStyle", StringComparison.OrdinalIgnoreCase)
            && ValueLabels.TryGetValue(value, out var style))
            return style;

        return value;
    }
}
