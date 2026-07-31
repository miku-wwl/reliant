using Microsoft.EntityFrameworkCore;
using Reliant.Application.Abstractions;

namespace Reliant.Infrastructure.Persistence;

public class UnitOfWork(ReliantDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Unique constraint / concurrency conflict -> the unit was rolled back.
            return false;
        }
    }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.CommitTransactionAsync(cancellationToken);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.RollbackTransactionAsync(cancellationToken);
    }
}
