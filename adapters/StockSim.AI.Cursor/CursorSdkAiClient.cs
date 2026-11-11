using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockSim.Application.Services;

namespace StockSim.AI.Cursor;

/// <summary>
/// 通过 Node 桥接调用 Cursor Agent SDK（本地 Agent.prompt）。
/// 认证：CURSOR_API_KEY / AI:Cursor:ApiKey / 本机 Cursor.auth.login 态。不使用账号密码。
/// </summary>
public sealed class CursorSdkAiClient : ICursorAiClient
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CursorSdkAiClient>? _logger;
    private readonly string _workDir;
    private readonly string _repoRoot;
    private readonly FileCursorAiClient _placeholder;

    public CursorSdkAiClient(IConfiguration configuration, ILogger<CursorSdkAiClient>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
        _repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        _workDir = configuration.GetValue<string>("AI:WorkDirectory")
                   ?? Path.Combine(_repoRoot, "data", "ai");
        Directory.CreateDirectory(_workDir);
        _placeholder = new FileCursorAiClient(configuration);
    }

    public Task<string> RunStockPickAsync(string prompt, string? contextJson, CancellationToken ct = default)
        => RunAsync("StockPick", prompt, contextJson, ct);

    public Task<string> RunStrategyUpdateAsync(string prompt, string payloadJson, CancellationToken ct = default)
        => RunAsync("StrategyUpdate", prompt, payloadJson, ct);

    public Task<string> RunGoalToPromptAsync(string goalPayloadJson, CancellationToken ct = default)
    {
        var goalSummary = SummarizeGoalPayload(goalPayloadJson);
        var instruction =
            "请根据投资目标 JSON 生成「下一步选股」用的中文提示词。" +
            "严格只输出 JSON：{prompt,rationale,analysis}，不要其它文字。" +
            "本步 JSON 不要直接列股票；但 prompt 字段必须要求下一步输出 3~5 只具体 A 股（含代码/名称/理由/风险），" +
            "且 prompt 中禁止写「不要输出股票代码」。" +
            "prompt 必须显式包含：预期收益%、预期周期（天）、题材偏好、选股类型（做T股/趋势股/次日上涨预选）。" +
            "若 tradeStyle=NextDayRise/次日上涨预选：结合 marketContext 中的热点新闻、下一交易日日期、题材与当日/历史强弱，" +
            "预选下一交易日更可能上涨的个股；horizon 视为 1 个交易日；不要按日内做T选股。" +
            "若 tradeStyle=TrendSwing/趋势股：强调底部反转或中期趋势观察，不要按日内做T选股；" +
            "若做T股：强调流动性与适中波动、适合高抛低吸。" +
            goalSummary;
        return RunAsync("GoalToPrompt", instruction, goalPayloadJson, ct);
    }

    private static string SummarizeGoalPayload(string? goalPayloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(goalPayloadJson) ? "{}" : goalPayloadJson);
            var root = doc.RootElement;
            var expected = root.TryGetProperty("expectedReturnPct", out var e) ? e.ToString() : "?";
            var days = root.TryGetProperty("horizonDays", out var d) ? d.ToString() : "?";
            var theme = root.TryGetProperty("themePreference", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "?"
                : "?";
            var style = root.TryGetProperty("tradeStyleLabel", out var sl) && sl.ValueKind == JsonValueKind.String
                ? sl.GetString()
                : root.TryGetProperty("tradeStyle", out var st) ? st.ToString() : "做T股";
            return $" 目标摘要：类型「{style}」，预期收益 {expected}% ，周期 {days} 天，题材「{theme}」。";
        }
        catch
        {
            return "";
        }
    }

    public async Task<CursorAiConnectionStatus> GetConnectionStatusAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_configuration.GetValue("AI:Cursor:Enabled", true))
            {
                return new CursorAiConnectionStatus(false, "disabled", null,
                    "AI:Cursor:Enabled=false。请改为 true 并重启 Web。");
            }

            var key = ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                return new CursorAiConnectionStatus(true, "api-key", null,
                    "已配置 API Key（环境变量 / appsettings / secrets 文件）。");
            }

            var bridgeDir = ResolveBridgeDir();
            var nodeExe = ResolveNodeExecutable();
            _logger?.LogInformation(
                "AI status probe: repo={Repo} node={Node} bridge={Bridge}",
                _repoRoot, nodeExe, bridgeDir);

            var json = await InvokeNodeScriptAsync("status.mjs", stdin: null, timeoutSec: 30, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "unknown" : "unknown";
            var email = root.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String
                ? em.GetString()
                : null;
            var hint = root.TryGetProperty("hint", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() ?? ""
                : "";
            return new CursorAiConnectionStatus(ok, status, email, hint);
        }
        catch (Exception ex)
        {
            var detail = FlattenException(ex);
            _logger?.LogWarning(ex, "Cursor Agent 状态检查失败: {Detail}", detail);
            return new CursorAiConnectionStatus(false, "error", null,
                "状态检查失败: " + detail +
                "。请确认已运行 scripts\\setup-cursor-agent.ps1 与 login-cursor-agent.bat，并重启 Web。");
        }
    }

    private async Task<string> RunAsync(string type, string prompt, string? payloadJson, CancellationToken ct)
    {
        var enabled = _configuration.GetValue("AI:Cursor:Enabled", true);
        // 默认不回退：避免“看起来像成功但仍是占位票”
        var fallback = _configuration.GetValue("AI:Cursor:FallbackToPlaceholder", false);

        if (!enabled)
        {
            _logger?.LogInformation("AI:Cursor:Enabled=false，使用占位选股");
            return await InvokePlaceholderAsync(type, prompt, payloadJson, ct);
        }

        try
        {
            var json = await InvokeBridgeAsync(type, prompt, payloadJson, ct);
            PersistExchange(type, prompt, payloadJson, json);
            return json;
        }
        catch (Exception ex) when (fallback)
        {
            _logger?.LogWarning(ex, "Cursor Agent 调用失败，回退占位建议");
            var placeholder = await InvokePlaceholderAsync(type, prompt, payloadJson, ct);
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(placeholder) ?? new();
                var note = $"Cursor Agent 调用失败已回退占位：{ex.Message}";
                using var stream = new MemoryStream();
                await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var kv in dict)
                    {
                        if (kv.Key is "note" or "_meta") continue;
                        writer.WritePropertyName(kv.Key);
                        kv.Value.WriteTo(writer);
                    }
                    writer.WriteString("note", note);
                    writer.WritePropertyName("_meta");
                    writer.WriteStartObject();
                    writer.WriteString("provider", "PlaceholderFallback");
                    writer.WriteString("error", ex.Message);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch
            {
                return placeholder;
            }
        }
    }

    private Task<string> InvokePlaceholderAsync(string type, string prompt, string? payloadJson, CancellationToken ct)
        => type switch
        {
            "StrategyUpdate" => _placeholder.RunStrategyUpdateAsync(prompt, payloadJson ?? "{}", ct),
            "GoalToPrompt" => _placeholder.RunGoalToPromptAsync(payloadJson ?? "{}", ct),
            _ => _placeholder.RunStockPickAsync(prompt, payloadJson, ct)
        };

    private async Task<string> InvokeBridgeAsync(string type, string prompt, string? payloadJson, CancellationToken ct)
    {
        EnsureBridgeInstalled();

        var model = _configuration.GetValue<string>("AI:Cursor:Model") ?? "composer-2.5";
        var timeoutSec = _configuration.GetValue("AI:Cursor:TimeoutSeconds", 180);
        var apiKey = ResolveApiKey();

        var task = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["prompt"] = prompt,
            ["payloadJson"] = payloadJson,
            ["model"] = model,
            ["cwd"] = _repoRoot
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            task["apiKey"] = apiKey;
        }

        return await InvokeNodeScriptAsync(
            "run.mjs",
            JsonSerializer.Serialize(task),
            timeoutSec,
            ct,
            extraEnv: new Dictionary<string, string> { ["STOCKSIM_CURSOR_MODEL"] = model });
    }

    private void EnsureBridgeInstalled()
    {
        var bridgeDir = ResolveBridgeDir();
        var runJs = Path.Combine(bridgeDir, "run.mjs");
        if (!File.Exists(runJs))
        {
            throw new InvalidOperationException($"未找到桥接脚本: {runJs}。请先运行 scripts\\setup-cursor-agent.ps1");
        }

        var nodeModules = Path.Combine(bridgeDir, "node_modules", "@cursor", "sdk");
        if (!Directory.Exists(nodeModules))
        {
            throw new InvalidOperationException("未安装 @cursor/sdk。请先运行 scripts\\setup-cursor-agent.ps1");
        }
    }

    private async Task<string> InvokeNodeScriptAsync(
        string scriptName,
        string? stdin,
        int timeoutSec,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        EnsureBridgeInstalled();
        var bridgeDir = ResolveBridgeDir();
        var scriptPath = Path.Combine(bridgeDir, scriptName);
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"未找到脚本: {scriptPath}");
        }

        var nodeExe = ResolveNodeExecutable();
        var apiKey = ResolveApiKey();

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = $"\"{scriptPath}\"",
            WorkingDirectory = bridgeDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 确保 SDK 能读到本机登录态（~/.cursor/sdk），避免搬迁/服务启动后环境缺省
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            psi.Environment["USERPROFILE"] = userProfile;
            if (!psi.Environment.ContainsKey("HOME") || string.IsNullOrWhiteSpace(psi.Environment["HOME"]))
                psi.Environment["HOME"] = userProfile;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            psi.Environment["CURSOR_API_KEY"] = apiKey;
        }

        if (extraEnv is not null)
        {
            foreach (var kv in extraEnv)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            throw new InvalidOperationException("无法启动 Node 桥接进程");
        }

        if (!string.IsNullOrEmpty(stdin))
        {
            await proc.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        }

        await proc.StandardInput.FlushAsync(ct);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, timeoutSec)));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"Cursor Agent 超时（>{timeoutSec}s）");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            var detail = ExtractBridgeError(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            throw new InvalidOperationException(detail);
        }

        // SDK 日志可能混进 stdout；status 若误用 console.log 会落到 stderr —— 两边都尝试抽 JSON
        var json = ExtractJsonPayload(stdout) ?? ExtractJsonPayload(stderr);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                "Cursor Agent 未返回 JSON。stdout=" + TrimForError(stdout) + " stderr=" + TrimForError(stderr));
        }

        using var _ = JsonDocument.Parse(json);
        return json;
    }

    /// <summary>
    /// 从可能混有日志行的输出中提取 JSON 对象（取第一个 '{' 到最后一个 '}'）。
    /// </summary>
    private static string? ExtractJsonPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var _ = JsonDocument.Parse(trimmed);
                return trimmed;
            }
            catch (JsonException)
            {
                /* fall through to slice */
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var slice = trimmed[start..(end + 1)];
        try
        {
            using var _ = JsonDocument.Parse(slice);
            return slice;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? ResolveApiKey()
    {
        var fromConfig = _configuration.GetValue<string>("AI:Cursor:ApiKey");
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.Trim();

        var fromEnv = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        // 本地未跟踪密钥文件（勿提交 Git）
        foreach (var rel in new[]
                 {
                     Path.Combine("secrets", "cursor-api-key.txt"),
                     Path.Combine("data", "ai", ".cursor-api-key")
                 })
        {
            var path = Path.Combine(_repoRoot, rel);
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch
            {
                /* ignore */
            }
        }

        return null;
    }

    private static string ExtractBridgeError(string? raw)
    {
        raw = TrimForError(raw);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var hint = root.TryGetProperty("hint", out var h) ? h.GetString() : null;
            if (!string.IsNullOrWhiteSpace(error))
            {
                return string.IsNullOrWhiteSpace(hint) ? error! : $"{error} | {hint}";
            }
        }
        catch
        {
            /* not json */
        }

        return "Cursor Agent 桥接失败: " + raw;
    }

    private void PersistExchange(string type, string prompt, string? payloadJson, string responseJson)
    {
        try
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var prefix = type == "StrategyUpdate" ? "strategy" : "stockpick";
            var request = new { type, prompt, payloadJson, createdAt = DateTime.UtcNow, provider = "CursorSdk" };
            File.WriteAllText(
                Path.Combine(_workDir, $"{prefix}_request_{id}.json"),
                JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(_workDir, $"{prefix}_response_{id}.json"), responseJson);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "写入 data/ai 交换文件失败（不影响主流程）");
        }
    }

    private string ResolveBridgeDir()
    {
        var configured = _configuration.GetValue<string>("AI:Cursor:BridgeDirectory");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(_repoRoot, configured));
            // 文件夹搬迁后旧绝对路径会失效，回退到仓库相对路径
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "status.mjs")))
                return candidate;

            _logger?.LogWarning("AI:Cursor:BridgeDirectory 无效，改用仓库默认路径: {Path}", candidate);
        }

        var fallback = Path.Combine(_repoRoot, "adapters", "cursor-agent-bridge");
        if (!Directory.Exists(fallback))
        {
            throw new InvalidOperationException(
                $"未找到桥接目录: {fallback}（repoRoot={_repoRoot}）。请确认仓库完整。");
        }

        return fallback;
    }

    private string ResolveNodeExecutable()
    {
        var configured = _configuration.GetValue<string>("AI:Cursor:NodeExecutable");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return configured;

            _logger?.LogWarning("AI:Cursor:NodeExecutable 不存在，尝试自动发现: {Path}", configured);
        }

        var portable = Path.Combine(_repoRoot, "tools", "node", "node.exe");
        if (File.Exists(portable)) return portable;

        var onPath = FindOnPath("node.exe") ?? FindOnPath("node");
        if (onPath is not null) return onPath;

        var cursorHelpers = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "cursor", "resources", "app", "resources", "helpers", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "cursor", "_", "resources", "app", "resources", "helpers", "node.exe"),
        };
        foreach (var p in cursorHelpers)
        {
            if (File.Exists(p)) return p;
        }

        throw new InvalidOperationException(
            $"未找到 Node.js（repoRoot={_repoRoot}）。请运行 scripts\\setup-cursor-agent.ps1。");
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var msg = (cur.Message ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(msg))
                msg = cur.GetType().Name;
            if (parts.Count == 0 || !parts[^1].Contains(msg, StringComparison.Ordinal))
                parts.Add(msg);
        }

        return parts.Count == 0 ? ex.GetType().Name : string.Join(" | ", parts);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                /* ignore bad PATH entries */
            }
        }

        return null;
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var hasSlnx = File.Exists(Path.Combine(dir.FullName, "StockSim.slnx"));
            var hasBridge = Directory.Exists(Path.Combine(dir.FullName, "adapters", "cursor-agent-bridge"));
            var hasDocs = Directory.Exists(Path.Combine(dir.FullName, "docs"));
            if (hasSlnx || hasBridge || hasDocs)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string TrimForError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(无输出)";
        text = text.Trim();
        return text.Length <= 800 ? text : text[..800] + "...";
    }
}
