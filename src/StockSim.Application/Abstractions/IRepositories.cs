using StockSim.Domain.Entities;

namespace StockSim.Application.Abstractions;

public interface IStockSimDbContext
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
