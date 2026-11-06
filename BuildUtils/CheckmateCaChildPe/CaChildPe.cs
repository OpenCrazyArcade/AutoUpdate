using System.Buffers.Binary;
using System.Text;

namespace OpenCrazyArcade.BuildUtils;

/// <summary>
/// Makes a PE32 image pass the two checks CA.exe runs on the child process it starts as the HackShield updater
/// (HS_Updater_LaunchHSUpdate, see RE/hackshield_architecture.md section 5.2):
///
///  1. Import scan (sub_ABCBF0). It turns an RVA into a file offset as <c>rva - (VirtualAddress - PointerToRawData)</c>
///     and loops forever when the delta of a section is not zero, which is what a Native AOT image has (its .data
///     section is larger in memory than on disk, so every later section is shifted). It also refuses an image that
///     imports KERNEL32!VirtualProtect.
///  2. Certificate table (sub_ABCF00). Wants DataDirectory[4].Size != 0 in the running image. The signature is never
///     verified, so a dummy blob is enough.
///
/// <see cref="Flatten"/> and <see cref="AddCertificate"/> do the two fixes, <see cref="Check"/> reports what is still
/// wrong. Nothing here touches code or data of the program, only the file layout and the header.
/// </summary>
public static class CaChildPe
{
    private const int OptionalHeaderPe32Magic = 0x10B;
    private const int SecurityDirectory = 4;
    private const int ImportDirectory = 1;
    private const int DebugDirectory = 6;

    /// <summary>Flatten, then add the certificate: what the build runs.</summary>
    public static byte[] Prepare(byte[] pe) => AddCertificate(Flatten(pe));

    /// <summary>What CA.exe's checks would object to; empty when the image is fine.</summary>
    public static IReadOnlyList<string> Check(byte[] pe)
    {
        var problems = new List<string>();
        var h = Header.Parse(pe);

        foreach (var s in h.Sections)
            if (s.VirtualAddress != s.PointerToRawData)
                problems.Add($"section {s.Name}: VirtualAddress 0x{s.VirtualAddress:X} != PointerToRawData 0x{s.PointerToRawData:X}");

        var (certOff, certSize) = h.GetDirectory(SecurityDirectory);
        if (certSize == 0) problems.Add("certificate table (DataDirectory[4]) is empty");
        else if ((long)certOff + certSize > pe.Length) problems.Add("certificate table lies outside the file");

        if (ImportsFunction(pe, h, "KERNEL32.dll", "VirtualProtect"))
            problems.Add("imports KERNEL32!VirtualProtect (CA.exe refuses such an updater)");

        return problems;
    }

