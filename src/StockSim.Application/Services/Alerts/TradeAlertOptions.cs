namespace StockSim.Application.Services.Alerts;

public sealed class TradeAlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>总开关。</summary>
    public bool Enabled { get; set; }

    /// <summary>同股同方向去重窗口（分钟）。</summary>
    public int DedupMinutes { get; set; } = 3;

    /// <summary>HTTP 超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>通用 Webhook（企业微信/飞书/钉钉/自定义），POST JSON 或 text。</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>webhook | markdown | text。企业微信机器人常用 text。</summary>
    public string WebhookFormat { get; set; } = "text";

    /// <summary>PushPlus token（微信推送 https://www.pushplus.plus ）。</summary>
    public string? PushPlusToken { get; set; }

    public string PushPlusTopic { get; set; } = string.Empty;

    public string TitlePrefix { get; set; } = "StockSim";
}
