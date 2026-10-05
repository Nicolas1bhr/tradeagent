using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContainmentProbe;

/// <summary>
/// One matrix cell's measured answer. <see cref="Ok"/> is whether the SECURITY PROPERTY held — a
/// negative control ("state/ read blocked") is Ok when the attempt was DENIED, a positive control
/// ("workspace RW") is Ok when it was ALLOWED. <see cref="Expected"/> and <see cref="Outcome"/> are
/// kept separate so the record shows what was wanted and what happened, never a verdict on its own.
///
/// A secret cell records ONLY whether an open-for-read succeeded and its error code. No byte of any
/// real secret is read, hashed, printed, copied or committed (brief; CLAUDE.md credential rule).
/// </summary>
public sealed record Probe(
    [property: JsonPropertyName("cell")] string Cell,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("expected")] string Expected,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("err")] int? Err = null)
{
    public static Probe Allowed(string cell, string name, string expected, string detail, bool okWhenAllowed)
        => new(cell, name, expected, "ALLOWED", okWhenAllowed, detail);

    public static Probe Denied(string cell, string name, string expected, string detail, int err, bool okWhenDenied)
        => new(cell, name, expected, "DENIED", okWhenDenied, detail, err);
}

public static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(string path, IEnumerable<Probe> probes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(probes.ToArray(), Options));
    }

    public static Probe[] Read(string path)
        => JsonSerializer.Deserialize<Probe[]>(File.ReadAllText(path), Options) ?? [];
}
