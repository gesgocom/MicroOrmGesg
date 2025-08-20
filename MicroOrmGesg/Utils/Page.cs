using Newtonsoft.Json;

namespace MicroOrmGesg.Utils;

public sealed class Page<T>
{
    public required List<T> Items { get; init; }
    public required int Total { get; init; }
    [JsonProperty("Page")] public required int PageNumber { get; init; }
    public required int Size { get; init; }
}
