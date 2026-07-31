namespace Reliant.Application.Abstractions;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to persist all tracked changes in a single database transaction.
    /// Returns <c>false</c> when the write failed because of a database-level
    /// uniqueness/concurrency conflict (e.g. a concurrent duplicate callback
    /// already inserted the same inbox MessageId) and the whole unit was rolled
    /// back, so the caller can treat the operation as already handled.
    /// </summary>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default);
}
