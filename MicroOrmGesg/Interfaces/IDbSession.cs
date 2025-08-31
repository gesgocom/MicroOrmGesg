using System.Data;
using Npgsql;

namespace MicroOrmGesg.Interfaces;

public interface IDbSession : IAsyncDisposable, IDisposable
{
    NpgsqlConnection? Connection { get; }
    NpgsqlTransaction? Transaction { get; }

    Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(IsolationLevel isolation = IsolationLevel.ReadCommitted, CancellationToken ct = default);
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
    void Close();
}