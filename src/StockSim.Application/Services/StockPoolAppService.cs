using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;
using StockSim.Domain.Trading;

namespace StockSim.Application.Services;

public class StockPoolAppService
{
    public const decimal DefaultPerStockCash = 200_000m;

    private readonly IStockSimDataStore _db;
    private readonly PerStockStrategyService _perStockStrategy;
    private readonly IStockSuggestProvider _suggest;
    private readonly IStockDetailProvider _detailProvider;
    private readonly ILogger<StockPoolAppService> _logger;

    public StockPoolAppService(
        IStockSimDataStore db,
        PerStockStrategyService perStockStrategy,
        IStockSuggestProvider suggest,
        IStockDetailProvider detailProvider,
        ILogger<StockPoolAppService> logger)
    {
        _db = db;
        _perStockStrategy = perStockStrategy;
        _suggest = suggest;
        _detailProvider = detailProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<StockPoolDto>> GetPoolsAsync(CancellationToken ct = default)
    {
        return await _db.StockPools
            .Select(p => new StockPoolDto(p.Id, p.Name, p.Description, p.IsActive, p.Items.Count))
            .ToListAsync(ct);
    }

    public async Task<StockPoolDto> CreateAsync(CreateStockPoolRequest request, CancellationToken ct = default)
    {
        var entity = new StockPool { Name = request.Name, Description = request.Description };
        _db.StockPools.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new StockPoolDto(entity.Id, entity.Name, entity.Description, entity.IsActive, 0);
    }

    /// <summary>站点仅使用单一股票池：取第一个启用池，没有则自动创建。</summary>
    public async Task<StockPoolDto> GetOrCreateDefaultPoolAsync(CancellationToken ct = default)
    {
        var pool = await _db.StockPools
            .Where(p => p.IsActive)
            .OrderBy(p => p.CreationTime)
            .Select(p => new StockPoolDto(p.Id, p.Name, p.Description, p.IsActive, p.Items.Count))
            .FirstOrDefaultAsync(ct);

        if (pool is not null) return pool;

        return await CreateAsync(new CreateStockPoolRequest("默认做T池", "一股一策股票池"), ct);
    }

    public async Task<IReadOnlyList<StockPoolItemDto>> GetItemsAsync(Guid poolId, CancellationToken ct = default)
    {
        return await _db.StockPoolItems
            .Where(i => i.StockPoolId == poolId)
            .Select(i => new StockPoolItemDto(
                i.Id, i.StockPoolId, i.Symbol, i.Name, i.Tags, i.Source,
                i.BoundStrategyId, i.StrategyUpdatedAt, i.InitialCash, i.AvailableCash, i.NotifyOnTrade, i.TradeStyle,
                i.PredictedRiseDate, i.RiseReturnPct, i.RiseSuccess, i.RiseEvaluatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 股票池次日预涨：预涨日收盘后（≥15:00）立即用真源日K结算，写入成功/失败。
    /// </summary>
    public async Task<int> SettleNextDayRiseAsync(DateTime localNow, CancellationToken ct = default)
    {
        var candidates = await _db.StockPoolItems
            .Where(i => i.TradeStyle == StockTradeStyle.NextDayRise
                        && i.PredictedRiseDate != null)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var settled = 0;
        foreach (var item in candidates)
        {
            var target = item.PredictedRiseDate!.Value.Date;
            if (!PriceForecastMetrics.CanFinalizeForecast(target, localNow))
                continue;

            // 已按同一预涨日结算过则跳过（改预涨日后可重算）
            if (item.RiseEvaluatedAt.HasValue
                && item.RiseSuccess.HasValue
                && item.RiseReturnPct.HasValue
                && item.RiseActualClose.HasValue)
            {
                continue;
            }

            StockDetailBundle? detail;
            try
            {
                detail = await _detailProvider.GetDetailAsync(item.Symbol, item.Name, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NextDayRise settle detail failed for {Symbol}", item.Symbol);
                continue;
            }

            if (detail is null || detail.IsSimulated)
            {
                _logger.LogInformation("Skip NextDayRise settle {Symbol}: no real daily bars", item.Symbol);
                continue;
            }

            var bars = detail.DailyBars
                .Where(d => d.Date.Date <= target && d.Close > 0)
                .OrderBy(d => d.Date)
                .ToList();
            var dayBar = bars.LastOrDefault(d => d.Date.Date == target);
            if (dayBar is null)
            {
                _logger.LogInformation("Skip NextDayRise settle {Symbol}: missing bar for {Date}", item.Symbol, target);
                continue;
            }

            var baseline = bars.LastOrDefault(d => d.Date.Date < target)?.Close ?? 0m;
            if (baseline <= 0)
            {
                _logger.LogInformation("Skip NextDayRise settle {Symbol}: no baseline before {Date}", item.Symbol, target);
                continue;
            }

            var (ret, dirHit, _) = NextDayPickMetrics.Evaluate(baseline, dayBar.Close, expectedReturnPct: 0m);
            item.RiseBaselinePrice = baseline;
            item.RiseActualClose = dayBar.Close;
            item.RiseReturnPct = ret;
            item.RiseSuccess = dirHit;
            item.RiseEvaluatedAt = localNow;
            item.LastModificationTime = DateTime.UtcNow;
            settled++;
        }

        if (settled > 0)
            await _db.SaveChangesAsync(ct);

        return settled;
    }

    /// <summary>开关：策略成交是否推送通知。</summary>
    public async Task<(bool Ok, string Message)> SetNotifyOnTradeAsync(
        Guid itemId,
        bool enabled,
        CancellationToken ct = default)
    {
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return (false, "成分不存在");
        item.NotifyOnTrade = enabled;
        item.LastModificationTime = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // 回读确认，避免列未迁移时“假成功”
        var saved = await _db.StockPoolItems.AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => i.NotifyOnTrade)
            .FirstAsync(ct);
        if (saved != enabled)
            return (false, $"保存失败：库中 NotifyOnTrade 仍为 {saved}，请重启 Web 以补齐数据库列后重试");

        return (true, enabled
            ? $"已开启 {item.Symbol} {item.Name} 成交通知"
            : $"已关闭 {item.Symbol} {item.Name} 成交通知");
    }

    /// <summary>
    /// 添加成分：代码/名称填其一即可；先默认策略入库，再入队异步 AI 刷新。
    /// </summary>
    public async Task<(StockPoolItemDto? Item, string Message)> AddItemSmartAsync(
        Guid poolId,
        string? symbol,
        string? name,
        string? tags,
        PoolItemSource source = PoolItemSource.Manual,
        StockTradeStyle tradeStyle = StockTradeStyle.IntradayT,
        CancellationToken ct = default)
    {
        var query = !string.IsNullOrWhiteSpace(symbol) ? symbol : name;
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, "请填写股票代码或名称");
        }

        var resolved = await _suggest.ResolveAsync(query.Trim(), ct);
        if (resolved is null)
        {
            // 若用户同时给了代码和名称，直接用
            if (!string.IsNullOrWhiteSpace(symbol) && !string.IsNullOrWhiteSpace(name))
            {
                var digits = new string(symbol.Where(char.IsDigit).ToArray());
                if (digits.Length == 6)
                {
                    resolved = new StockSuggestItem { Symbol = digits, Name = name.Trim() };
                }
            }
        }

        // 一侧有值另一侧空：用解析结果补全
        if (resolved is null && !string.IsNullOrWhiteSpace(symbol))
        {
            var digits = new string(symbol.Where(char.IsDigit).ToArray());
            if (digits.Length == 6)
            {
                resolved = new StockSuggestItem
                {
                    Symbol = digits,
                    Name = string.IsNullOrWhiteSpace(name) ? digits : name.Trim()
                };
            }
        }

        if (resolved is null)
        {
            return (null, $"无法识别股票「{query}」，请从联想列表中选择或输入 6 位代码");
        }

        var finalSymbol = resolved.Symbol;
        var finalName = !string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), query.Trim(), StringComparison.Ordinal)
            ? name.Trim()
            : resolved.Name;

        // 若用户填了代码且解析名称不同，仍以解析名为准（更准）
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var digits = new string(symbol.Where(char.IsDigit).ToArray());
            if (digits.Length == 6) finalSymbol = digits;
        }