    /// <summary>
    /// Lays the file out so every section starts at file offset == its RVA (zero padding where the file was shorter
    /// than the section in memory). The program is unchanged: RVAs stay, only the on-disk positions move.
    /// </summary>
    public static byte[] Flatten(byte[] pe)
    {
        var h = Header.Parse(pe);
        uint fileAlign = h.FileAlignment;
        if (fileAlign == 0 || (h.SizeOfHeaders > h.Sections.Min(s => s.VirtualAddress)))
            throw new InvalidDataException("headers overlap the first section");

        uint end = 0;
        foreach (var s in h.Sections)
        {
            if (s.VirtualAddress % fileAlign != 0)
                throw new InvalidDataException($"section {s.Name} is not aligned to FileAlignment");
            end = Math.Max(end, s.VirtualAddress + RoundUp(Math.Max(s.VirtualSize, s.SizeOfRawData), fileAlign));
        }

        var outPe = new byte[end];
        Array.Copy(pe, 0, outPe, 0, h.SizeOfHeaders);
        for (int i = 0; i < h.Sections.Count; i++)
        {
            var s = h.Sections[i];
            if (s.SizeOfRawData != 0)
                Array.Copy(pe, s.PointerToRawData, outPe, s.VirtualAddress, s.SizeOfRawData);
            int at = h.SectionTableOffset + 40 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(outPe.AsSpan(at + 16), RoundUp(Math.Max(s.VirtualSize, s.SizeOfRawData), fileAlign));
            BinaryPrimitives.WriteUInt32LittleEndian(outPe.AsSpan(at + 20), s.VirtualAddress);
        }

        // The debug directory stores a file offset of its own; keep it pointing at the same bytes.
        var (dbgRva, dbgSize) = h.GetDirectoryRva(DebugDirectory);
        for (uint e = 0; dbgRva != 0 && e + 28 <= dbgSize; e += 28)
        {
            int at = (int)(dbgRva + e);          // flat layout: offset == RVA
            uint rawRva = BinaryPrimitives.ReadUInt32LittleEndian(outPe.AsSpan(at + 20));
            if (rawRva != 0) BinaryPrimitives.WriteUInt32LittleEndian(outPe.AsSpan(at + 24), rawRva);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(outPe.AsSpan(h.OptionalHeaderOffset + 64), 0);   // checksum
        return outPe;
    }

    /// <summary>Appends a dummy WIN_CERTIFICATE after the image and points DataDirectory[4] at it.</summary>
    public static byte[] AddCertificate(byte[] pe)
    {
        var h = Header.Parse(pe);
        int start = (pe.Length + 7) & ~7;
        byte[] blob = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, 32);                  // dwLength
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(4), 0x0200);    // wRevision
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(6), 0x0002);    // wCertificateType: PKCS signed data
        Encoding.ASCII.GetBytes("OpenCrazyArcade-CleanHS").CopyTo(blob, 8);

        var outPe = new byte[start + blob.Length];
        Array.Copy(pe, outPe, pe.Length);
        blob.CopyTo(outPe, start);
        int dir = h.OptionalHeaderOffset + 96 + 8 * SecurityDirectory;
        BinaryPrimitives.WriteUInt32LittleEndian(outPe.AsSpan(dir), (uint)start);
        BinaryPrimitives.WriteUInt32LittleEndian(outPe.AsSpan(dir + 4), (uint)blob.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(outPe.AsSpan(h.OptionalHeaderOffset + 64), 0);
        return outPe;
    }

    private static uint RoundUp(uint v, uint a) => (v + a - 1) / a * a;

    private static bool ImportsFunction(byte[] pe, Header h, string dll, string function)
    {
        var (rva, size) = h.GetDirectoryRva(ImportDirectory);
        if (rva == 0) return false;
        for (int d = h.RvaToOffset(rva); d >= 0 && d + 20 <= pe.Length; d += 20)
        {
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(d + 12));
            uint thunk = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(d));
            if (nameRva == 0) break;
            if (thunk == 0) thunk = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(d + 16));
            if (!string.Equals(ReadAscii(pe, h.RvaToOffset(nameRva)), dll, StringComparison.OrdinalIgnoreCase)) continue;
            for (int t = h.RvaToOffset(thunk); t >= 0 && t + 4 <= pe.Length; t += 4)
            {
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(t));
                if (entry == 0) break;
                if ((entry & 0x80000000) != 0) continue;     // by ordinal
                int hint = h.RvaToOffset(entry);
                if (hint >= 0 && string.Equals(ReadAscii(pe, hint + 2), function, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private static string ReadAscii(byte[] b, int at)
    {
        if (at < 0) return "";
        int e = at;
        while (e < b.Length && b[e] != 0) e++;
        return Encoding.ASCII.GetString(b, at, e - at);
    }

    private sealed record Section(string Name, uint VirtualSize, uint VirtualAddress, uint SizeOfRawData, uint PointerToRawData);

    private sealed class Header
    {
        public int OptionalHeaderOffset, SectionTableOffset;
        public uint FileAlignment, SizeOfHeaders;
        public List<Section> Sections = [];
        private byte[] _pe = [];

        public static Header Parse(byte[] pe)
        {
            if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') throw new InvalidDataException("not a PE file");
            int nt = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
            if (nt < 0 || nt + 24 > pe.Length || BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(nt)) != 0x4550)
                throw new InvalidDataException("no PE signature");
            var h = new Header { _pe = pe, OptionalHeaderOffset = nt + 24 };
            int sections = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(nt + 6));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(nt + 20));
            if (BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(h.OptionalHeaderOffset)) != OptionalHeaderPe32Magic)
                throw new InvalidDataException("only PE32 (x86) images are supported");
            h.FileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(h.OptionalHeaderOffset + 36));
            h.SizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(h.OptionalHeaderOffset + 60));
            h.SectionTableOffset = h.OptionalHeaderOffset + optSize;
            for (int i = 0; i < sections; i++)
            {
                int at = h.SectionTableOffset + 40 * i;
                string name = Encoding.ASCII.GetString(pe, at, 8).TrimEnd('\0');
                h.Sections.Add(new Section(name,
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(at + 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(at + 12)),
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(at + 16)),
                    BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(at + 20))));
            }
            return h;
        }

        /// <summary>Directory entry as stored (for the certificate table that is a FILE offset, not an RVA).</summary>
        public (uint Offset, uint Size) GetDirectory(int i) =>
            (BinaryPrimitives.ReadUInt32LittleEndian(_pe.AsSpan(OptionalHeaderOffset + 96 + 8 * i)),
             BinaryPrimitives.ReadUInt32LittleEndian(_pe.AsSpan(OptionalHeaderOffset + 100 + 8 * i)));

        public (uint Rva, uint Size) GetDirectoryRva(int i) => GetDirectory(i);

        public int RvaToOffset(uint rva)
        {
            foreach (var s in Sections)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                    return (int)(rva - s.VirtualAddress + s.PointerToRawData);
            return -1;
        }
    }
}
