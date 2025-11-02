using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MicroOrmGesg.Migrations.Models;

namespace MicroOrmGesg.Migrations.Internal;

/// <summary>
/// Parsea scripts SQL con directivas @step y @check para generar pasos de migración.
/// Formato esperado:
/// -- @step id:001 name:create.users
/// -- @check SELECT to_regclass('public.users') IS NOT NULL;
/// CREATE TABLE IF NOT EXISTS users(...);
/// </summary>
internal static class StepParser
{
    private static readonly Regex StepHeaderRegex = new(
        @"^\s*--\s*@step\s+id:(?<id>[^\s]+)\s+name:(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex CheckHeaderRegex = new(
        @"^\s*--\s*@check\s+(?<sql>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Parsea una secuencia asíncrona de líneas SQL y genera pasos de migración.
    /// </summary>
    /// <param name="lines">Secuencia de líneas del script SQL.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Secuencia asíncrona de pasos de migración.</returns>
    public static async IAsyncEnumerable<MigrationStep> ParseAsync(
        IAsyncEnumerable<string> lines,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        MigrationStep? currentStep = null;

        await foreach (var rawLine in lines.WithCancellation(ct))
        {
            var line = rawLine ?? string.Empty;

            // Detectar nuevo @step
            var stepMatch = StepHeaderRegex.Match(line);
            if (stepMatch.Success)
            {
                // Emitir paso anterior si existe
                if (currentStep is not null && currentStep.SqlBuilder is not null && currentStep.SqlBuilder.Length > 0)
                {
                    yield return currentStep with
                    {
                        SqlToApply = currentStep.SqlBuilder.ToString().Trim()
                    };
                }

                // Iniciar nuevo paso
                currentStep = new MigrationStep
                {
                    Id = stepMatch.Groups["id"].Value.Trim(),
                    Name = stepMatch.Groups["name"].Value.Trim(),
                    CheckSql = null,
                    SqlToApply = string.Empty,
                    SqlBuilder = new StringBuilder()
                };
                continue;
            }

            // Detectar @check
            var checkMatch = CheckHeaderRegex.Match(line);
            if (checkMatch.Success && currentStep is not null)
            {
                currentStep = currentStep with
                {
                    CheckSql = checkMatch.Groups["sql"].Value.Trim()
                };
                continue;
            }

            // Acumular SQL del paso
            if (currentStep is not null && currentStep.SqlBuilder is not null)
            {
                currentStep.SqlBuilder.AppendLine(line);
            }
        }

        // Emitir último paso si existe
        if (currentStep is not null && currentStep.SqlBuilder is not null && currentStep.SqlBuilder.Length > 0)
        {
            yield return currentStep with
            {
                SqlToApply = currentStep.SqlBuilder.ToString().Trim()
            };
        }
    }
}
