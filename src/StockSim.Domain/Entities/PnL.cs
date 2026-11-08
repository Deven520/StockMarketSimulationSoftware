using StockSim.Domain.Common;

namespace StockSim.Domain.Entities;

public class DailyPnL : EntityBase
{
    public DateTime TradeDate { get; set; }
    public Guid SimAccountId { get; set; }
    public Guid? StrategyDefinitionId { get; set; }
    public Guid? StockPoolId { get; set; }
    public string? Symbol { get; set; }
    public int TradeCount { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal Fees { get; set; }
    public decimal WinRate { get; set; }
}
