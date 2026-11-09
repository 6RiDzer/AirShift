namespace AirShift.Core.Memory;

/// <summary>A byte pattern such as "48 8B 15 ? ? ? ?" where ? matches any byte.</summary>
public sealed class Signature
{
    private readonly byte[] _bytes;
    private readonly bool[] _mask;

    private Signature(byte[] bytes, bool[] mask)
    {
        _bytes = bytes;
        _mask = mask;
    }

    public int Length => _bytes.Length;

    public static Signature Parse(string pattern)
    {
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new FormatException("Empty signature.");
        var bytes = new byte[parts.Length];
        var mask = new bool[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "?" or "??")
            {
                mask[i] = false;
                continue;
            }
            bytes[i] = Convert.ToByte(parts[i], 16);
            mask[i] = true;
        }
        if (!mask[0]) throw new FormatException("A signature cannot start with a wildcard.");
        return new Signature(bytes, mask);
    }

    /// <summary>Every offset in <paramref name="data"/> where the pattern matches, up to <paramref name="limit"/>.</summary>
    public List<int> FindAll(ReadOnlySpan<byte> data, int limit = 8)
    {
        var hits = new List<int>();
        var first = _bytes[0];
        var last = data.Length - _bytes.Length;
        var i = 0;
        while (i <= last)
        {
            var next = data[i..(last + 1)].IndexOf(first);
            if (next < 0) break;
            i += next;
            if (MatchesAt(data, i))
            {
                hits.Add(i);
                if (hits.Count >= limit) break;
            }
            i++;
        }
        return hits;
    }

    private bool MatchesAt(ReadOnlySpan<byte> data, int offset)
    {
        for (var j = 1; j < _bytes.Length; j++)
        {
            if (_mask[j] && data[offset + j] != _bytes[j]) return false;
        }
        return true;
    }
}
