using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace AirShift.Core.Memory;

/// <summary>
/// The game's file version when Windows will not let AirShift open the file itself (the Microsoft
/// Store version lives in a protected folder): read from the version resource of the game's own
/// image in memory, or else from the package folder's name.
/// </summary>
public static partial class ImageVersion
{
    private const uint FixedFileInfoSignature = 0xFEEF04BD;
    private const int ResourceTypeVersion = 16;

    /// <summary>"a.b.c.d" from the VS_FIXEDFILEINFO of the main module mapped at <paramref name="moduleBase"/>; null when it does not read.</summary>
    public static string? FromImage(Func<long, int, byte[]?> read, long moduleBase, long moduleSize)
    {
        try
        {
            var dos = read(moduleBase, 0x40);
            if (dos is null || dos[0] != (byte)'M' || dos[1] != (byte)'Z') return null;
            var nt = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
            if (nt <= 0 || nt > 0x1000) return null;
            var headers = read(moduleBase + nt, 24 + 240);
            if (headers is null || BinaryPrimitives.ReadUInt32LittleEndian(headers) != 0x00004550) return null;
            var optional = headers.AsSpan(24);
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
            var directories = magic switch { 0x20B => 112, 0x10B => 96, _ => -1 };
            if (directories < 0) return null;
            var resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(optional[(directories + 16)..]);
            var resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(optional[(directories + 20)..]);
            if (resourceRva == 0 || resourceSize == 0 || resourceRva + resourceSize > moduleSize) return null;
            var root = moduleBase + resourceRva;

            // Type (RT_VERSION) -> first name -> first language -> data entry.
            var typeDir = Find(read, root, ResourceTypeVersion);
            if (typeDir is not { } t || !t.Directory) return null;
            var nameDir = Find(read, root + t.Offset, null);
            if (nameDir is not { } n || !n.Directory) return null;
            var language = Find(read, root + n.Offset, null);
            if (language is not { } l || l.Directory) return null;
            var entry = read(root + l.Offset, 8);
            if (entry is null) return null;
            var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var dataSize = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(4));
            if (dataSize <= 0 || dataSize > 0x10000 || dataRva + (uint)dataSize > moduleSize) return null;
            var data = read(moduleBase + dataRva, dataSize);
            if (data is null) return null;
            for (var i = 0; i + 52 <= data.Length; i += 4)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) != FixedFileInfoSignature) continue;
                var ms = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 8));
                var ls = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 12));
                return $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The version in a Store package folder name, such as Publisher.Game_3.483.970.0_x64__abc.</summary>
    public static string? FromPackagePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var match = PackageVersion().Match(path);
        return match.Success ? match.Groups[1].Value : null;
    }

    private readonly record struct Entry(bool Directory, long Offset);

    /// <summary>The entry with this id in one resource directory, or its first entry when <paramref name="id"/> is null. Offsets are from the resource root.</summary>
    private static Entry? Find(Func<long, int, byte[]?> read, long directory, int? id)
    {
        var header = read(directory, 16);
        if (header is null) return null;
        var named = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
        var ids = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        var count = named + ids;
        if (count == 0 || count > 4096) return null;
        var entries = read(directory + 16, count * 8);
        if (entries is null) return null;
        for (var i = 0; i < count; i++)
        {
            var name = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4));
            if (id is { } wanted && ((name & 0x8000_0000) != 0 || name != wanted)) continue;
            return new Entry((offset & 0x8000_0000) != 0, offset & 0x7FFF_FFFF);
        }
        return null;
    }

    [GeneratedRegex(@"_(\d+\.\d+\.\d+\.\d+)_", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersion();
}
