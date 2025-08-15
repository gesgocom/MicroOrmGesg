namespace MicroOrmGesg.Utils;

public enum StringFilterMode
{
    Equals,        // col = @filter
    Contains,      // col ILIKE '%' || @filter || '%'
    StartsWith,    // col ILIKE @filter || '%'
    EndsWith       // col ILIKE '%' || @filter
}