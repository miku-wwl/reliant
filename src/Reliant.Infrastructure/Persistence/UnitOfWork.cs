using Microsoft.EntityFrameworkCore;
using Reliant.Application.Abstractions;

namespace Reliant.Infrastructure.Persistence;

public class UnitOfWork(ReliantDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return db.SaveChangesAsync(cancellationToken);
    }
}
