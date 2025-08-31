using System.Text.RegularExpressions;

namespace MicroOrmGesg.Utils;

public class Naming
{
    private static readonly Regex LowerUpper = new(@"([a-z0-9])([A-Z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string ToSnake(string name)
        => LowerUpper.Replace(name, "$1_$2").ToLowerInvariant();

    internal static string Quote(string ident)
    {
        var parts = ident.Split('.');
        return string.Join('.', parts.Select(p => $"\"{p}\""));
    }
}