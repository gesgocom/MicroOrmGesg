using Dapper;
using MicroOrmGesg.Interfaces;
using Microsoft.Extensions.Logging;

namespace MicroOrmGesg.Repository;

/// <summary>
/// Implementación de IDirectQuery que permite ejecutar queries directas de Dapper
/// usando IDbSession, compartiendo conexión y transacción con el repositorio genérico.
/// </summary>
public sealed class DirectQuery : IDirectQuery
{
    private readonly ILogger<DirectQuery> _logger;

    public DirectQuery(ILogger<DirectQuery> logger)
    {
        _logger = logger;
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando QueryAsync<{Type}>: {Sql}", typeof(T).Name, sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var result = await session.Connection!.QueryAsync<T>(cmd);
            _logger.LogDebug("QueryAsync<{Type}> ejecutado exitosamente", typeof(T).Name);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando QueryAsync<{Type}>: {Sql}", typeof(T).Name, sql);
            throw;
        }
    }

    public async Task<T> QuerySingleAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando QuerySingleAsync<{Type}>: {Sql}", typeof(T).Name, sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var result = await session.Connection!.QuerySingleAsync<T>(cmd);
            _logger.LogDebug("QuerySingleAsync<{Type}> ejecutado exitosamente", typeof(T).Name);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando QuerySingleAsync<{Type}>: {Sql}", typeof(T).Name, sql);
            throw;
        }
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando QuerySingleOrDefaultAsync<{Type}>: {Sql}", typeof(T).Name, sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var result = await session.Connection!.QuerySingleOrDefaultAsync<T>(cmd);
            _logger.LogDebug("QuerySingleOrDefaultAsync<{Type}> ejecutado exitosamente", typeof(T).Name);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando QuerySingleOrDefaultAsync<{Type}>: {Sql}", typeof(T).Name, sql);
            throw;
        }
    }

    public async Task<T> QueryFirstAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando QueryFirstAsync<{Type}>: {Sql}", typeof(T).Name, sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var result = await session.Connection!.QueryFirstAsync<T>(cmd);
            _logger.LogDebug("QueryFirstAsync<{Type}> ejecutado exitosamente", typeof(T).Name);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando QueryFirstAsync<{Type}>: {Sql}", typeof(T).Name, sql);
            throw;
        }
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando QueryFirstOrDefaultAsync<{Type}>: {Sql}", typeof(T).Name, sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var result = await session.Connection!.QueryFirstOrDefaultAsync<T>(cmd);
            _logger.LogDebug("QueryFirstOrDefaultAsync<{Type}> ejecutado exitosamente", typeof(T).Name);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando QueryFirstOrDefaultAsync<{Type}>: {Sql}", typeof(T).Name, sql);
            throw;
        }
    }

    public async Task<int> ExecuteAsync(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando ExecuteAsync (comando): {Sql}", sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var rowsAffected = await session.Connection!.ExecuteAsync(cmd);
            _logger.LogDebug("ExecuteAsync completado: {RowsAffected} fila(s) afectada(s)", rowsAffected);
            return rowsAffected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando ExecuteAsync: {Sql}", sql);
            throw;
        }
    }

    public async Task<T?> ExecuteScalarAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando ExecuteScalarAsync<{Type}>: {Sql}", typeof(T).Name, sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var result = await session.Connection!.ExecuteScalarAsync<T>(cmd);
            _logger.LogDebug("ExecuteScalarAsync<{Type}> ejecutado exitosamente: {Result}", typeof(T).Name, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando ExecuteScalarAsync<{Type}>: {Sql}", typeof(T).Name, sql);
            throw;
        }
    }

    public async Task<SqlMapper.GridReader> QueryMultipleAsync(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        ValidateSession(session);

        _logger.LogDebug("Ejecutando QueryMultipleAsync (múltiples result sets): {Sql}", sql);

        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: session.Transaction,
            cancellationToken: ct);

        try
        {
            var result = await session.Connection!.QueryMultipleAsync(cmd);
            _logger.LogDebug("QueryMultipleAsync ejecutado exitosamente");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando QueryMultipleAsync: {Sql}", sql);
            throw;
        }
    }

    private void ValidateSession(IDbSession session)
    {
        if (session == null)
        {
            _logger.LogError("IDbSession es null");
            throw new ArgumentNullException(nameof(session));
        }

        if (session.Connection == null)
        {
            _logger.LogError("La conexión de IDbSession es null. Asegúrate de llamar a OpenAsync() primero");
            throw new InvalidOperationException(
                "La sesión está cerrada. Llama a OpenAsync() antes de ejecutar queries directas.");
        }
    }
}
