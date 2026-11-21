namespace StockSim.Domain.Strategies;

/// <summary>
/// 次日预涨题材结构化组装：板块 + 风格 + 可选催化；空心口号改写建议。
/// </summary>
public static class NextDayThemeComposer
{
    /// <summary>板块选项（含 SectorAliases 键与常用扩展）。</summary>
    public static IReadOnlyList<string> SectorOptions { get; } =
        new[]
        {
            "半导体", "新能源", "电网设备", "医药", "建材", "机器人",
            "军工", "金融", "消费", "物流", "地产", "汽车", "计算机", "通信"
        };

    /// <summary>风格模板（按实证胜率倾向排序）。</summary>
    public static IReadOnlyList<ThemeStyleOption> StyleOptions { get; } =
        new[]
        {
            new ThemeStyleOption("oversold", "超跌反弹", "超跌反弹",
                Recommended: true, HighRisk: false, RequiresSector: false),
            new ThemeStyleOption("volume", "持续放量", "持续放量",
                Recommended: true, HighRisk: false, RequiresSector: true),
            new ThemeStyleOption("stabilize", "低位企稳", "低位企稳",
                Recommended: true, HighRisk: false, RequiresSector: false),
            new ThemeStyleOption("policy_sector", "政策+板块共振", "政策共振",
                Recommended: false, HighRisk: false, RequiresSector: true),
            new ThemeStyleOption("heavy_news", "重大利好（慎用）", "有重大利好",
                Recommended: false, HighRisk: true, RequiresSector: true)
        };

    public static IReadOnlyList<string> CatalystOptions { get; } =
        new[] { "无", "订单", "政策", "业绩", "放量" };

