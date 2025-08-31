using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MicroOrmGesg.Interfaces;
using Npgsql;

namespace MicroOrmGesg.Repository;

/// <summary>
/// Implementación de comprobación de salud de la base de datos.
/// </summary>
public sealed class DbHealthCheck(NpgsqlDataSource dataSource) : IDbHealthCheck
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async Task<(bool Ok, string Message)> CheckAsync(string? username = null, string? password = null, CancellationToken ct = default)
    {
        try
        {
            // Si proporcionan credenciales, construimos una conexión ad-hoc con ellas.
            if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password))
            {
                var baseCs = _dataSource.ConnectionString;
                var csb = new NpgsqlConnectionStringBuilder(baseCs)
                {
                    Username = username ?? new NpgsqlConnectionStringBuilder(baseCs).Username,
                    Password = password ?? new NpgsqlConnectionStringBuilder(baseCs).Password,
                    Timeout = Math.Max(5, new NpgsqlConnectionStringBuilder(baseCs).Timeout), // asegurar timeout razonable
                    CommandTimeout = Math.Max(5, new NpgsqlConnectionStringBuilder(baseCs).CommandTimeout)
                };

                await using var conn = new NpgsqlConnection(csb.ConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                var server = conn.PostgreSqlVersion.ToString();
                return (true, $"Conexión OK (SELECT 1) - Servidor PostgreSQL {server}");
            }
            else
            {
                // Usamos el pool/datasource configurado por DI
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                var server = conn.PostgreSqlVersion.ToString();
                return (true, $"Conexión OK (SELECT 1) - Servidor PostgreSQL {server}");
            }
        }
        catch (PostgresException pgex)
        {
            // Errores de PostgreSQL con código SQLSTATE
            var msg = $"PostgreSQL error (SQLSTATE {pgex.SqlState}): {pgex.MessageText}";
            return (false, msg);
        }
        catch (NpgsqlException npgex)
        {
            // Errores de Npgsql (transporte, autenticación, etc.)
            var msg = $"Npgsql error: {npgex.Message}";
            return (false, msg);
        }
        catch (SocketException sex)
        {
            var msg = $"Socket error: {sex.Message}";
            return (false, msg);
        }
        catch (TimeoutException tex)
        {
            var msg = $"Timeout al conectar o ejecutar comando: {tex.Message}";
            return (false, msg);
        }
        catch (Exception ex)
        {
            var msg = $"Error inesperado en comprobación de BD: {ex.Message}";
            return (false, msg);
        }
    }
}