using System.Runtime.CompilerServices;
using System.Text;

namespace MicroOrmGesg.Migrations;

/// <summary>
/// Fuente de migraciones basada en un archivo SQL en disco.
/// Lee el archivo línea por línea en streaming para minimizar el uso de memoria.
/// </summary>
public sealed class FileMigrationSource : IMigrationSource
{
    private readonly string _filePath;

    /// <summary>
    /// Crea una nueva fuente de migraciones desde un archivo.
    /// </summary>
    /// <param name="filePath">Ruta absoluta o relativa al archivo SQL con los pasos de migración.</param>
    /// <exception cref="ArgumentNullException">Si filePath es null.</exception>
    /// <exception cref="FileNotFoundException">Si el archivo no existe (se valida en ReadLinesAsync).</exception>
    public FileMigrationSource(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        _filePath = filePath;
    }

    /// <summary>
    /// Lee las líneas del archivo SQL de forma asíncrona.
    /// </summary>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Secuencia asíncrona de líneas del archivo.</returns>
    /// <exception cref="FileNotFoundException">Si el archivo no existe.</exception>
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            throw new FileNotFoundException($"Migration file not found: {_filePath}", _filePath);

        using var reader = new StreamReader(_filePath, Encoding.UTF8);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            yield return line ?? string.Empty;
        }
    }
}
