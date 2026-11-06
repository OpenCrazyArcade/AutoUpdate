using System.Buffers.Binary;
using System.Text;
using OpenCrazyArcade.BuildUtils;
using Xunit;

namespace OpenCrazyArcade.BuildUtils.Tests;

public class CaChildPeTests
{
    /// <summary>
    /// A small PE32 that has what a Native AOT exe has and CA.exe trips over: a .data section bigger in memory than
    /// on disk (so later sections sit at a different file offset than RVA), an import table, no certificate.
    /// </summary>
    private static byte[] MiniPe(bool importVirtualProtect)
    {
        const int fileAlign = 0x200;
        var pe = new byte[0x200 + 0x200 + 0x200 + 0x200];     // headers, .text, .data (raw), .idata
        var s = pe.AsSpan();
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(s[0x3C..], 0x80);
        int nt = 0x80;
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(pe, nt);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(nt + 4)..], 0x14C);     // i386
        BinaryPrimitives.WriteUInt16LittleEndian(s[(nt + 6)..], 3);         // sections
        BinaryPrimitives.WriteUInt16LittleEndian(s[(nt + 20)..], 224);      // optional header size
        int opt = nt + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(s[opt..], 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 32)..], 0x1000);  // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 36)..], fileAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 56)..], 0x5000);  // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 60)..], 0x200);   // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 64)..], 0xDEAD);  // checksum
        // imports live in the third section (RVA 0x4000)
        BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 96 + 8)..], 0x4000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 96 + 12)..], 40);

        int sec = opt + 224;
        void Section(int i, string name, uint vsize, uint va, uint rsize, uint raw)
        {
            int at = sec + 40 * i;
            Encoding.ASCII.GetBytes(name).CopyTo(pe, at);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at + 8), vsize);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at + 12), va);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at + 16), rsize);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at + 20), raw);
        }
        Section(0, ".text", 0x100, 0x1000, 0x200, 0x200);
        Section(1, ".data", 0x2000, 0x2000, 0x200, 0x400);       // 8 KiB in memory, 512 bytes on disk
        Section(2, ".idata", 0x200, 0x4000, 0x200, 0x600);       // so this one is 0x3A00 away from its RVA

        // import descriptor for KERNEL32.dll at file 0x600
        int idata = 0x600, rvaBase = 0x4000;
        BinaryPrimitives.WriteUInt32LittleEndian(s[idata..], (uint)(rvaBase + 0x80));          // OriginalFirstThunk
        BinaryPrimitives.WriteUInt32LittleEndian(s[(idata + 12)..], (uint)(rvaBase + 0x60));   // Name
        BinaryPrimitives.WriteUInt32LittleEndian(s[(idata + 16)..], (uint)(rvaBase + 0x80));   // FirstThunk
        Encoding.ASCII.GetBytes("KERNEL32.dll\0").CopyTo(pe, idata + 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(idata + 0x80)..], (uint)(rvaBase + 0xA0)); // thunk -> hint/name
        Encoding.ASCII.GetBytes("\0\0" + (importVirtualProtect ? "VirtualProtect" : "ExitProcess") + "\0").CopyTo(pe, idata + 0xA0);
        return pe;
    }

    [Fact]
    public void A_native_aot_like_image_fails_both_checks_before_the_fix()
    {
        var problems = CaChildPe.Check(MiniPe(false));
        Assert.Contains(problems, p => p.Contains(".idata") && p.Contains("PointerToRawData"));
        Assert.Contains(problems, p => p.Contains("certificate"));
    }

    [Fact]
    public void Prepare_makes_every_section_start_at_its_RVA_and_adds_a_certificate()
    {
        var fixedPe = CaChildPe.Prepare(MiniPe(false));
        Assert.Empty(CaChildPe.Check(fixedPe));

        int opt = 0x80 + 24;
        var (certOff, certSize) = (BinaryPrimitives.ReadUInt32LittleEndian(fixedPe.AsSpan(opt + 96 + 32)),
                                   BinaryPrimitives.ReadUInt32LittleEndian(fixedPe.AsSpan(opt + 96 + 36)));
        Assert.Equal(32u, certSize);
        Assert.Equal((uint)fixedPe.Length, certOff + certSize);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fixedPe.AsSpan(opt + 64)));   // checksum cleared
    }

    [Fact]
    public void Flatten_keeps_the_bytes_of_every_section_and_the_import_table_readable()
    {
        var original = MiniPe(false);
        original[0x200] = 0x90;                                   // marker in .text
        original[0x400] = 0x5A;                                   // marker in .data
        var flat = CaChildPe.Flatten(original);

        Assert.Equal(0x90, flat[0x1000]);
        Assert.Equal(0x5A, flat[0x2000]);
        Assert.Equal("KERNEL32.dll", Encoding.ASCII.GetString(flat, 0x4000 + 0x60, 12));
        Assert.Equal(0x4200, flat.Length);                         // last section ends at RVA 0x4000 + 0x200
    }

    [Fact]
    public void An_import_of_VirtualProtect_is_reported_and_not_hidden_by_the_fix()
    {
        var fixedPe = CaChildPe.Prepare(MiniPe(true));
        var problems = CaChildPe.Check(fixedPe);
        Assert.Single(problems);
        Assert.Contains("VirtualProtect", problems[0]);
    }

    [Fact]
    public void Prepare_twice_gives_a_valid_image_too()
    {
        var once = CaChildPe.Prepare(MiniPe(false));
        var twice = CaChildPe.Prepare(once);
        Assert.Empty(CaChildPe.Check(twice));
    }

    [Fact]
    public void Garbage_is_rejected_not_crashed_on()
    {
        Assert.Throws<InvalidDataException>(() => CaChildPe.Check(new byte[200]));
        Assert.Throws<InvalidDataException>(() => CaChildPe.Flatten(Encoding.ASCII.GetBytes("not an exe")));
    }
}
