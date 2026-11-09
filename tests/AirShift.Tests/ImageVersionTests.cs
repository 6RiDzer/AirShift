using System.Buffers.Binary;
using AirShift.Core.Memory;
using Xunit;

namespace AirShift.Tests;

public class ImageVersionTests
{
    private const long Base = 0x7FF7_388B_0000;

    /// <summary>A minimal 64-bit image with one version resource, laid out as Windows maps it.</summary>
    private static byte[] Image(uint ms, uint ls)
    {
        var image = new byte[0x1000];
        var span = image.AsSpan();
        span[0] = (byte)'M';
        span[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(span[0x3C..], 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x80..], 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x98..], 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x98 + 128)..], 0x400); // resource directory rva
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x98 + 132)..], 0x200);

        static void Directory(Span<byte> span, int at, uint name, uint offset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 14)..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 16)..], name);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 20)..], offset);
        }

        // An icon type first, so the version is not simply the first entry.
        BinaryPrimitives.WriteUInt16LittleEndian(span[(0x400 + 14)..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x400 + 16)..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x400 + 20)..], 0x8000_0090);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x400 + 24)..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x400 + 28)..], 0x8000_0020);
        Directory(span, 0x420, 1, 0x8000_0038);
        Directory(span, 0x438, 0x409, 0x50);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x450..], 0x600); // data rva
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x454..], 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x600 + 40)..], 0xFEEF04BD);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x600 + 48)..], ms);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(0x600 + 52)..], ls);
        return image;
    }

    private static Func<long, int, byte[]?> Reader(byte[] image) => (address, length) =>
    {
        var offset = address - Base;
        return offset < 0 || offset + length > image.Length ? null : image.AsSpan((int)offset, length).ToArray();
    };

    [Fact]
    public void Reads_the_version_from_the_image_in_memory()
    {
        var image = Image((3u << 16) | 483, 970u << 16);
        Assert.Equal("3.483.970.0", ImageVersion.FromImage(Reader(image), Base, image.Length));
    }

    [Fact]
    public void Anything_else_reads_as_unknown()
    {
        var image = Image(1, 2);
        image[0] = 0;
        Assert.Null(ImageVersion.FromImage(Reader(image), Base, image.Length));
        Assert.Null(ImageVersion.FromImage((_, _) => null, Base, 0x1000));
    }

    [Fact]
    public void Reads_the_version_from_a_store_package_folder()
    {
        Assert.Equal("3.483.970.0", ImageVersion.FromPackagePath(@"C:\Program Files\WindowsApps\Microsoft.SunriseBaseGame_3.483.970.0_x64__8wekyb3d8bbwe\forzahorizon6.exe"));
        Assert.Null(ImageVersion.FromPackagePath(@"C:\Games\Steam\steamapps\common\ForzaHorizon6\forzahorizon6.exe"));
    }
}
