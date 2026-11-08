using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

public class SimAccount : AuditedEntity
{
    public string Name { get; set; } = "默认模拟账户";
    public decimal Cash { get; set; }
    public decimal InitialCash { get; set; }
    public TradingMode TradingMode { get; set; } = TradingMode.SimIntradaySellAllowed;
    public ICollection<Position> Positions { get; set; } = new List<Position>();
}

public class Position : AuditedEntity
{
    public Guid SimAccountId { get; set; }
    public SimAccount? SimAccount { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>目标底仓（做T后尽量回到该数量）。</summary>
    public int BaseQuantity { get; set; }
    public int Quantity { get; set; }
    /// <summary>当日可卖数量（T+1 模式下小于持仓）。</summary>
    public int SellableQuantity { get; set; }
    public decimal AvgCost { get; set; }
}

public class Order : AuditedEntity
{
    public Guid SimAccountId { get; set; }
    public Guid? StrategyDefinitionId { get; set; }
    public Guid? StockPoolId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public string? RejectReason { get; set; }
    public DateTime TradeDate { get; set; }
    public bool IsManual { get; set; }
}

public class Trade : EntityBase
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public Guid SimAccountId { get; set; }
    public Guid? StrategyDefinitionId { get; set; }
    public Guid? StockPoolId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Commission { get; set; }
    public decimal StampTax { get; set; }
    public decimal TransferFee { get; set; }
    public decimal Slippage { get; set; }
    public decimal TotalFee { get; set; }
    public decimal Amount { get; set; }
    public DateTime TradeDate { get; set; }
    public DateTime TradedAt { get; set; } = DateTime.UtcNow;
}
