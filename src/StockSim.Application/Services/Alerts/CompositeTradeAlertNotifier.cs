using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StockSim.Application.Services.Alerts;

/// <summary>Webhook + PushPlus 组合通知；带同股同向去重；失败不抛出。</summary>
public sealed class CompositeTradeAlertNotifier : ITradeAlertNotifier
{
    private readonly HttpClient _http;
    private readonly TradeAlertOptions _options;
    private readonly ILogger<CompositeTradeAlertNotifier> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastSent = new(StringComparer.OrdinalIgnoreCase);

    public CompositeTradeAlertNotifier(
        HttpClient http,
        IOptions<TradeAlertOptions> options,
        ILogger<CompositeTradeAlertNotifier> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        // 默认偏短会导致 PushPlus 偶发超时；至少 10 秒
        var timeoutSec = Math.Clamp(_options.TimeoutSeconds <= 0 ? 15 : _options.TimeoutSeconds, 5, 60);
        _http.Timeout = TimeSpan.FromSeconds(timeoutSec);
    }

    public async Task NotifyAsync(TradeAlertMessage message, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        var dedupKey = $"{message.Symbol}|{message.Side}";
        var window = TimeSpan.FromMinutes(Math.Max(0, _options.DedupMinutes));
        if (window > TimeSpan.Zero
            && _lastSent.TryGetValue(dedupKey, out var last)
            && DateTime.UtcNow - last < window)
        {
            _logger.LogDebug("Trade alert dedup skipped {Key}", dedupKey);
            return;
        }

        var (_, anyOk, _) = await SendCoreAsync(message, cancellationToken);
        if (anyOk)
            _lastSent[dedupKey] = DateTime.UtcNow;
    }

    public async Task<(bool Ok, string Message)> SendTestAsync(
        TradeAlertMessage message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookUrl)
            && string.IsNullOrWhiteSpace(_options.PushPlusToken))
        {
            return (false,
                "未配置 Alerts:WebhookUrl / PushPlusToken。请在 Web 的 appsettings.json 填写后重启（正式成交通知还需 Worker 同步配置并 Enabled=true）");
        }

        var (text, anyOk, detail) = await SendCoreAsync(message, cancellationToken);
        if (!anyOk)
        {
            return (false,
                string.IsNullOrWhiteSpace(detail)
                    ? "测试推送失败，请查看 Web 日志（Webhook/PushPlus 返回非成功）"
                    : $"测试推送失败：{detail}");
        }

        var channels = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.WebhookUrl)) channels.Add("Webhook");
        if (!string.IsNullOrWhiteSpace(_options.PushPlusToken)) channels.Add("PushPlus");
        var enabledHint = _options.Enabled
            ? ""
            : " 注意：当前 Alerts:Enabled=false，实盘成交不会推送，测试可发出。";
        return (true, $"测试推送已发出（{string.Join("+", channels)}）：{text}.{enabledHint}");
    }

    private async Task<(string Text, bool AnyOk, string Detail)> SendCoreAsync(
        TradeAlertMessage message,
        CancellationToken cancellationToken)
    {
        var title = message.FormatTitle();
        var text = message.FormatText(_options.TitlePrefix);
        var anyOk = false;
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            var (ok, err) = await TrySendWebhookAsync(text, cancellationToken);
            anyOk |= ok;
            if (!ok) details.Add($"Webhook: {err}");
        }

        if (!string.IsNullOrWhiteSpace(_options.PushPlusToken))
        {
            var (ok, err) = await TrySendPushPlusAsync(title, text, cancellationToken);
            anyOk |= ok;
            if (!ok) details.Add($"PushPlus: {err}");
        }

        if (string.IsNullOrWhiteSpace(_options.WebhookUrl)
            && string.IsNullOrWhiteSpace(_options.PushPlusToken))
        {
            _logger.LogWarning("No WebhookUrl / PushPlusToken configured. Title: {Title} Message: {Text}", title, text);
        }

        return ($"{title} · {text}", anyOk, string.Join("；", details));
    }

    private async Task<(bool Ok, string Error)> TrySendWebhookAsync(string text, CancellationToken ct)
    {
        try
        {
            object body = _options.WebhookFormat.ToLowerInvariant() switch
            {
                "markdown" => new
                {
                    msgtype = "markdown",
                    markdown = new { content = text }
                },
                "feishu" => new
                {
                    msg_type = "text",
                    content = new { text }
                },
                "dingtalk" => new
                {
                    msgtype = "text",
                    text = new { content = text }
                },
                _ => new
                {
                    msgtype = "text",
                    text = new { content = text },
                    content = text
                }
            };

            using var resp = await _http.PostAsJsonAsync(_options.WebhookUrl, body, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Webhook alert failed {Status}: {Body}", (int)resp.StatusCode, Truncate(respBody));
                return (false, $"HTTP {(int)resp.StatusCode} {Truncate(respBody)}");
            }

            _logger.LogInformation("Webhook alert sent");
            return (true, "");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Webhook alert timeout ({Timeout}s)", _http.Timeout.TotalSeconds);
            return (false, $"超时（{_http.Timeout.TotalSeconds:0}s）");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook alert exception");
            return (false, ex.Message);
        }
    }

    private async Task<(bool Ok, string Error)> TrySendPushPlusAsync(string title, string text, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["token"] = _options.PushPlusToken!,
                ["title"] = string.IsNullOrWhiteSpace(title) ? _options.TitlePrefix : title,
                ["content"] = text,
                ["template"] = "txt"
            };
            if (!string.IsNullOrWhiteSpace(_options.PushPlusTopic))
                payload["topic"] = _options.PushPlusTopic;

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            using var resp = await _http.PostAsync("https://www.pushplus.plus/send", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("PushPlus failed {Status}: {Body}", (int)resp.StatusCode, Truncate(body));
                return (false, $"HTTP {(int)resp.StatusCode} {Truncate(body)}");
            }

            // PushPlus 常返回 HTTP 200，业务成败看 JSON.code
            if (TryParsePushPlusCode(body, out var code, out var msg) && code != 200)
            {
                _logger.LogWarning("PushPlus business failed code={Code} msg={Msg}", code, msg);
                return (false, string.IsNullOrWhiteSpace(msg) ? $"业务码 {code}" : msg);
            }

            _logger.LogInformation("PushPlus alert sent: {Body}", Truncate(body));
            return (true, "");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("PushPlus alert timeout ({Timeout}s)", _http.Timeout.TotalSeconds);
            return (false, $"超时（{_http.Timeout.TotalSeconds:0}s），请增大 Alerts:TimeoutSeconds");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PushPlus alert exception");
            return (false, ex.Message);
        }
    }

    private static bool TryParsePushPlusCode(string? body, out int code, out string? msg)
    {
        code = 0;
        msg = null;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("code", out var c)) return false;
            code = c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : int.TryParse(c.GetString(), out var n) ? n : 0;
            if (doc.RootElement.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
                msg = m.GetString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string? s, int n = 200)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s[..n] + "…";
    }
}
