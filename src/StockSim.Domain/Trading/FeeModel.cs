using StockSim.Domain.Enums;

namespace StockSim.Domain.Trading;

public sealed class FeeBreakdown
{
    public decimal Commission { get; init; }
    public decimal StampTax { get; init; }
    public decimal TransferFee { get; init; }
    public decimal Slippage { get; init; }
    public decimal Total => Commission + StampTax + TransferFee + Slippage;
}

public interface IFeeCalculator
{
    FeeBreakdown Calculate(OrderSide side, decimal price, int quantity);
}

/// <summary>
/// A股常见费用近似模型：佣金万三最低5元、印花税卖出万五、过户费近似。
/// </summary>
public sealed class AShareFeeCalculator : IFeeCalculator
{
    private readonly decimal _commissionRate;
    private readonly decimal _minCommission;
    private readonly decimal _stampTaxRate;
    private readonly decimal _transferFeeRate;
    private readonly decimal _slippageRate;

    public AShareFeeCalculator(
        decimal commissionRate = 0.0003m,
        decimal minCommission = 5m,
        decimal stampTaxRate = 0.0005m,
        decimal transferFeeRate = 0.00001m,
        decimal slippageRate = 0.0001m)
    {
        _commissionRate = commissionRate;
        _minCommission = minCommission;
        _stampTaxRate = stampTaxRate;
        _transferFeeRate = transferFeeRate;
        _slippageRate = slippageRate;
    }

    public FeeBreakdown Calculate(OrderSide side, decimal price, int quantity)
    {
        var amount = price * quantity;
        var commission = Math.Max(amount * _commissionRate, _minCommission);
        var stampTax = side == OrderSide.Sell ? amount * _stampTaxRate : 0m;
        var transferFee = amount * _transferFeeRate;
        var slippage = amount * _slippageRate;

        return new FeeBreakdown
        {
            Commission = Round2(commission),
            StampTax = Round2(stampTax),
            TransferFee = Round2(transferFee),
            Slippage = Round2(slippage)
        };
    }

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
