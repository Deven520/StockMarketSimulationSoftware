namespace StockSim.Domain.Common;

public abstract class EntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreationTime { get; set; } = DateTime.UtcNow;
}

public abstract class AuditedEntity : EntityBase
{
    public string? CreatorUserId { get; set; }
    public DateTime? LastModificationTime { get; set; }
}
