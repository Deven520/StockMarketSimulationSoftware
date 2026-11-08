using Microsoft.EntityFrameworkCore;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Strategies;

namespace StockSim.Application.Services;

public class StrategyAppService
{
    private readonly IStockSimDataStore _db;

    public StrategyAppService(IStockSimDataStore db) => _db = db;

    public async Task<IReadOnlyList<StrategyDto>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Strategies.Include(s => s.Versions).ToListAsync(ct);
        return list.Select(Map).ToList();
    }

    public async Task<StrategyDto> CreateAsync(CreateStrategyRequest request, CancellationToken ct = default)
    {
        var parameters = string.IsNullOrWhiteSpace(request.ParametersJson)
            ? StrategyParameters.Default().ToJson()
            : request.ParametersJson!;

        var strategy = new StrategyDefinition
        {
            Name = request.Name,
            Description = request.Description,
            IsEnabled = true
        };
        var version = new StrategyVersion
        {
            StrategyDefinition = strategy,
            VersionNumber = 1,
            ParametersJson = parameters,
            ChangeNote = string.IsNullOrWhiteSpace(request.ChangeNote) ? "初始版本" : request.ChangeNote!,
            CreatedBy = string.IsNullOrWhiteSpace(request.ChangeNote) ? "system" : "AI"
        };
        strategy.Versions.Add(version);
        _db.Strategies.Add(strategy);
        await _db.SaveChangesAsync(ct);

        strategy.CurrentVersionId = version.Id;
        await _db.SaveChangesAsync(ct);
        return Map(strategy);
    }

    public async Task<StrategyDto?> UpdateParamsAsync(Guid strategyId, UpdateStrategyParamsRequest request, CancellationToken ct = default)
    {
        var strategy = await _db.Strategies.Include(s => s.Versions).FirstOrDefaultAsync(s => s.Id == strategyId, ct);
        if (strategy is null) return null;

        var next = (strategy.Versions.MaxBy(v => v.VersionNumber)?.VersionNumber ?? 0) + 1;
        var version = new StrategyVersion
        {
            StrategyDefinitionId = strategy.Id,
            VersionNumber = next,
            ParametersJson = request.ParametersJson,
            ChangeNote = request.ChangeNote ?? "参数更新",
            CreatedBy = "user"
        };
        _db.StrategyVersions.Add(version);
        await _db.SaveChangesAsync(ct);
        strategy.CurrentVersionId = version.Id;
        await _db.SaveChangesAsync(ct);
        strategy.Versions.Add(version);
        return Map(strategy);
    }

    public async Task BindPoolAsync(Guid strategyId, Guid poolId, int priority = 1, CancellationToken ct = default)
    {
        var exists = await _db.StrategyBindings.AnyAsync(
            b => b.StrategyDefinitionId == strategyId && b.StockPoolId == poolId, ct);
        if (exists) return;

        _db.StrategyBindings.Add(new StrategyBinding
        {
            StrategyDefinitionId = strategyId,
            StockPoolId = poolId,
            Priority = priority,
            IsEnabled = true
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(Guid strategyId, bool enabled, CancellationToken ct = default)
    {
        var strategy = await _db.Strategies.FindAsync([strategyId], ct);
        if (strategy is null) return;
        strategy.IsEnabled = enabled;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RollbackAsync(Guid strategyId, Guid versionId, CancellationToken ct = default)
    {
        var strategy = await _db.Strategies.Include(s => s.Versions).FirstOrDefaultAsync(s => s.Id == strategyId, ct);
        var version = strategy?.Versions.FirstOrDefault(v => v.Id == versionId);
        if (strategy is null || version is null) return;

        var clone = new StrategyVersion
        {
            StrategyDefinitionId = strategy.Id,
            VersionNumber = strategy.Versions.Max(v => v.VersionNumber) + 1,
            ParametersJson = version.ParametersJson,
            ChangeNote = $"回滚自 v{version.VersionNumber}",
            CreatedBy = "user"
        };
        _db.StrategyVersions.Add(clone);
        await _db.SaveChangesAsync(ct);
        strategy.CurrentVersionId = clone.Id;
        await _db.SaveChangesAsync(ct);
    }

    private static StrategyDto Map(StrategyDefinition s)
    {
        var current = s.Versions.FirstOrDefault(v => v.Id == s.CurrentVersionId)
                      ?? s.Versions.MaxBy(v => v.VersionNumber);
        return new StrategyDto(
            s.Id,
            s.Name,
            s.Description,
            s.IsEnabled,
            current?.VersionNumber ?? 0,
            current?.ParametersJson ?? "{}");
    }
}
