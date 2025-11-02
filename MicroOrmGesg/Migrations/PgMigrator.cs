using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using MicroOrmGesg.Migrations.Internal;
using MicroOrmGesg.Migrations.Models;
using Npgsql;

namespace MicroOrmGesg.Migrations;

/// <summary>
/// Implementación del migrador de esquemas PostgreSQL con soporte para idempotencia,
/// checksums, detección de drift y advisory locks.
/// </summary>
public sealed class PgMigrator : IPgMigrator
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PgMigratorOptions _options;
    private readonly ILogger<PgMigrator> _logger;

    // DTO interno para filas del journal
    private sealed record JournalRow(string checksum, bool success, string? message);

    public PgMigrator(
        NpgsqlDataSource dataSource,
        PgMigratorOptions options,
        ILogger<PgMigrator> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MigrationResult> RunAsync(IMigrationSource source, CancellationToken ct = default)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        var totalStopwatch = Stopwatch.StartNew();
        var stepResults = new List<StepResult>();
        var advisoryLockKey = ComputeAdvisoryLockKey(_options.AdvisoryLockKey);

        _logger.LogInformation("Iniciando migraciones PostgreSQL con clave de advisory lock '{LockKey}'", _options.AdvisoryLockKey);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Configurar timeout de comandos
        if (_options.CommandTimeoutSeconds > 0)
        {
            await conn.ExecuteAsync($"SET statement_timeout = {_options.CommandTimeoutSeconds * 1000};");
        }

        // Adquirir advisory lock global
        _logger.LogDebug("Adquiriendo advisory lock {LockKey}...", advisoryLockKey);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@key);", new { key = advisoryLockKey });

        try
        {
            // Asegurar que existe la tabla de journal
            await EnsureJournalTableAsync(conn, ct);

            // Parsear y ejecutar pasos
            await foreach (var step in StepParser.ParseAsync(source.ReadLinesAsync(ct), ct))
            {
                var stepResult = await ProcessStepAsync(conn, step, ct);
                stepResults.Add(stepResult);

                // Detener si hubo error y StopOnError está habilitado
                if (!stepResult.Success && _options.StopOnError)
                {
                    _logger.LogError("Migración fallida en el paso {StepId} ({StepName}). Deteniendo debido a la política StopOnError.",
                        stepResult.StepId, stepResult.StepName);
                    break;
                }
            }
        }
        finally
        {
            // Liberar advisory lock
            _logger.LogDebug("Liberando advisory lock {LockKey}...", advisoryLockKey);
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@key);", new { key = advisoryLockKey });
        }

        totalStopwatch.Stop();

        var result = new MigrationResult
        {
            StepsApplied = stepResults.Count(r => r.Success && !r.Skipped),
            StepsSkipped = stepResults.Count(r => r.Skipped),
            StepsFailed = stepResults.Count(r => !r.Success),
            StepsWithDrift = stepResults.Count(r => r.DriftDetected),
            TotalDuration = totalStopwatch.Elapsed,
            Steps = stepResults
        };

        _logger.LogInformation(
            "Migraciones completadas: {Applied} aplicadas, {Skipped} omitidas, {Failed} fallidas, {Drift} con drift. Duración: {Duration:N2}s",
            result.StepsApplied, result.StepsSkipped, result.StepsFailed, result.StepsWithDrift, result.TotalDuration.TotalSeconds);

        return result;
    }

    private async Task EnsureJournalTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var tableName = _options.JournalTableName;
        var schemaPrefix = string.IsNullOrWhiteSpace(_options.JournalSchema)
            ? string.Empty
            : $"\"{_options.JournalSchema}\".";

        var createTableSql = $@"
            CREATE TABLE IF NOT EXISTS {schemaPrefix}""{tableName}""(
                step_id text PRIMARY KEY,
                step_name text NOT NULL,
                checksum text NOT NULL,
                applied_at timestamptz NOT NULL,
                duration_ms int NOT NULL,
                success boolean NOT NULL,
                message text NULL
            );";

        await conn.ExecuteAsync(createTableSql);
        _logger.LogDebug("Tabla journal {Schema}{Table} asegurada.", schemaPrefix, tableName);
    }

    private async Task<StepResult> ProcessStepAsync(NpgsqlConnection conn, MigrationStep step, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var checksum = ComputeChecksum(step.SqlToApply);

        _logger.LogInformation("Procesando paso {StepId} ({StepName})...", step.Id, step.Name);

        try
        {
            // 1. Verificar si el paso ya está registrado en el journal
            var journalRow = await GetJournalRowAsync(conn, step.Id);

            // 2. Si está registrado con el mismo checksum y success=true, omitir
            if (journalRow is { success: true } && journalRow.checksum == checksum)
            {
                _logger.LogDebug("Paso {StepId} ya aplicado con checksum coincidente. Omitiendo.", step.Id);
                stopwatch.Stop();
                return new StepResult
                {
                    StepId = step.Id,
                    StepName = step.Name,
                    Checksum = checksum,
                    Success = true,
                    Skipped = true,
                    DriftDetected = false,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    Message = "Ya aplicado con checksum coincidente"
                };
            }

            // 3. Ejecutar @check si existe
            bool alreadyAppliedPerCheck = false;
            if (!string.IsNullOrWhiteSpace(step.CheckSql))
            {
                _logger.LogDebug("Ejecutando @check para paso {StepId}: {CheckSql}", step.Id, step.CheckSql);
                alreadyAppliedPerCheck = await conn.ExecuteScalarAsync<bool>(step.CheckSql);
                _logger.LogDebug("Resultado @check para paso {StepId}: {Result}", step.Id, alreadyAppliedPerCheck);
            }

            // 4. Detectar drift: @check=true pero checksum difiere
            if (alreadyAppliedPerCheck && journalRow is not null && journalRow.checksum != checksum)
            {
                _logger.LogWarning("Drift detectado en paso {StepId}: checksum cambió de {OldChecksum} a {NewChecksum}",
                    step.Id, journalRow.checksum, checksum);

                stopwatch.Stop();

                return _options.DriftPolicy switch
                {
                    DriftPolicy.Fail => throw new InvalidOperationException(
                        $"Drift detectado en paso {step.Id}. Checksum cambió de {journalRow.checksum} a {checksum}. La política es Fail."),

                    DriftPolicy.WarnAndSkip => await RecordDriftAndSkipAsync(conn, step, checksum, stopwatch),

                    DriftPolicy.Reapply => await ExecuteStepAsync(conn, step, checksum, stopwatch, ct),

                    _ => throw new NotSupportedException($"La política de drift {_options.DriftPolicy} no está soportada.")
                };
            }

            // 5. Si @check=false o no hay journal, ejecutar el paso
            if (!alreadyAppliedPerCheck)
            {
                return await ExecuteStepAsync(conn, step, checksum, stopwatch, ct);
            }

            // 6. Si @check=true pero no hay journal, adoptarlo (migración a un esquema pre-existente)
            if (alreadyAppliedPerCheck && journalRow is null)
            {
                _logger.LogInformation("Paso {StepId} ya existe según @check. Adoptando en journal.", step.Id);
                await UpsertJournalAsync(conn, step.Id, step.Name, checksum, success: true, durationMs: 0,
                    message: "Adoptado desde esquema existente");

                stopwatch.Stop();
                return new StepResult
                {
                    StepId = step.Id,
                    StepName = step.Name,
                    Checksum = checksum,
                    Success = true,
                    Skipped = true,
                    DriftDetected = false,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    Message = "Adoptado desde esquema existente"
                };
            }

            // Caso por defecto: omitir
            stopwatch.Stop();
            return new StepResult
            {
                StepId = step.Id,
                StepName = step.Name,
                Checksum = checksum,
                Success = true,
                Skipped = true,
                DriftDetected = false,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                Message = "Omitido"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando paso {StepId} ({StepName})", step.Id, step.Name);
            stopwatch.Stop();

            await UpsertJournalAsync(conn, step.Id, step.Name, checksum, success: false,
                durationMs: (int)stopwatch.ElapsedMilliseconds, message: ex.Message);

            return new StepResult
            {
                StepId = step.Id,
                StepName = step.Name,
                Checksum = checksum,
                Success = false,
                Skipped = false,
                DriftDetected = false,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                Message = ex.Message
            };
        }
    }

    private async Task<StepResult> ExecuteStepAsync(
        NpgsqlConnection conn,
        MigrationStep step,
        string checksum,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        _logger.LogInformation("Ejecutando paso {StepId} ({StepName})...", step.Id, step.Name);

        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            // Dividir SQL en sentencias individuales
            var statements = SqlSplitter.Split(step.SqlToApply).ToList();
            _logger.LogDebug("Paso {StepId} contiene {Count} sentencia(s) SQL", step.Id, statements.Count);

            foreach (var stmt in statements)
            {
                if (string.IsNullOrWhiteSpace(stmt)) continue;

                _logger.LogTrace("Ejecutando: {Sql}", stmt);
                await conn.ExecuteAsync(stmt, transaction: tx);
            }

            await tx.CommitAsync(ct);
            stopwatch.Stop();

            _logger.LogInformation("Paso {StepId} ejecutado exitosamente en {Duration}ms", step.Id, stopwatch.ElapsedMilliseconds);

            await UpsertJournalAsync(conn, step.Id, step.Name, checksum, success: true,
                durationMs: (int)stopwatch.ElapsedMilliseconds, message: null);

            return new StepResult
            {
                StepId = step.Id,
                StepName = step.Name,
                Checksum = checksum,
                Success = true,
                Skipped = false,
                DriftDetected = false,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                Message = null
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            stopwatch.Stop();

            _logger.LogError(ex, "Paso {StepId} falló después de {Duration}ms", step.Id, stopwatch.ElapsedMilliseconds);

            await UpsertJournalAsync(conn, step.Id, step.Name, checksum, success: false,
                durationMs: (int)stopwatch.ElapsedMilliseconds, message: ex.Message);

            return new StepResult
            {
                StepId = step.Id,
                StepName = step.Name,
                Checksum = checksum,
                Success = false,
                Skipped = false,
                DriftDetected = false,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                Message = ex.Message
            };
        }
    }

    private async Task<StepResult> RecordDriftAndSkipAsync(
        NpgsqlConnection conn,
        MigrationStep step,
        string checksum,
        Stopwatch stopwatch)
    {
        var message = $"Drift detectado: checksum cambió pero @check pasó. Omitiendo según política {nameof(DriftPolicy.WarnAndSkip)}.";

        await UpsertJournalAsync(conn, step.Id, step.Name, checksum, success: true,
            durationMs: (int)stopwatch.ElapsedMilliseconds, message: message);

        return new StepResult
        {
            StepId = step.Id,
            StepName = step.Name,
            Checksum = checksum,
            Success = true,
            Skipped = true,
            DriftDetected = true,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
            Message = message
        };
    }

    private async Task<JournalRow?> GetJournalRowAsync(NpgsqlConnection conn, string stepId)
    {
        var tableName = _options.JournalTableName;
        var schemaPrefix = string.IsNullOrWhiteSpace(_options.JournalSchema)
            ? string.Empty
            : $"\"{_options.JournalSchema}\".";

        var sql = $@"
            SELECT checksum, success, message
            FROM {schemaPrefix}""{tableName}""
            WHERE step_id = @stepId;";

        return await conn.QuerySingleOrDefaultAsync<JournalRow>(sql, new { stepId });
    }

    private async Task UpsertJournalAsync(
        NpgsqlConnection conn,
        string stepId,
        string stepName,
        string checksum,
        bool success,
        int durationMs,
        string? message = null)
    {
        var tableName = _options.JournalTableName;
        var schemaPrefix = string.IsNullOrWhiteSpace(_options.JournalSchema)
            ? string.Empty
            : $"\"{_options.JournalSchema}\".";

        var sql = $@"
            INSERT INTO {schemaPrefix}""{tableName}""(step_id, step_name, checksum, applied_at, duration_ms, success, message)
            VALUES(@stepId, @stepName, @checksum, now(), @durationMs, @success, @message)
            ON CONFLICT(step_id) DO UPDATE
                SET step_name = EXCLUDED.step_name,
                    checksum = EXCLUDED.checksum,
                    applied_at = EXCLUDED.applied_at,
                    duration_ms = EXCLUDED.duration_ms,
                    success = EXCLUDED.success,
                    message = EXCLUDED.message;";

        await conn.ExecuteAsync(sql, new
        {
            stepId,
            stepName,
            checksum,
            durationMs,
            success,
            message
        });
    }

    private static string ComputeChecksum(string sql)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static long ComputeAdvisoryLockKey(string key)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
        // Tomar los primeros 8 bytes como long
        return BitConverter.ToInt64(bytes, 0);
    }
}
