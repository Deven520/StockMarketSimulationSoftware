using StockSim.Domain.Common;

namespace StockSim.Domain.Entities;

public class StrategyDefinition : AuditedEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public Guid? CurrentVersionId { get; set; }
    public ICollection<StrategyVersion> Versions { get; set; } = new List<StrategyVersion>();
    public ICollection<StrategyBinding> Bindings { get; set; } = new List<StrategyBinding>();
}

public class StrategyVersion : EntityBase
{
    public Guid StrategyDefinitionId { get; set; }
    public StrategyDefinition? StrategyDefinition { get; set; }
    public int VersionNumber { get; set; }
    /// <summary>策略参数 JSON 文本（兼容 SQL Server 2012）。</summary>
    public string ParametersJson { get; set; } = "{}";
    public string? ChangeNote { get; set; }
    public string? CreatedBy { get; set; }
}

public class StrategyBinding : AuditedEntity
{
    public Guid StrategyDefinitionId { get; set; }
    public StrategyDefinition? StrategyDefinition { get; set; }
    public Guid? StockPoolId { get; set; }
    public string? Symbol { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
}
