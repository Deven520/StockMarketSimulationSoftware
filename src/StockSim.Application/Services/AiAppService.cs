using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.Strategies;

namespace StockSim.Application.Services;

public record CursorAiConnectionStatus(bool Ok, string Status, string? Email, string Hint);

public interface ICursorAiClient
{
    Task<string> RunStockPickAsync(string prompt, string? contextJson, CancellationToken ct = default);
    Task<string> RunStrategyUpdateAsync(string prompt, string payloadJson, CancellationToken ct = default);
    /// <summary>投资目标 JSON → 选股提示词 JSON（prompt/rationale）。</summary>
    Task<string> RunGoalToPromptAsync(string goalPayloadJson, CancellationToken ct = default);
    Task<CursorAiConnectionStatus> GetConnectionStatusAsync(CancellationToken ct = default);
}

public class AiAppService
{
    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly StockPoolAppService _pools;
    private readonly StrategyAppService _strategies;

    public AiAppService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        StockPoolAppService pools,
        StrategyAppService strategies)
    {
        _db = db;
        _ai = ai;
        _pools = pools;
        _strategies = strategies;
    }

    public Task<CursorAiConnectionStatus> GetCursorConnectionStatusAsync(CancellationToken ct = default)
        => _ai.GetConnectionStatusAsync(ct);

    public async Task<AiJobDto> CreateStockPickJobAsync(CreateAiStockPickRequest request, CancellationToken ct = default)
    {
        var job = new AiJob
        {
            JobType = AiJobType.StockPick,
            Status = AiJobStatus.Running,
            Prompt = request.Prompt,
            InputPayload = JsonSerializer.Serialize(new { prompt = request.Prompt, requestedAt = DateTime.UtcNow })
        };
        _db.AiJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        try
        {
            var output = await _ai.RunStockPickAsync(request.Prompt, null, ct);
            ApplyAiOutput(job, output, isStockPick: true);
        }
        catch (Exception ex)
        {
            job.Status = AiJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return MapJobDto(job, job.Suggestions.Count);
    }

    public async Task<AiJobDto> CreateStrategyUpdateJobAsync(Guid? strategyId, string prompt, CancellationToken ct = default)
    {
        object? parameters = StrategyParameters.Default().ToJson();
        object? recentPnL = Array.Empty<object>();

        if (strategyId.HasValue)
        {
            var strategy = await _db.Strategies.Include(s => s.Versions).FirstOrDefaultAsync(s => s.Id == strategyId, ct);
            if (strategy is not null)
            {
                var current = strategy.Versions.FirstOrDefault(v => v.Id == strategy.CurrentVersionId)
                              ?? strategy.Versions.MaxBy(v => v.VersionNumber);
                parameters = current?.ParametersJson;
                recentPnL = await _db.DailyPnLs
                    .Where(x => x.StrategyDefinitionId == strategyId)
                    .OrderByDescending(x => x.TradeDate)
                    .Take(30)
                    .Select(x => new { x.TradeDate, x.RealizedPnL, x.Fees, x.TradeCount, x.WinRate })
                    .ToListAsync(ct);
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            strategyId,
            intent = "CreateOrUpdateStrategy",
            parameters,
            recentPnL
        });

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
            var output = await _ai.RunStrategyUpdateAsync(prompt, payload, ct);
            ApplyAiOutput(job, output, isStockPick: false);
        }
        catch (Exception ex)
        {
            job.Status = AiJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return MapJobDto(job, job.Suggestions.Count);
    }

    /// <param name="strategyApplyMode">create=新增策略（默认）；update=更新已有策略</param>
    public async Task<(bool Ok, string Message)> ReviewSuggestionAsync(
        Guid jobId,
        Guid suggestionId,
        bool accept,
        Guid? targetPoolId,
        Guid? targetStrategyId,
        string? strategyApplyMode = "create",
        string? newStrategyName = null,
        Guid? bindPoolId = null,
        CancellationToken ct = default)
    {
        var job = await _db.AiJobs.Include(j => j.Suggestions).FirstOrDefaultAsync(j => j.Id == jobId, ct);
        var suggestion = job?.Suggestions.FirstOrDefault(s => s.Id == suggestionId);
        if (job is null || suggestion is null)
        {
            return (false, "任务或建议不存在");
        }

        if (!accept)
        {
            suggestion.IsAccepted = false;
            suggestion.ReviewedAt = DateTime.UtcNow;
            job.Status = AiJobStatus.Rejected;
            await _db.SaveChangesAsync(ct);
            return (true, "已拒绝建议");
        }

        if (job.JobType == AiJobType.StockPick)
        {
            if (!targetPoolId.HasValue) return (false, "请指定目标股票池");
            var symbol = suggestion.Symbol;
            var name = suggestion.Name ?? symbol;
            if (string.IsNullOrWhiteSpace(symbol))
            {
                using var doc = JsonDocument.Parse(suggestion.PayloadJson);
                symbol = doc.RootElement.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : symbol;
            }

            if (string.IsNullOrWhiteSpace(symbol)) return (false, "建议缺少 symbol");

            var remark = $"AI选股确认 | 提示词: {Truncate(job.Prompt, 80)} | 理由: {Truncate(suggestion.Reason, 120)}";
            await _pools.AddItemAsync(targetPoolId.Value, new AddPoolItemRequest(symbol!, name ?? symbol!, null, remark), PoolItemSource.Ai, ct: ct);
        }
        else if (job.JobType == AiJobType.StrategyUpdate)
        {
            using var doc = JsonDocument.Parse(suggestion.PayloadJson);
            var parametersJson = doc.RootElement.TryGetProperty("parameters", out var p)
                ? p.GetRawText()
                : suggestion.PayloadJson;
            // 若整段就是 parameters 对象（无外层包装），直接使用
            if (!doc.RootElement.TryGetProperty("parameters", out _) &&
                doc.RootElement.TryGetProperty("mode", out _))
            {
                parametersJson = suggestion.PayloadJson;
            }

            var note = doc.RootElement.TryGetProperty("changeNote", out var c)
                ? c.GetString()
                : $"AI策略建议 | {Truncate(job.Prompt, 60)}";
            var mode = string.IsNullOrWhiteSpace(strategyApplyMode) ? "create" : strategyApplyMode.Trim().ToLowerInvariant();

            if (mode == "update")
            {
                if (!targetStrategyId.HasValue) return (false, "请选择要更新的已有策略");
                await _strategies.UpdateParamsAsync(
                    targetStrategyId.Value,
                    new UpdateStrategyParamsRequest(parametersJson, note),
                    ct);
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(newStrategyName)
                    ? $"AI做T策略-{DateTime.Now:MMdd-HHmm}"
                    : newStrategyName.Trim();
                var created = await _strategies.CreateAsync(
                    new CreateStrategyRequest(
                        name,
                        $"由AI根据提示词生成。提示词：{Truncate(job.Prompt, 200)}",
                        parametersJson,
                        note),
                    ct);

                if (bindPoolId.HasValue)
                {
                    await _strategies.BindPoolAsync(created.Id, bindPoolId.Value, ct: ct);
                }

                suggestion.IsAccepted = true;
                suggestion.ReviewedAt = DateTime.UtcNow;
                job.Status = AiJobStatus.Accepted;
                await _db.SaveChangesAsync(ct);
                return (true, $"已新增策略「{created.Name}」(v{created.CurrentVersion})");
            }
        }

        suggestion.IsAccepted = true;
        suggestion.ReviewedAt = DateTime.UtcNow;
        job.Status = AiJobStatus.Accepted;
        await _db.SaveChangesAsync(ct);
        return (true, "已确认并应用");
    }

    public async Task<IReadOnlyList<AiJobDto>> ListJobsAsync(CancellationToken ct = default)
    {
        var jobs = await _db.AiJobs
            .OrderByDescending(j => j.CreationTime)
            .Take(100)
            .Select(j => new
            {
                j.Id,
                j.JobType,
                j.Status,
                j.Prompt,
                j.CreationTime,
                j.ErrorMessage,
                j.AnalysisSummary,
                j.CompletedAt,
                Count = j.Suggestions.Count
            })
            .ToListAsync(ct);

        return jobs.Select(j => new AiJobDto(
            j.Id, j.JobType, j.Status, j.Prompt, j.CreationTime, j.ErrorMessage,
            j.AnalysisSummary, j.Count, j.CompletedAt)).ToList();
    }

    /// <summary>删除 AI 任务及其建议。</summary>
    public async Task<(bool Ok, string Message)> DeleteJobAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _db.AiJobs.Include(j => j.Suggestions).FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return (false, "任务不存在");
        if (job.Status == AiJobStatus.Running)
            return (false, "执行中任务不可删除");

        _db.AiSuggestions.RemoveRange(job.Suggestions);
        _db.AiJobs.Remove(job);
        await _db.SaveChangesAsync(ct);
        return (true, "已删除 AI 任务");
    }

    public async Task<AiJobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.AiJobs.Include(j => j.Suggestions).FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return null;

        var steps = ParseSteps(job.AnalysisStepsJson);
        var suggestions = job.Suggestions
            .OrderBy(s => s.CreationTime)
            .Select(AiSuggestionFactory.ToDto)
            .ToList();

        return new AiJobDetailDto(
            job.Id,
            job.JobType,
            job.Status,
            job.Prompt,
            job.AnalysisSummary,
            steps,
            job.InputPayload,
            job.OutputPayload,
            job.ErrorMessage,
            job.CreationTime,
            job.CompletedAt,
            suggestions);
    }

    public async Task<IReadOnlyList<AiSuggestion>> GetSuggestionsAsync(Guid jobId, CancellationToken ct = default)
    {
        return await _db.AiSuggestions.Where(s => s.AiJobId == jobId).ToListAsync(ct);
    }

    private void ApplyAiOutput(AiJob job, string output, bool isStockPick)
    {
        job.OutputPayload = output;
        job.Status = AiJobStatus.SucceededPendingReview;
        job.CompletedAt = DateTime.UtcNow;

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "{}" : output);
        var root = doc.RootElement;

        job.AnalysisSummary = ExtractAnalysis(root, job.Prompt);
        job.AnalysisStepsJson = ExtractStepsJson(root);

        if (isStockPick && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                _db.AiSuggestions.Add(AiSuggestionFactory.FromItem(job.Id, item));
            }
        }
        else
        {
            _db.AiSuggestions.Add(AiSuggestionFactory.FromItem(job.Id, root));
        }
    }

    private static string ExtractAnalysis(JsonElement root, string prompt)
    {
        var analysis = GetString(root, "analysis")
                       ?? GetString(root, "summary")
                       ?? GetString(root, "note");
        if (!string.IsNullOrWhiteSpace(analysis))
        {
            return analysis!;
        }

        return $"针对提示词「{prompt}」已生成建议，详见候选列表。原始输出未提供 analysis 字段。";
    }

    private static string? ExtractStepsJson(JsonElement root)
    {
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            return steps.GetRawText();
        }

        return null;
    }

    private static IReadOnlyList<string> ParseSteps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    private static AiJobDto MapJobDto(AiJob job, int count) =>
        new(job.Id, job.JobType, job.Status, job.Prompt, job.CreationTime, job.ErrorMessage,
            job.AnalysisSummary, count, job.CompletedAt);

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";
}