        if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 2
            && (string.IsNullOrWhiteSpace(symbol) || new string(symbol.Where(char.IsDigit).ToArray()).Length != 6))
        {
            finalName = resolved.Name;
            finalSymbol = resolved.Symbol;
        }

        // 趋势股也需 AI/ATR 高低预测以支撑「预测低位建仓」，但不走做T引擎
        var item = await AddItemAsync(
            poolId,
            new AddPoolItemRequest(finalSymbol, finalName, tags, null),
            source,
            autoGenerateStrategy: true,
            tradeStyle: tradeStyle,
            ct: ct);

        if (item is null) return (null, "添加失败");
        if (tradeStyle == StockTradeStyle.NextDayRise)
        {
            var day = item.PredictedRiseDate?.ToString("yyyy-MM-dd") ?? "下一交易日";
            return (item,
                $"已添加次日预涨 {finalName}({finalSymbol})，预涨日 {day}；按趋势逻辑低位建仓（上限{StrategyEngineService.TrendMaxPositionCash:N0}），不做自动做T");
        }

        return StockTradeStyleHelper.IsTrendLike(tradeStyle)
            ? (item, $"已添加趋势股 {finalName}({finalSymbol})，下一交易日起按预测低位建仓（上限{StrategyEngineService.TrendMaxPositionCash:N0}），不做自动做T")
            : (item, $"已添加 {finalName}({finalSymbol})，默认策略已就绪，AI 正在后台优化");
    }

    public async Task<StockPoolItemDto?> AddItemAsync(
        Guid poolId,
        AddPoolItemRequest request,
        PoolItemSource source = PoolItemSource.Manual,
        bool autoGenerateStrategy = true,
        StockTradeStyle tradeStyle = StockTradeStyle.IntradayT,
        CancellationToken ct = default)
    {
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var exists = await _db.StockPoolItems.FirstOrDefaultAsync(
            i => i.StockPoolId == poolId && i.Symbol == symbol, ct);
        var riseDate = tradeStyle == StockTradeStyle.NextDayRise
            ? PriceForecastMetrics.NextTradingDay(DateTime.Today)
            : (DateTime?)null;

        if (exists is not null)
        {
            var changed = false;
            if (exists.TradeStyle != tradeStyle)
            {
                exists.TradeStyle = tradeStyle;
                changed = true;
            }

            if (tradeStyle == StockTradeStyle.NextDayRise)
            {
                if (exists.PredictedRiseDate != riseDate)
                {
                    exists.PredictedRiseDate = riseDate;
                    ClearRiseSettlement(exists);
                    changed = true;
                }
            }
            else if (exists.PredictedRiseDate.HasValue || exists.RiseSuccess.HasValue)
            {
                exists.PredictedRiseDate = null;
                ClearRiseSettlement(exists);
                changed = true;
            }

            if (changed) await _db.SaveChangesAsync(ct);

            if (autoGenerateStrategy && !exists.BoundStrategyId.HasValue)
            {
                await _perStockStrategy.EnsureDefaultStrategyAsync(exists.Id, ct);
                await EnqueueStrategyRefreshAsync(exists, nightly: false, ct);
                exists = await _db.StockPoolItems.FirstAsync(i => i.Id == exists.Id, ct);
            }

            return MapItem(exists);
        }

        var account = await _db.Accounts.FirstOrDefaultAsync(ct);
        if (account is not null)
        {
            account.Cash += DefaultPerStockCash;
            account.InitialCash += DefaultPerStockCash;
        }

        var item = new StockPoolItem
        {
            StockPoolId = poolId,
            Symbol = symbol,
            Name = request.Name,
            Tags = request.Tags,
            Remark = request.Remark,
            Source = source,
            TradeStyle = tradeStyle,
            PredictedRiseDate = riseDate,
            InitialCash = DefaultPerStockCash,
            AvailableCash = DefaultPerStockCash,
            // 入池当日可按行情建底仓；下一交易日起才自动做 T（趋势股仍保留字段，但引擎会跳过做T）
            AllowTFromDate = AShareMarketSession.NextTradingDay(DateTime.Today)
        };
        _db.StockPoolItems.Add(item);

        // 初始化空持仓占位（底仓 0，可卖 0），便于展示
        if (account is not null)
        {
            var hasPos = await _db.Positions.AnyAsync(p => p.SimAccountId == account.Id && p.Symbol == symbol, ct);
            if (!hasPos)
            {
                _db.Positions.Add(new Position
                {
                    SimAccountId = account.Id,
                    Symbol = symbol,
                    Name = request.Name,
                    BaseQuantity = 0,
                    Quantity = 0,
                    SellableQuantity = 0,
                    AvgCost = 0
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        if (autoGenerateStrategy)
        {
            await _perStockStrategy.EnsureDefaultStrategyAsync(item.Id, ct);
            try
            {
                await EnqueueStrategyRefreshAsync(item, nightly: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enqueue AI refresh failed for {ItemId}", item.Id);
            }

            item = await _db.StockPoolItems.FirstAsync(i => i.Id == item.Id, ct);
        }

        return MapItem(item);
    }

    private async Task EnqueueStrategyRefreshAsync(StockPoolItem item, bool nightly, CancellationToken ct)
    {
        _db.AiQueueItems.Add(new AiQueueItem
        {
            TaskType = AiQueueTaskType.StrategyRefresh,
            Status = AiQueueItemStatus.Pending,
            Title = nightly
                ? $"夜更策略 · {item.Name}({item.Symbol})"
                : $"策略刷新 · {item.Name}({item.Symbol})",
            CorrelationId = Guid.NewGuid(),
            RelatedPoolItemId = item.Id,
            Symbol = item.Symbol,
            InputPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                poolItemId = item.Id,
                symbol = item.Symbol,
                name = item.Name,
                nightly
            })
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await _db.StockPoolItems.FindAsync([itemId], ct);
        if (item is null) return;

        if (item.BoundStrategyId.HasValue)
        {
            var bindings = await _db.StrategyBindings
                .Where(b => b.StrategyDefinitionId == item.BoundStrategyId)
                .ToListAsync(ct);
            foreach (var b in bindings)
            {
                b.IsEnabled = false;
            }

            var strategy = await _db.Strategies.FindAsync([item.BoundStrategyId.Value], ct);
            if (strategy is not null) strategy.IsEnabled = false;
        }

        // 清空该股持仓（池视角）
        var positions = await _db.Positions.Where(p => p.Symbol == item.Symbol).ToListAsync(ct);
        foreach (var p in positions)
        {
            p.Quantity = 0;
            p.BaseQuantity = 0;
            p.SellableQuantity = 0;
            p.AvgCost = 0;
        }

        var account = await _db.Accounts.FirstOrDefaultAsync(ct);
        if (account is not null)
        {
            account.Cash = Math.Max(0, account.Cash - item.AvailableCash);
        }

        _db.StockPoolItems.Remove(item);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(int Ok, int Fail, List<string> Errors)> ImportCsvAsync(Guid poolId, string csv, CancellationToken ct = default)
    {
        var ok = 0;
        var fail = 0;
        var errors = new List<string>();
        var lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var start = lines.Length > 0 && lines[0].Contains("Symbol", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        for (var i = start; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
            {
                fail++;
                errors.Add($"第{i + 1}行：缺少代码或名称");
                continue;
            }

            try
            {
                var symbol = parts[0].Trim();
                var name = parts.Length > 1 ? parts[1].Trim() : null;
                var (item, msg) = await AddItemSmartAsync(poolId, symbol, name, parts.Length > 2 ? parts[2].Trim() : null, ct: ct);
                if (item is null)
                {
                    fail++;
                    errors.Add($"第{i + 1}行：{msg}");
                }
                else ok++;
            }
            catch (Exception ex)
            {
                fail++;
                errors.Add($"第{i + 1}行：{ex.Message}");
            }
        }

        return (ok, fail, errors);
    }

    private static void ClearRiseSettlement(StockPoolItem item)
    {
        item.RiseBaselinePrice = null;
        item.RiseActualClose = null;
        item.RiseReturnPct = null;
        item.RiseSuccess = null;
        item.RiseEvaluatedAt = null;
    }

    private static StockPoolItemDto MapItem(StockPoolItem i) =>
        new(i.Id, i.StockPoolId, i.Symbol, i.Name, i.Tags, i.Source, i.BoundStrategyId, i.StrategyUpdatedAt,
            i.InitialCash, i.AvailableCash, i.NotifyOnTrade, i.TradeStyle, i.PredictedRiseDate,
            i.RiseReturnPct, i.RiseSuccess, i.RiseEvaluatedAt);
}
