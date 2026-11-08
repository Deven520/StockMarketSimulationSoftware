using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace StockSim.Web.Helpers;

/// <summary>
/// 将 AI 队列入参/出参 JSON 的可识别英文字段转为中文，仅用于页面展示，不改库存原文。
/// </summary>
public static class AiPayloadChineseDisplay
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly Dictionary<string, string> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // 通用 / 队列
        ["prompt"] = "提示词",
        ["rationale"] = "理由",
        ["analysis"] = "分析说明",
        ["steps"] = "步骤",
        ["items"] = "候选列表",
        ["note"] = "备注",
        ["error"] = "错误",
        ["hint"] = "说明",
        ["message"] = "消息",
        ["ok"] = "成功",
        ["status"] = "状态",
        ["type"] = "类型",
        ["title"] = "标题",
        ["fallback"] = "已回退模板",
        ["queued"] = "已排队",
        ["enqueue"] = "入队参数",
        ["context"] = "上下文",
        ["payloadJson"] = "载荷JSON",
        ["provider"] = "提供方",
        ["createdAt"] = "创建时间",
        ["requestedAt"] = "请求时间",
        ["asOf"] = "数据截止",
        ["email"] = "邮箱",
        ["workerId"] = "Worker标识",

        // 投资目标 / 选股
        ["goalId"] = "目标Id",
        ["expectedReturnPct"] = "预期收益%",
        ["horizonDays"] = "预期周期(天)",
        ["themePreference"] = "题材偏好",
        ["generatedPrompt"] = "生成的提示词",
        ["promptRationale"] = "提示词理由",
        ["symbol"] = "代码",
        ["name"] = "名称",
        ["reason"] = "入选理由",
        ["logic"] = "选取逻辑",
        ["risk"] = "风险",
        ["poolItemId"] = "池成分Id",
        ["poolId"] = "股票池Id",
        ["nightly"] = "夜间刷新",
        ["correlationId"] = "关联Id",

        // 策略输出
        ["parameters"] = "策略参数",
        ["changeNote"] = "变更说明",
        ["aiNotes"] = "AI备注",
        ["mode"] = "模式",
        ["version"] = "版本",
        ["style"] = "风格",
        ["aggression"] = "激进度",
        ["session"] = "交易时段策略",
        ["triggers"] = "触发规则",
        ["sizing"] = "仓位规则",
        ["factors"] = "因子偏好",
        ["forecast"] = "高低价预测",
        ["baseBuild"] = "底仓建仓",
        ["feeModel"] = "费用模型",
        ["tradingSession"] = "交易时段",

        ["buyTriggerPct"] = "买入触发%",
        ["sellTriggerPct"] = "卖出触发%",
        ["positionPctPerTrade"] = "单笔仓位比例",
        ["maxTradesPerDay"] = "单日最大次数",
        ["stopLossPct"] = "止损%",
        ["takeProfitPct"] = "止盈%",
        ["buyMinPct"] = "买入偏离下限%",
        ["buyMaxPct"] = "买入偏离上限%",
        ["sellMinPct"] = "卖出偏离下限%",
        ["sellMaxPct"] = "卖出偏离上限%",
        ["minPositionPct"] = "最小仓位比例",
        ["maxPositionPct"] = "最大仓位比例",
        ["defaultPositionPct"] = "默认仓位比例",
        ["maxDailyLossPct"] = "单日最大亏损%",
        ["maxConsecutiveLosses"] = "连亏上限",
        ["forceFlatMinutesBeforeClose"] = "收盘前强平分钟",
        ["avoidWindows"] = "回避时段",
        ["preferredWindows"] = "偏好时段",
        ["anchor"] = "锚定基准",
        ["start"] = "开始",
        ["end"] = "结束",

        ["forDate"] = "预测目标日",
        ["predictedHigh"] = "预测最高价",
        ["predictedLow"] = "预测最低价",
        ["forecastScope"] = "预测范围",
        ["forecastRule"] = "预测规则",
        ["forecastTargetDate"] = "预测目标日",
        ["expectedOutput"] = "期望输出结构",
        ["currentParameters"] = "当前策略参数",
        ["quotes"] = "报价",
        ["bars"] = "K线",
        ["news"] = "新闻",
        ["hotNews"] = "热点新闻",
        ["stockNews"] = "个股新闻",
        ["trades"] = "成交",
        ["decisionLogs"] = "决策日志",
        ["valuation"] = "估值",
        ["board"] = "盘口",
        ["last"] = "最新价",
        ["prevClose"] = "昨收",
        ["open"] = "开盘",
        ["high"] = "最高",
        ["low"] = "最低",
        ["close"] = "收盘",
        ["volume"] = "成交量",
        ["amount"] = "成交额",
        ["changePct"] = "涨跌幅%",
        ["pe"] = "市盈率",
        ["pb"] = "市净率",
        ["marketCap"] = "市值",
        ["date"] = "日期",
        ["time"] = "时间",
        ["side"] = "方向",
        ["qty"] = "数量",
        ["quantity"] = "数量",
        ["price"] = "价格",
        ["fee"] = "费用",
        ["state"] = "状态机状态",
        ["rejectReason"] = "拒绝原因",
        ["effectiveParams"] = "有效参数",
        ["sameTradingDay"] = "当日",
        ["nextTradingDay"] = "下一交易日",

        // Policy 子字段常见名
        ["buy"] = "买入",
        ["sell"] = "卖出",
        ["min"] = "最小",
        ["max"] = "最大",
        ["default"] = "默认",
        ["maxLossStreak"] = "最大连亏",
        ["dayLossLimitPct"] = "日亏上限%",
        ["volLookback"] = "波动回看",
        ["volumeRatio"] = "量比",
        ["prefer"] = "偏好时段",
        ["avoid"] = "回避时段",
        ["weight"] = "权重",
        ["cashBudget"] = "资金预算",
        ["initialCash"] = "初始资金",
        ["availableCash"] = "可用资金",
        ["shares"] = "股数",
        ["lots"] = "手数",
        ["pctOfAvailable"] = "可用资金占比",
        ["allowWhenUpPctBelow"] = "涨幅低于时允许",
        ["minLots"] = "最少手数",
        ["maxLots"] = "最多手数",
        ["buyDeviationPct"] = "买入偏离%",
        ["sellDeviationPct"] = "卖出偏离%",
        ["base"] = "基准值",
        ["forceFlatBefore"] = "收盘前强平时刻",
        ["keepBaseLot"] = "保留底仓手数",
        ["maxIntradayExposurePct"] = "日内最大敞口%",
        ["dayLossStopPct"] = "日亏停%",
        ["symbolPauseOnConsecutiveLosses"] = "连亏暂停次数",
        ["blockNearLimitPct"] = "近涨跌停禁做%",
        ["useVolumeConfirm"] = "量能确认",
        ["minVolumeRatio"] = "最小量比",
        ["enabled"] = "启用",
        ["maxCashPct"] = "最大用资比例",
        ["minCashReserve"] = "最低留存资金",
        ["minChangePct"] = "涨跌幅下限%",
        ["maxChangePct"] = "涨跌幅上限%",
        ["requirePreferWindow"] = "需在偏好时段",
    };

    private static readonly Dictionary<string, string> ValueMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["true"] = "是",
        ["false"] = "否",
        ["StockPick"] = "选股",
        ["StrategyUpdate"] = "策略更新",
        ["GoalToPrompt"] = "目标→提示词",
        ["IntradayT"] = "日内做T",
        ["AdaptiveIntradayT"] = "自适应日内做T",
        ["ContinuousAuction"] = "连续竞价",
        ["AShareDefault"] = "A股默认费用",
        ["mean_reversion"] = "均值回归",
        ["momentum_t"] = "动量做T",
        ["defensive"] = "防守",
        ["VWAP"] = "VWAP均价",
        ["session_vwap"] = "分时VWAP",
        ["PrevClose"] = "昨收",
        ["sameTradingDay"] = "当日",
        ["nextTradingDay"] = "下一交易日",
        ["Pending"] = "排队中",
        ["Running"] = "执行中",
        ["Succeeded"] = "成功",
        ["Failed"] = "失败",
        ["Buy"] = "买入",
        ["Sell"] = "卖出",
        ["Idle"] = "空闲",
        ["SoldHigh"] = "已高抛",
        ["BoughtLow"] = "已低吸",
        ["RiskHalt"] = "风控暂停",
        ["SessionAvoid"] = "时段回避",
        ["ForceFlat"] = "强平",
        ["CursorSdk"] = "Cursor SDK",
    };

    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "（空）";

        var text = raw.Trim();
        if ((text.StartsWith('{') && text.EndsWith('}'))
            || (text.StartsWith('[') && text.EndsWith(']')))
        {
            try
            {
                var node = JsonNode.Parse(text);
                var localized = LocalizeNode(node);
                return localized?.ToJsonString(PrettyOptions) ?? text;
            }
            catch
            {
                /* keep raw */
            }
        }

        return LocalizePlainValue(text);
    }

    public static string StatusName(string status) =>
        ValueMap.TryGetValue(status, out var cn) ? cn : status;

    private static JsonNode? LocalizeNode(JsonNode? node)
    {
        if (node is null) return null;

        switch (node)
        {
            case JsonObject obj:
            {
                var next = new JsonObject();
                foreach (var kv in obj)
                {
                    var key = LocalizeKey(kv.Key);
                    // 避免重名键互相覆盖：若冲突则保留「中文(原键)」
                    if (next.ContainsKey(key) && !string.Equals(key, kv.Key, StringComparison.Ordinal))
                        key = $"{key}({kv.Key})";
                    next[key] = LocalizeNode(kv.Value?.DeepClone());
                }

                return next;
            }
            case JsonArray arr:
            {
                var next = new JsonArray();
                foreach (var item in arr)
                    next.Add(LocalizeNode(item?.DeepClone()));
                return next;
            }
            case JsonValue value:
                return LocalizeJsonValue(value);
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode LocalizeJsonValue(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b))
            return JsonValue.Create(b ? "是" : "否");

        if (value.TryGetValue<string>(out var s) && s is not null)
            return JsonValue.Create(LocalizePlainValue(s));

        // 数字等保持原样
        return value.DeepClone();
    }

    private static string LocalizeKey(string key)
    {
        if (KeyMap.TryGetValue(key, out var cn))
            return cn;

        // camelCase → 尽量保留可读性：未知键保持原样
        return key;
    }

    private static string LocalizePlainValue(string text)
    {
        if (ValueMap.TryGetValue(text, out var cn))
            return cn;

        // ISO 时间略作本地可读（失败则原样）
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt)
            && text.Contains('T', StringComparison.Ordinal))
        {
            var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
            return local.ToString("yyyy-MM-dd HH:mm:ss");
        }

        return text;
    }
}
