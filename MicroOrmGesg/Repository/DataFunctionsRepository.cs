using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MicroOrmGesg.Interfaces;

namespace MicroOrmGesg.Repository;

public sealed class DataFunctionsRepository : IDataFunctions
{
    public async Task<TResult?> CallFunctionAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        var (sql, parameters) = BuildFunctionCallSql(functionName, args, schema, forSetReturning: false);
        var cmd = new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct);
        return await session.Connection!.ExecuteScalarAsync<TResult?>(cmd);
    }

    public async Task<List<TResult>> CallFunctionListAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        var (sql, parameters) = BuildFunctionCallSql(functionName, args, schema, forSetReturning: true);
        var cmd = new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct);
        var rows = await session.Connection!.QueryAsync<TResult>(cmd);
        return rows.AsList();
    }

    public async Task CallVoidFunctionAsync(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        var (sql, parameters) = BuildFunctionCallSql(functionName, args, schema, forSetReturning: false);
        var cmd = new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct);
        await session.Connection!.ExecuteAsync(cmd);
    }

    private static (string sql, DynamicParameters parameters) BuildFunctionCallSql(string functionName, object? args, string? schema, bool forSetReturning)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("Nombre de función inválido", nameof(functionName));

        string qualified = string.IsNullOrWhiteSpace(schema) ? functionName : $"{schema}.{functionName}";

        var parameters = new DynamicParameters();
        var placeholders = new List<string>();

        if (args is null)
        {
            // sin parámetros
        }
        else if (args is IDictionary<string, object?> dict)
        {
            foreach (var kv in dict)
            {
                var name = NormalizeParamName(kv.Key);
                parameters.Add(name, kv.Value);
                placeholders.Add($"@{name}");
            }
        }
        else
        {
            var props = args.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetMethod is not null);
            foreach (var p in props)
            {
                var name = NormalizeParamName(p.Name);
                var value = p.GetValue(args);
                parameters.Add(name, value);
                placeholders.Add($"@{name}");
            }
        }

        string argList = placeholders.Count > 0 ? string.Join(",", placeholders) : string.Empty;
        string sql = forSetReturning ? $"select * from {qualified}({argList})" : $"select {qualified}({argList})";
        return (sql, parameters);
    }

    private static string NormalizeParamName(string name)
        => name.Trim().TrimStart('@');
}