using System.Text;

namespace MicroOrmGesg.Migrations.Internal;

/// <summary>
/// Divide una cadena SQL en sentencias individuales respetando la sintaxis de PostgreSQL.
/// Maneja correctamente literales de cadena, dollar quoting, y comentarios.
/// </summary>
internal static class SqlSplitter
{
    /// <summary>
    /// Divide el SQL en sentencias separadas por punto y coma (;).
    /// Respeta literales de cadena ('...'), dollar quoting ($...$), y comentarios (-- y /* ... */).
    /// </summary>
    /// <param name="sql">SQL con una o más sentencias.</param>
    /// <returns>Enumerable de sentencias SQL individuales, sin punto y coma final.</returns>
    public static IEnumerable<string> Split(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            yield break;

        var sb = new StringBuilder();
        bool inSingleQuote = false;
        bool inDollarQuote = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        string? dollarTag = null;

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            char? next = i + 1 < sql.Length ? sql[i + 1] : null;

            // Comentario de línea: --
            if (!inSingleQuote && !inDollarQuote && !inBlockComment && c == '-' && next == '-')
            {
                inLineComment = true;
                sb.Append(c);
                continue;
            }

            if (inLineComment)
            {
                sb.Append(c);
                if (c == '\n' || c == '\r')
                    inLineComment = false;
                continue;
            }

            // Comentario de bloque: /* ... */
            if (!inSingleQuote && !inDollarQuote && !inLineComment && c == '/' && next == '*')
            {
                inBlockComment = true;
                sb.Append(c);
                continue;
            }

            if (inBlockComment)
            {
                sb.Append(c);
                if (c == '*' && next == '/')
                {
                    sb.Append('/');
                    i++; // saltar el '/'
                    inBlockComment = false;
                }
                continue;
            }

            // Literales de cadena con comillas simples: '...'
            if (!inDollarQuote && !inBlockComment && !inLineComment && c == '\'')
            {
                sb.Append(c);
                // Manejar escape de comillas simples: ''
                if (inSingleQuote && next == '\'')
                {
                    sb.Append('\'');
                    i++; // saltar la segunda comilla
                }
                else
                {
                    inSingleQuote = !inSingleQuote;
                }
                continue;
            }

            // Dollar quoting: $...$  o  $tag$...$tag$
            if (!inSingleQuote && !inBlockComment && !inLineComment && c == '$')
            {
                // Extraer el tag (puede estar vacío: $$)
                int tagStart = i;
                int tagEnd = i + 1;
                while (tagEnd < sql.Length && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_'))
                    tagEnd++;

                if (tagEnd < sql.Length && sql[tagEnd] == '$')
                {
                    string currentTag = sql.Substring(tagStart, tagEnd - tagStart + 1); // incluye ambos $

                    if (!inDollarQuote)
                    {
                        // Iniciar dollar quote
                        inDollarQuote = true;
                        dollarTag = currentTag;
                        sb.Append(currentTag);
                        i = tagEnd; // saltar todo el tag
                        continue;
                    }
                    else if (currentTag == dollarTag)
                    {
                        // Finalizar dollar quote
                        inDollarQuote = false;
                        dollarTag = null;
                        sb.Append(currentTag);
                        i = tagEnd; // saltar todo el tag
                        continue;
                    }
                }
            }

            // Separador de sentencias: ;
            if (!inSingleQuote && !inDollarQuote && !inBlockComment && !inLineComment && c == ';')
            {
                var statement = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(statement))
                    yield return statement;

                sb.Clear();
                continue;
            }

            // Carácter normal
            sb.Append(c);
        }

        // Sentencia final sin punto y coma
        var finalStatement = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(finalStatement))
            yield return finalStatement;
    }
}
