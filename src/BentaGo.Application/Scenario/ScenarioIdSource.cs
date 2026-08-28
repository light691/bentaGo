using System.Security.Cryptography;
using System.Text;

namespace BentaGo.Application.Scenario;

/// <summary>
/// Deterministic Guid source for scenarios and tests: same seed, same ids,
/// same order, byte-identical re-runs. Each id is the first 16 bytes of
/// SHA-256(seed | "|" | segment | "#" | counter), so ids are stable across
/// processes and machines while still being globally unique (docs/02:
/// internal UUID).
/// </summary>
public sealed class ScenarioIdSource
{
    private static readonly byte[] Separator = Encoding.UTF8.GetBytes("|");

    private readonly byte[] _seed;
    private int _counter;

    public ScenarioIdSource(string seed)
    {
        _seed = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    public Guid Next(string segment)
    {
        byte[] segmentBytes = System.Text.Encoding.UTF8.GetBytes($"{segment}#{_counter++}");
        byte[] material = new byte[_seed.Length + Separator.Length + segmentBytes.Length];
        _seed.CopyTo(material, 0);
        Separator.CopyTo(material, _seed.Length);
        segmentBytes.CopyTo(material, _seed.Length + Separator.Length);

        byte[] hash = SHA256.HashData(material);

        // RFC-4122 v4 shape: version nibble 4, variant 10xx, so consumers
        // that check Version/Variant (or that display the Guid) see a
        // proper-looking id instead of 16 arbitrary bytes.
        byte[] idBytes = new byte[16];
        hash.AsSpan(0, 16).CopyTo(idBytes);
        idBytes[7] = (byte)((idBytes[7] & 0x0F) | 0x40);
        idBytes[8] = (byte)((idBytes[8] & 0x3F) | 0x80);
        return new Guid(idBytes);
    }
}
