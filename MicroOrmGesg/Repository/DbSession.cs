using System.Data;
using MicroOrmGesg.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MicroOrmGesg.Repository;

/*
 * Sesión de base de datos con alcance Scoped basada en un NpgsqlDataSource (pool de conexiones).
 * Proporciona una única conexión y, opcionalmente, una transacción por solicitud o unidad de trabajo.
 * Implementa IDisposable e IAsyncDisposable.
 *
 * Uso típico:
 *  1) await OpenAsync()
 *  2) opcionalmente BeginTransactionAsync()
 *  3) ejecutar comandos usando Connection (y Transaction si aplica)
 *  4) CommitAsync() o RollbackAsync()
 *  5) Dispose/DisposeAsync() (normalmente gestionado por DI si está registrado como Scoped)
 */
public sealed class DbSession(NpgsqlDataSource dataSource, ILogger<DbSession> logger) : IDbSession
{
    /* Fábrica de conexiones con pool; segura para subprocesos (thread-safe) */
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly ILogger<DbSession> _logger = logger;

    /* Conexión única por cada ámbito (scope) */
    private NpgsqlConnection? _conn;

    /* Transacción única por cada ámbito (scope) */
    private NpgsqlTransaction? _tx;

    /* Marca para saber si el objeto ya fue liberado (disposed) */
    private bool _disposed;

    /* Conexión abierta actual (si existe). Puede ser null antes de llamar a OpenAsync */
    public NpgsqlConnection? Connection => _conn;

    /* Transacción activa actual (si existe). Será null si no se ha iniciado ninguna transacción */
    public NpgsqlTransaction? Transaction => _tx;
    
    /*
     * Abre (o reutiliza) una conexión Npgsql del pool.
     * Si la conexión ya está abierta, la reutiliza.
     * Si existe una conexión previa cerrada o inválida, la elimina y abre una nueva.
     */
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_conn is { State: ConnectionState.Open })
        {
            _logger.LogDebug("Reutilizando conexión existente a la base de datos");
            return _conn;
        }

        _conn?.Dispose(); // limpiar cualquier conexión previa cerrada o defectuosa

        _logger.LogDebug("Abriendo nueva conexión a la base de datos desde el pool");
        _conn = await _dataSource.OpenConnectionAsync(ct);
        _logger.LogDebug("Conexión abierta exitosamente");

        return _conn;
    }

    /*
     * Inicia una transacción en la conexión actual de la sesión.
     * Lanza una excepción si ya existe una transacción activa.
     */
    public async Task BeginTransactionAsync(
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_tx is not null)
        {
            _logger.LogError("Intento de iniciar una transacción cuando ya existe una activa");
            throw new InvalidOperationException("Ya existe una transacción activa.");
        }

        _logger.LogDebug("Iniciando transacción con nivel de aislamiento {IsolationLevel}", isolation);
        var conn = await OpenAsync(ct);
        _tx = await conn.BeginTransactionAsync(isolation, ct);
        _logger.LogDebug("Transacción iniciada exitosamente");
    }

    /*
     * Confirma (commit) la transacción actual y la libera.
     * Lanza una excepción si no hay una transacción activa.
     * No cierra la conexión, para permitir continuar con otras operaciones.
     */
    public async Task CommitAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_tx is null)
        {
            _logger.LogError("Intento de confirmar transacción cuando no hay ninguna activa");
            throw new InvalidOperationException("No hay transacción activa que confirmar.");
        }

        _logger.LogDebug("Confirmando transacción (COMMIT)");
        await _tx.CommitAsync(ct);
        await _tx.DisposeAsync();
        _tx = null;
        _logger.LogDebug("Transacción confirmada exitosamente");
    }

    /*
     * Revierte (rollback) la transacción actual si existe y la libera.
     * Es seguro llamarlo aunque no haya transacción activa (no hace nada).
     */
    public async Task RollbackAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_tx is null)
        {
            _logger.LogDebug("Rollback solicitado pero no hay transacción activa");
            return;
        }

        _logger.LogWarning("Revirtiendo transacción (ROLLBACK)");
        await _tx.RollbackAsync(ct);
        await _tx.DisposeAsync();
        _tx = null;
        _logger.LogDebug("Transacción revertida exitosamente");
    }

    /*
     * Cierra y libera la conexión actual si está abierta.
     * Es opcional llamarlo manualmente; Dispose/DisposeAsync también la liberan.
     * Puede usarse si se quiere liberar la conexión antes de que finalice el scope.
     */
    public void Close()
    {
        if (_conn is null) return;

        _logger.LogDebug("Cerrando conexión manualmente");

        if (_conn.State != ConnectionState.Closed)
            _conn.Close();

        _conn.Dispose();
        _conn = null;

        _logger.LogDebug("Conexión cerrada y liberada");
    }

    /* Libera de forma asíncrona la transacción (si existe) y la conexión (si existe) */
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _logger.LogDebug("Liberando recursos de DbSession (DisposeAsync)");

        if (_tx is not null) await _tx.DisposeAsync();
        if (_conn is not null) await _conn.DisposeAsync();
        _tx = null;
        _conn = null;
        _disposed = true;
        GC.SuppressFinalize(this);

        _logger.LogDebug("Recursos liberados exitosamente");
    }

    /* Libera de forma síncrona la transacción (si existe) y la conexión (si existe) */
    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogDebug("Liberando recursos de DbSession (Dispose)");

        _tx?.Dispose();
        _conn?.Dispose();
        _tx = null;
        _conn = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    
    /* Lanza una excepción si el objeto ya fue liberado para evitar uso posterior */
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DbSession));
    }
}