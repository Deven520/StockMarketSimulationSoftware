using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockSim.Domain.Strategies;

/// <summary>
/// 策略画像（AI 写入、版本化落库）。Schema v2：边界+偏好；盘中由 RuntimeOverrides 在边界内自适应。
/// 仍兼容 v1 扁平字段（buyTriggerPct 等）。
/// </summary>
public sealed class StrategyParameters
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "AdaptiveIntradayT";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    /// <summary>mean_reversion | momentum_t | defensive</summary>
    [JsonPropertyName("style")]
    public string Style { get; set; } = "mean_reversion";

    [JsonPropertyName("aggression")]
    public decimal Aggression { get; set; } = 0.45m;

    [JsonPropertyName("session")]
    public SessionPolicy Session { get; set; } = new();

    [JsonPropertyName("triggers")]
    public TriggerPolicy Triggers { get; set; } = new();

    [JsonPropertyName("sizing")]
    public SizingPolicy Sizing { get; set; } = new();

    [JsonPropertyName("risk")]
    public RiskPolicy Risk { get; set; } = new();

    [JsonPropertyName("factors")]
    public FactorPolicy Factors { get; set; } = new();

    [JsonPropertyName("aiNotes")]
    public string? AiNotes { get; set; }

    /// <summary>对目标交易日最高/最低价的预测；盘中做 T 用作软边界（非硬阈值）。</summary>
    [JsonPropertyName("forecast")]
    public ForecastPolicy? Forecast { get; set; }

    /// <summary>入池首日（做T日前）用可用资金建底仓的规则。</summary>
    [JsonPropertyName("baseBuild")]
    public BaseBuildPolicy BaseBuild { get; set; } = new();

    /// <summary>盘中开盘路径分型与剧本开关。</summary>
    [JsonPropertyName("path")]
    public PathPolicy Path { get; set; } = new();

    // —— v1 兼容字段（反序列化后迁移进 Triggers/Sizing/Risk）——
    [JsonPropertyName("buyTriggerPct")]
    public decimal? LegacyBuyTriggerPct { get; set; }

    [JsonPropertyName("sellTriggerPct")]
    public decimal? LegacySellTriggerPct { get; set; }

    [JsonPropertyName("positionPctPerTrade")]
    public decimal? LegacyPositionPctPerTrade { get; set; }

    [JsonPropertyName("maxTradesPerDay")]
    public int? LegacyMaxTradesPerDay { get; set; }

    [JsonPropertyName("stopLossPct")]
    public decimal? LegacyStopLossPct { get; set; }

    [JsonPropertyName("takeProfitPct")]
    public decimal? LegacyTakeProfitPct { get; set; }

    [JsonPropertyName("tradingSession")]
    public string? LegacyTradingSession { get; set; }

    [JsonPropertyName("feeModel")]
    public string FeeModel { get; set; } = "AShareDefault";

    /// <summary>引擎用：最大日交易次数。</summary>
    [JsonIgnore]
    public int MaxTradesPerDay => Sizing.MaxTradesPerDay;

    public static StrategyParameters Default() => new();

    public static StrategyParameters FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default();

        var p = JsonSerializer.Deserialize<StrategyParameters>(json, JsonOptions()) ?? Default();
        p.MigrateLegacy();
        p.NormalizeRanges();
        return p;
    }

    public string ToJson()
    {
        // 输出 v2 干净结构，不写 legacy 扁平字段
        var export = new
        {
            mode = Mode,
            version = Math.Max(Version, 2),
            style = Style,
            aggression = Aggression,
            session = Session,
            triggers = Triggers,
            sizing = Sizing,
            risk = Risk,
            factors = Factors,
            aiNotes = AiNotes,
            forecast = Forecast,
            baseBuild = BaseBuild,
            path = Path,
            feeModel = FeeModel
        };
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    public EffectiveStrategyParams ToEffective(RuntimeOverrides? overrides = null)
    {
        MigrateLegacy();
        NormalizeRanges();
        var o = overrides ?? new RuntimeOverrides();
        var style = !string.IsNullOrWhiteSpace(o.StyleHint) ? o.StyleHint! : Style;
        return new EffectiveStrategyParams
        {
            BuyDeviationPct = Triggers.BuyDeviationPct.Resolve(o.BuyDeviationPct),
            SellDeviationPct = Triggers.SellDeviationPct.Resolve(o.SellDeviationPct),
            PositionPctPerTrade = Sizing.PositionPctPerTrade.Resolve(o.PositionPctPerTrade),
            MaxTradesPerDay = Sizing.MaxTradesPerDay,
            KeepBaseLot = Sizing.KeepBaseLot,
            MaxIntradayExposurePct = Sizing.MaxIntradayExposurePct,
            DayLossStopPct = Risk.DayLossStopPct,
            SymbolPauseOnConsecutiveLosses = Risk.SymbolPauseOnConsecutiveLosses,
            BlockNearLimitPct = Risk.BlockNearLimitPct,
            UseVolumeConfirm = Factors.UseVolumeConfirm,
            MinVolumeRatio = Factors.MinVolumeRatio,
            Anchor = Triggers.Anchor,
            Style = style,
            Paused = o.Paused,
            PauseReason = o.PauseReason,
            PreferWindows = Session.Prefer,
            AvoidWindows = Session.Avoid,
            ForceFlatBefore = Session.ForceFlatBefore,
            BaseBuildEnabled = BaseBuild.Enabled,
            BaseBuildMaxCashPct = Math.Clamp(BaseBuild.MaxCashPct, 0.1m, 1m),
            BaseBuildMinCashReserve = Math.Max(0, BaseBuild.MinCashReserve),
            BaseBuildMinChangePct = BaseBuild.MinChangePct,
            BaseBuildMaxChangePct = BaseBuild.MaxChangePct,
            BaseBuildRequirePreferWindow = BaseBuild.RequirePreferWindow,
            AllowBreakAvoid = o.AllowBreakAvoid,
            BreakAvoidSigma = o.BreakAvoidSigma ?? Path.BreakAvoidSigma,
            PullbackSellPct = o.PullbackSellPct ?? Path.PullbackSellPct,
            BlockNewT = o.BlockNewT,
            SoftBandShrinkScale = o.SoftBandShrinkScale ?? 0.6m,
            PathLabel = o.PathLabel
        };
    }

    private void MigrateLegacy()
    {
        if (LegacyBuyTriggerPct.HasValue && Triggers.BuyDeviationPct.Base == ParamRange.DefaultBuy.Base)
            Triggers.BuyDeviationPct = ParamRange.Around(LegacyBuyTriggerPct.Value, span: 0.4m, negative: true);
        if (LegacySellTriggerPct.HasValue && Triggers.SellDeviationPct.Base == ParamRange.DefaultSell.Base)
            Triggers.SellDeviationPct = ParamRange.Around(LegacySellTriggerPct.Value, span: 0.4m, negative: false);
        if (LegacyPositionPctPerTrade.HasValue)
            Sizing.PositionPctPerTrade = ParamRange.Around(LegacyPositionPctPerTrade.Value, span: 0.1m, negative: false, minFloor: 0.05m, maxCeil: 0.5m);
        if (LegacyMaxTradesPerDay.HasValue)
            Sizing.MaxTradesPerDay = LegacyMaxTradesPerDay.Value;
        if (LegacyStopLossPct.HasValue)
            Risk.DayLossStopPct = LegacyStopLossPct.Value;
        if (LegacyTakeProfitPct.HasValue && Triggers.SellDeviationPct.Base == ParamRange.DefaultSell.Base)
        {
            // takeProfit 作卖出参考时已有 sell；保留 day 目标感写入 aggression 微调即可
        }

        if (string.Equals(Mode, "IntradayT", StringComparison.OrdinalIgnoreCase))
            Mode = "AdaptiveIntradayT";
        if (Version < 2) Version = 2;
    }

    private void NormalizeRanges()
    {
        Triggers.BuyDeviationPct.EnsureOrdered(preferNegative: true);
        Triggers.SellDeviationPct.EnsureOrdered(preferNegative: false);
        Sizing.PositionPctPerTrade.EnsureOrdered(preferNegative: false);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public sealed class SessionPolicy
{
    [JsonPropertyName("prefer")]
    public List<string> Prefer { get; set; } = ["10:00-11:20", "13:30-14:40"];

    [JsonPropertyName("avoid")]
    public List<string> Avoid { get; set; } = ["09:30-09:45", "14:50-15:00"];

    [JsonPropertyName("forceFlatBefore")]
    public string ForceFlatBefore { get; set; } = "14:55";
}

public sealed class TriggerPolicy
{
    [JsonPropertyName("buyDeviationPct")]
    public ParamRange BuyDeviationPct { get; set; } = ParamRange.DefaultBuy;

    [JsonPropertyName("sellDeviationPct")]
    public ParamRange SellDeviationPct { get; set; } = ParamRange.DefaultSell;

    /// <summary>open | vwap | session_vwap</summary>
    [JsonPropertyName("anchor")]
    public string Anchor { get; set; } = "vwap";
}

public sealed class SizingPolicy
{
    [JsonPropertyName("positionPctPerTrade")]
    public ParamRange PositionPctPerTrade { get; set; } = new() { Base = 0.2m, Min = 0.1m, Max = 0.35m };

    [JsonPropertyName("maxTradesPerDay")]
    public int MaxTradesPerDay { get; set; } = 6;

    [JsonPropertyName("maxIntradayExposurePct")]
    public decimal MaxIntradayExposurePct { get; set; } = 0.4m;

    [JsonPropertyName("keepBaseLot")]
    public int KeepBaseLot { get; set; } = 100;
}

public sealed class RiskPolicy
{
    [JsonPropertyName("dayLossStopPct")]
    public decimal DayLossStopPct { get; set; } = -1.5m;

    [JsonPropertyName("symbolPauseOnConsecutiveLosses")]
    public int SymbolPauseOnConsecutiveLosses { get; set; } = 3;

    [JsonPropertyName("blockNearLimitPct")]
    public decimal BlockNearLimitPct { get; set; } = 1.5m;

    [JsonPropertyName("maxSlippageBps")]
    public decimal MaxSlippageBps { get; set; } = 20m;
}

public sealed class FactorPolicy
{
    [JsonPropertyName("useVolumeConfirm")]
    public bool UseVolumeConfirm { get; set; } = true;

    [JsonPropertyName("minVolumeRatio")]
    public decimal MinVolumeRatio { get; set; } = 0.8m;

    [JsonPropertyName("trendFilter")]
    public string TrendFilter { get; set; } = "none";
}

/// <summary>入池后、做T日前：用专属资金按行情建底仓。</summary>
public sealed class BaseBuildPolicy
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>最多动用 AvailableCash 的比例（默认 55%）。</summary>
    [JsonPropertyName("maxCashPct")]
    public decimal MaxCashPct { get; set; } = 0.55m;

    /// <summary>至少保留现金（默认 3 万），供后续低吸。</summary>
    [JsonPropertyName("minCashReserve")]
    public decimal MinCashReserve { get; set; } = 30_000m;

    /// <summary>涨跌幅下限（过低不接飞刀）。</summary>
    [JsonPropertyName("minChangePct")]
    public decimal MinChangePct { get; set; } = -5m;

    /// <summary>涨跌幅上限（过高不追）。</summary>
    [JsonPropertyName("maxChangePct")]
    public decimal MaxChangePct { get; set; } = 2.5m;

    /// <summary>是否仅在 prefer 时段建仓。</summary>
    [JsonPropertyName("requirePreferWindow")]
    public bool RequirePreferWindow { get; set; }
}

/// <summary>目标日高低价预测（写入 StrategyPolicy，并同步落准确率历史表）。</summary>
public sealed class ForecastPolicy
{
    [JsonPropertyName("forDate")]
    public string? ForDate { get; set; }

    [JsonPropertyName("predictedHigh")]
    public decimal PredictedHigh { get; set; }

    [JsonPropertyName("predictedLow")]
    public decimal PredictedLow { get; set; }

    [JsonPropertyName("asOf")]
    public string? AsOf { get; set; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; set; }

    /// <summary>ai | atr_baseline | placeholder | calibrated_ai</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>占位预测应为 false，不进准确率榜。</summary>
    [JsonPropertyName("countsTowardAccuracy")]
    public bool? CountsTowardAccuracy { get; set; }

    /// <summary>hard | softPrefer（默认 softPrefer：偏离达标时不硬拦，仅缩仓）。</summary>
    [JsonPropertyName("softBandMode")]
    public string SoftBandMode { get; set; } = "softPrefer";

    /// <summary>相对参考价（昨收/开盘）钳制预测高的涨幅上限 %。</summary>
    [JsonPropertyName("capHighByLimitPct")]
    public decimal CapHighByLimitPct { get; set; } = 12m;
}

/// <summary>盘中路径分型策略边界。</summary>
public sealed class PathPolicy
{
    [JsonPropertyName("enable")]
    public bool Enable { get; set; } = true;

    [JsonPropertyName("confirmUntil")]
    public string ConfirmUntil { get; set; } = "10:15";

    [JsonPropertyName("breakAvoidSigma")]
    public decimal BreakAvoidSigma { get; set; } = 1.5m;

    [JsonPropertyName("pullbackSellPct")]
    public decimal PullbackSellPct { get; set; } = 0.8m;

    /// <summary>夜更可选：四类路径先验，仅加权开盘假设。</summary>
    [JsonPropertyName("pathPriors")]
    public Dictionary<string, decimal>? PathPriors { get; set; }
}

public sealed class ParamRange
{
    [JsonPropertyName("base")]
    public decimal Base { get; set; }

    [JsonPropertyName("min")]
    public decimal Min { get; set; }

    [JsonPropertyName("max")]
    public decimal Max { get; set; }

    public static ParamRange DefaultBuy => new() { Base = -0.8m, Min = -1.5m, Max = -0.4m };
    public static ParamRange DefaultSell => new() { Base = 0.8m, Min = 0.4m, Max = 1.5m };

    public decimal Resolve(decimal? overrideValue = null)
    {
        var v = overrideValue ?? Base;
        var lo = Math.Min(Min, Max);
        var hi = Math.Max(Min, Max);
        return Math.Clamp(v, lo, hi);
    }

    public void EnsureOrdered(bool preferNegative)
    {
        if (Min > Max) (Min, Max) = (Max, Min);
        if (preferNegative)
        {
            Base = -Math.Abs(Base);
            Min = -Math.Abs(Math.Max(Math.Abs(Min), Math.Abs(Max)));
            Max = -Math.Abs(Math.Min(Math.Abs(Min), Math.Abs(Max)));
            if (Min > Max) (Min, Max) = (Max, Min);
        }
    }

    public static ParamRange Around(decimal center, decimal span, bool negative, decimal minFloor = 0, decimal maxCeil = 100)
    {
        if (negative)
        {
            center = -Math.Abs(center);
            return new ParamRange
            {
                Base = center,
                Min = Math.Min(center - span, center),
                Max = Math.Max(center + span, center) > -0.1m ? -0.1m : Math.Max(center + span, center)
            };
        }

        return new ParamRange
        {
            Base = center,
            Min = Math.Max(minFloor, center - span),
            Max = Math.Min(maxCeil, center + span)
        };
    }
}

/// <summary>盘中自适应覆盖（内存/短缓存，不进策略版本表）。</summary>
public sealed class RuntimeOverrides
{
    public decimal? BuyDeviationPct { get; set; }
    public decimal? SellDeviationPct { get; set; }
    public decimal? PositionPctPerTrade { get; set; }
    public bool Paused { get; set; }
    public string? PauseReason { get; set; }
    public DateTime AsOf { get; set; } = DateTime.Now;
    public string? StyleHint { get; set; }
    public bool AllowBreakAvoid { get; set; }
    public decimal? BreakAvoidSigma { get; set; }
    public decimal? PullbackSellPct { get; set; }
    public bool BlockNewT { get; set; }
    public decimal? SoftBandShrinkScale { get; set; }
    public string? PathLabel { get; set; }
}

/// <summary>边界钳制后的有效参数。</summary>
public sealed class EffectiveStrategyParams
{
    public decimal BuyDeviationPct { get; init; }
    public decimal SellDeviationPct { get; init; }
    public decimal PositionPctPerTrade { get; init; }
    public int MaxTradesPerDay { get; init; }
    public int KeepBaseLot { get; init; }
    public decimal MaxIntradayExposurePct { get; init; }
    public decimal DayLossStopPct { get; init; }
    public int SymbolPauseOnConsecutiveLosses { get; init; }
    public decimal BlockNearLimitPct { get; init; }
    public bool UseVolumeConfirm { get; init; }
    public decimal MinVolumeRatio { get; init; }
    public string Anchor { get; init; } = "vwap";
    public string Style { get; init; } = "mean_reversion";
    public bool Paused { get; init; }
    public string? PauseReason { get; init; }
    public IReadOnlyList<string> PreferWindows { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AvoidWindows { get; init; } = Array.Empty<string>();
    public string ForceFlatBefore { get; init; } = "14:55";

    public bool BaseBuildEnabled { get; init; } = true;
    public decimal BaseBuildMaxCashPct { get; init; } = 0.55m;
    public decimal BaseBuildMinCashReserve { get; init; } = 30_000m;
    public decimal BaseBuildMinChangePct { get; init; } = -5m;
    public decimal BaseBuildMaxChangePct { get; init; } = 2.5m;
    public bool BaseBuildRequirePreferWindow { get; init; }

    public bool AllowBreakAvoid { get; init; }
    public decimal BreakAvoidSigma { get; init; } = 1.5m;
    public decimal PullbackSellPct { get; init; }
    public bool BlockNewT { get; init; }
    public decimal SoftBandShrinkScale { get; init; } = 0.6m;
    public string? PathLabel { get; init; }
}

public enum SignalAction
{
    None = 0,
    Buy = 1,
    Sell = 2
}

public sealed class TradeSignal
{
    public required string Symbol { get; init; }
    public SignalAction Action { get; init; }
    public int Quantity { get; init; }
    public decimal Price { get; init; }
    public string Reason { get; init; } = string.Empty;
    public Guid? StrategyDefinitionId { get; init; }
    public Guid? StockPoolId { get; init; }
    /// <summary>是否为首日/补建底仓买入（成交后应写入 BaseQuantity）。</summary>
    public bool IsBaseBuild { get; init; }
}