    /// <summary>拼装题材句：板块 + 风格 + 催化（跳过「无」）。</summary>
    public static string Compose(string? sector, string? styleIdOrPhrase, string? catalyst)
    {
        var style = ResolveStyle(styleIdOrPhrase);
        var stylePhrase = style?.Phrase ?? styleIdOrPhrase?.Trim();
        var sec = string.IsNullOrWhiteSpace(sector) ? null : sector.Trim();
        var cat = string.IsNullOrWhiteSpace(catalyst) || catalyst.Trim() is "无"
            ? null
            : catalyst.Trim();

        if (style is { HighRisk: true } && string.IsNullOrWhiteSpace(sec))
            throw new ArgumentException("「重大利好」类须指定具体板块，勿单独使用空心叙事。");

        if (style is { RequiresSector: true } && string.IsNullOrWhiteSpace(sec))
            throw new ArgumentException($"风格「{style.Label}」须选择板块，例如「半导体{style.Phrase}」。");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sec))
            parts.Add(sec);
        if (!string.IsNullOrWhiteSpace(stylePhrase))
            parts.Add(stylePhrase);
        if (!string.IsNullOrWhiteSpace(cat)
            && (stylePhrase is null
                || !stylePhrase.Contains(cat, StringComparison.OrdinalIgnoreCase)))
            parts.Add(cat);

        if (parts.Count == 0)
            throw new ArgumentException("请至少选择风格，或填写自定义题材。");

        // 板块+风格紧挨（半导体超跌反弹）；催化用 + 连接
        if (parts.Count == 1) return parts[0];
        if (parts.Count == 2 && cat is null)
            return parts[0] + parts[1];
        if (sec is not null && stylePhrase is not null && cat is not null)
            return sec + stylePhrase + "+" + cat;
        if (sec is not null && stylePhrase is not null)
            return sec + stylePhrase;
        return string.Join("+", parts);
    }

    public static ThemeStyleOption? ResolveStyle(string? idOrPhrase)
    {
        if (string.IsNullOrWhiteSpace(idOrPhrase)) return null;
        var t = idOrPhrase.Trim();
        return StyleOptions.FirstOrDefault(s =>
            s.Id.Equals(t, StringComparison.OrdinalIgnoreCase)
            || s.Phrase.Equals(t, StringComparison.OrdinalIgnoreCase)
            || s.Label.Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>弱市仅保留非 HighRisk、推荐或超跌/企稳类风格。</summary>
    public static IReadOnlyList<ThemeStyleOption> StylesForMarket(bool weakOrDragHeavy) =>
        weakOrDragHeavy
            ? StyleOptions.Where(s => !s.HighRisk && (s.Recommended || s.Id is "oversold" or "stabilize" or "volume"))
                .ToList()
            : StyleOptions;

    /// <summary>空心/叙事题材 → 改写建议（不改写则返回 null）。</summary>
    public static ThemeRewriteSuggestion? TrySuggestRewrite(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return null;
        var raw = theme.Trim();
        var cls = NextDayPickGuard.ClassifyTheme(raw);
        var vague = NextDayPickGuard.IsVagueThemeSlogan(raw);
        var blocked = cls == NextDayThemeClass.NarrativeBlocked;
        if (!vague && !blocked && cls != NextDayThemeClass.SoftPenalty)
            return null;
        if (!vague && !blocked && NextDayPickGuard.HasSectorCue(raw))
            return null;

        var preferred = "超跌反弹";
        var withSectorHints = SectorOptions.Take(4)
            .Select(s => s + "持续放量")
            .ToList();
        var alts = new List<string> { preferred };
        alts.AddRange(withSectorHints);
        alts.Add("医药低位企稳");

        var reason = blocked
            ? $"「{raw}」属纯叙事题材，历史方向命中极差，禁止直接入队。"
            : vague
                ? $"「{raw}」为空心口号（无板块实体），历史命中一般，建议改为可验证风格。"
                : $"「{raw}」约束偏弱，建议补板块或改用超跌/放量模板。";

        return new ThemeRewriteSuggestion(raw, preferred, alts.Distinct(StringComparer.Ordinal).ToList(), reason);
    }

    /// <summary>
    /// 规范化入队题材：优先结构化组装；空心词在 acceptRewrite 时改写为推荐模板。
    /// </summary>
    public static ThemeNormalizeResult NormalizeForNextDay(
        string? freeText,
        string? sector,
        string? styleIdOrPhrase,
        string? catalyst,
        bool acceptRewrite,
        bool weakOrDragHeavy)
    {
        // 结构化优先
        if (!string.IsNullOrWhiteSpace(sector) || !string.IsNullOrWhiteSpace(styleIdOrPhrase))
        {
            var style = ResolveStyle(styleIdOrPhrase);
            if (weakOrDragHeavy && style is { HighRisk: true })
                throw new ArgumentException("弱市/高 MarketDrag 下禁用「重大利好」类题材，请改选超跌反弹或板块+持续放量。");

            var composed = Compose(sector, styleIdOrPhrase, catalyst);
            var cls = NextDayPickGuard.ClassifyTheme(composed);
            if (cls == NextDayThemeClass.NarrativeBlocked)
                throw new ArgumentException(
                    NextDayPickGuard.ThemeClassHint(cls) ?? "题材不可用，请换模板。");
            return new ThemeNormalizeResult(composed, Rewritten: false, null, cls);
        }

        var text = (freeText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("请选择板块+风格，或填写自定义题材。");

        if (weakOrDragHeavy && NextDayPickGuard.IsVagueThemeSlogan(text))
            throw new ArgumentException(
                "弱市下禁止空心题材口号。请用三块选择器选「板块+超跌反弹/持续放量」。");

        var suggestion = TrySuggestRewrite(text);
        var themeClass = NextDayPickGuard.ClassifyTheme(text);

        if (themeClass == NextDayThemeClass.NarrativeBlocked)
        {
            if (!acceptRewrite || suggestion is null)
                throw new ArgumentException(
                    (suggestion?.Reason ?? "纯叙事题材不可用。")
                    + " 可改选：" + string.Join(" / ", suggestion?.Alternatives.Take(3) ?? new[] { "超跌反弹" })
                    + "（勾选「接受题材改写」将自动替换为推荐模板）。");
            return new ThemeNormalizeResult(suggestion.Preferred, Rewritten: true, suggestion, NextDayThemeClass.Ok);
        }

        if (suggestion is not null && NextDayPickGuard.IsVagueThemeSlogan(text))
        {
            if (!acceptRewrite)
                throw new ArgumentException(
                    suggestion.Reason
                    + " 推荐：" + string.Join("、", suggestion.Alternatives.Take(3))
                    + "。确认后请勾选「接受题材改写」或改用上方三块选择器。");
            return new ThemeNormalizeResult(suggestion.Preferred, Rewritten: true, suggestion, NextDayThemeClass.Ok);
        }

        return new ThemeNormalizeResult(text, Rewritten: false, suggestion, themeClass);
    }
}

public sealed record ThemeStyleOption(
    string Id,
    string Label,
    string Phrase,
    bool Recommended,
    bool HighRisk,
    bool RequiresSector);

public sealed record ThemeRewriteSuggestion(
    string Original,
    string Preferred,
    IReadOnlyList<string> Alternatives,
    string Reason);

public sealed record ThemeNormalizeResult(
    string Theme,
    bool Rewritten,
    ThemeRewriteSuggestion? Suggestion,
    NextDayThemeClass ThemeClass);
