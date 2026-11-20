using System.Globalization;
using System.IO;
using System.Text;

namespace Mskpy.Infrastructure;

/// <summary>
/// Stamps a JPEG copy's EXIF with GPS (latitude/longitude) and DateTimeOriginal.
///
/// The existing APP1(EXIF) segment is MERGED, not rebuilt:
///   * every existing TIFF entry (Make, Model, Software, FNumber, ExposureTime,
///     Orientation, MakerNote, …) is copied byte-for-byte to a new IFD;
///   * only DateTimeOriginal (0x9003) is replaced;
///   * the GPS IFD is inserted (or replaced) and referenced from the Exif IFD;
///   * the original TIFF bytes are left untouched, so all out-of-line values
///     (including MakerNotes) keep their original offsets.
///
/// When the source has no Exif APP1, a minimal APP1 is created and inserted
/// after SOI / APP0.
///
/// No external library: the EXIF APP1 segment is written directly into the JPEG.
/// JPEG layout preserved: SOI ... APP0 ... APP1(EXIF) ... SOF0 ... SOS ... EOI.
/// </summary>
public static class JpegExifStamper
{
    private const ushort TagExifIfdPointer = 0x8769;
    private const ushort TagGpsIfdPointer = 0x8825;
    private const ushort TagDateTimeOriginal = 0x9003;

    /// <summary>
    /// Returns the path of a copy of <paramref name="sourcePath"/> with EXIF GPS +
    /// DateTimeOriginal stamped into it. The original file is not modified.
    ///
    /// P1 (0.9.10): stamping is best-effort. A partial or unusual EXIF block,
    /// an out-of-range segment or a 64 KB APP1 overflow must never fail an
    /// attach: the source bytes are copied through unmodified instead and the
    /// reason is reported through <paramref name="log"/>.
    /// </summary>
    public static string Stamp(string sourcePath, double latitude, double longitude, DateTime dateTime, string? outputPath = null, Action<string>? log = null)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source JPEG not found.", sourcePath);
        outputPath ??= Path.ChangeExtension(sourcePath, null) + "-stamped" + Path.GetExtension(sourcePath);

        var jpeg = File.ReadAllBytes(sourcePath);
        byte[] stamped;
        try
        {
            stamped = StampJpeg(jpeg, latitude, longitude, dateTime);
        }
        catch (Exception ex)
        {
            // P1: keep the photo. Unstamped bytes are a valid upload; a lost
            // photo is not.
            log?.Invoke($"EXIF stamp skipped for '{Path.GetFileName(sourcePath)}': {ex.GetType().Name}: {ex.Message}");
            stamped = jpeg;
        }

        try
        {
            File.WriteAllBytes(outputPath, stamped);
        }
        catch (Exception ex) when (stamped != jpeg)
        {
            // Writing the stamped copy failed (locked/AV): fall back to the
            // untouched source bytes under the same slot name.
            log?.Invoke($"Stamped copy could not be written for '{Path.GetFileName(sourcePath)}': {ex.Message}; using the source bytes.");
            File.WriteAllBytes(outputPath, jpeg);
        }
        return outputPath;
    }

    /// <summary>
    /// Stamps an in-memory JPEG. Exposed so the behaviour can be regression-tested
    /// without touching the file system.
    /// </summary>
    public static byte[] StampJpeg(byte[] jpeg, double latitude, double longitude, DateTime dateTime)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Not a valid JPEG (missing SOI).");

        if (TryFindExifApp1(jpeg, out var app1Offset, out var app1Length))
        {
            var tiffLength = app1Length - 2 - 6; // length field - 2 length bytes - "Exif\0\0"
            if (tiffLength < 8)
                return InsertFreshApp1(jpeg, latitude, longitude, dateTime);

            var tiff = new byte[tiffLength];
            Buffer.BlockCopy(jpeg, app1Offset + 4 + 6, tiff, 0, tiffLength);

            var merged = MergeTiff(tiff, latitude, longitude, dateTime);
            var app1 = BuildApp1(merged);

            var tailStart = app1Offset + 2 + app1Length;
            var result = new byte[jpeg.Length - (2 + app1Length) + app1.Length];
            Buffer.BlockCopy(jpeg, 0, result, 0, app1Offset);
            Buffer.BlockCopy(app1, 0, result, app1Offset, app1.Length);
            Buffer.BlockCopy(jpeg, tailStart, result, app1Offset + app1.Length, jpeg.Length - tailStart);
            return result;
        }

        return InsertFreshApp1(jpeg, latitude, longitude, dateTime);
    }

    private static byte[] InsertFreshApp1(byte[] jpeg, double latitude, double longitude, DateTime dateTime)
    {
        var app1 = BuildApp1(BuildTiff(latitude, longitude, dateTime));

        // Insert after SOI, and after APP0 (JFIF) when present, so readers see
        // SOI -> APP0 -> APP1. A segment is marker (2) + length field (2) +
        // payload (length - 2), so the next marker starts at pos + 2 + length.
        var insertAt = 2;
        if (jpeg.Length > 6 && jpeg[2] == 0xFF && jpeg[3] == 0xE0)
        {
            var app0Length = (jpeg[4] << 8) | jpeg[5];
            var app0End = 2 + 2 + app0Length;
            if (app0End <= jpeg.Length) insertAt = app0End;
        }

        var result = new byte[jpeg.Length + app1.Length];
        Buffer.BlockCopy(jpeg, 0, result, 0, insertAt);
        Buffer.BlockCopy(app1, 0, result, insertAt, app1.Length);
        Buffer.BlockCopy(jpeg, insertAt, result, insertAt + app1.Length, jpeg.Length - insertAt);
        return result;
    }

    /// <summary>Finds the first APP1 segment whose payload starts with "Exif\0\0".</summary>
    private static bool TryFindExifApp1(byte[] jpeg, out int offset, out int length)
    {
        offset = -1;
        length = 0;
        var pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) break;
            var marker = jpeg[pos + 1];
            if (marker is 0xD8 or 0xD9 or 0xDA) break;

            var segLength = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (segLength < 2 || pos + 2 + segLength > jpeg.Length) break;

            if (marker == 0xE1 && segLength >= 8 &&
                jpeg[pos + 4] == 0x45 && jpeg[pos + 5] == 0x78 &&
                jpeg[pos + 6] == 0x69 && jpeg[pos + 7] == 0x66 &&
                jpeg[pos + 8] == 0x00 && jpeg[pos + 9] == 0x00)
            {
                offset = pos;
                length = segLength;
                return true;
            }

            pos += 2 + segLength;
        }
        return false;
    }

    /// <summary>
    /// Appends replacement IFDs to the existing TIFF. Original bytes stay in place,
    /// so every copied entry keeps its original out-of-line value offset.
    /// </summary>
    private static byte[] MergeTiff(byte[] source, double latitude, double longitude, DateTime dateTime)
    {
        if (source.Length < 8) return BuildTiff(latitude, longitude, dateTime);

        var littleEndian = source[0] == 0x49 && source[1] == 0x49;
        var bigEndian = source[0] == 0x4D && source[1] == 0x4D;
        if (!littleEndian && !bigEndian) return BuildTiff(latitude, longitude, dateTime);
        if (ReadU16(source, 2, littleEndian) != 42) return BuildTiff(latitude, longitude, dateTime);

        var ifd0Offset = ReadU32(source, 4, littleEndian);
        if (ifd0Offset < 8 || ifd0Offset + 2 > source.Length) return BuildTiff(latitude, longitude, dateTime);

        var ifd0 = ReadIfd(source, ifd0Offset, littleEndian);
        var exifOffset = FindEntryValue(ifd0.Entries, TagExifIfdPointer);
        // P1 (0.9.10): a JPEG can carry an EXIF APP1 with only IFD0 - no Exif
        // sub-IFD at all. `default(Ifd)` has a null Entries list, and the
        // foreach below used to throw NullReferenceException on exactly those
        // files ("Photo could not be loaded: Object reference not set ...").
        Ifd exif = new(new List<IfdEntry>(), 0);

        if (exifOffset >= 8 && exifOffset + 2 <= source.Length)
            exif = ReadIfd(source, exifOffset, littleEndian);

        var output = new List<byte>(source.Length + 512);
        output.AddRange(source);

        // DateTimeOriginal payload (20 bytes incl. NUL).
        Align2(output);
        var dateTimeOffset = (uint)output.Count;
        var dateTimeBytes = Encoding.ASCII.GetBytes(
            dateTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture) + "\0");
        output.AddRange(dateTimeBytes);
        Align2(output);

        // Fresh GPS IFD.
        var gpsIfdOffset = AppendGpsIfd(output, latitude, longitude, littleEndian);
        Align2(output);

        // New Exif IFD: copy every existing entry except the two we replace.
        var exifEntries = new List<IfdEntry>();
        foreach (var entry in exif.Entries)
            if (entry.Tag != TagDateTimeOriginal && entry.Tag != TagGpsIfdPointer)
                exifEntries.Add(entry);
        exifEntries.Add(new IfdEntry(TagDateTimeOriginal, 2, (uint)dateTimeBytes.Length, dateTimeOffset, null));
        var newExifOffset = AppendIfd(output, exifEntries, exif.NextIfd, littleEndian);
        Align2(output);

        // New IFD0: copy every existing entry except the two pointers, then add
        // the fresh Exif pointer and the GPS pointer (0x8825 belongs in IFD0 per
        // the EXIF spec, which is where standard readers such as PIL look).
        var ifd0Entries = new List<IfdEntry>();
        foreach (var entry in ifd0.Entries)
            if (entry.Tag != TagExifIfdPointer && entry.Tag != TagGpsIfdPointer)
                ifd0Entries.Add(entry);
        ifd0Entries.Add(new IfdEntry(TagExifIfdPointer, 4, 1, newExifOffset, null));
        ifd0Entries.Add(new IfdEntry(TagGpsIfdPointer, 4, 1, gpsIfdOffset, null));
        var newIfd0Offset = AppendIfd(output, ifd0Entries, ifd0.NextIfd, littleEndian);
        Align2(output);

        WriteU32(output, 4, newIfd0Offset, littleEndian);
        return output.ToArray();
    }

    /// <summary>Builds a minimal TIFF structure (II, little-endian) from scratch.</summary>
    private static byte[] BuildTiff(double latitude, double longitude, DateTime dateTime)
    {
        const bool littleEndian = true;
        var output = new List<byte>(256);

        output.Add(0x49); // 'I'
        output.Add(0x49); // 'I'
        AppendU16(output, 42, littleEndian);
        AppendU32(output, 0, littleEndian); // IFD0 offset patched below

        Align2(output);
        var dateTimeOffset = (uint)output.Count;
        var dateTimeBytes = Encoding.ASCII.GetBytes(
            dateTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture) + "\0");
        output.AddRange(dateTimeBytes);
        Align2(output);

        var gpsIfdOffset = AppendGpsIfd(output, latitude, longitude, littleEndian);
        Align2(output);

        var exifEntries = new List<IfdEntry>
        {
            new(TagDateTimeOriginal, 2, (uint)dateTimeBytes.Length, dateTimeOffset, null)
        };
        var exifOffset = AppendIfd(output, exifEntries, 0, littleEndian);
        Align2(output);

        var ifd0Entries = new List<IfdEntry>
        {
            new(TagExifIfdPointer, 4, 1, exifOffset, null),
            new(TagGpsIfdPointer, 4, 1, gpsIfdOffset, null)
        };
        var ifd0Offset = AppendIfd(output, ifd0Entries, 0, littleEndian);
        Align2(output);

        WriteU32(output, 4, ifd0Offset, littleEndian);
        return output.ToArray();
    }

    private static uint AppendGpsIfd(List<byte> output, double latitude, double longitude, bool littleEndian)
    {
        // Variable-length rationals first (offsets are absolute from TIFF start).
        var latitudeOffset = AppendRationals(output, DegreesToDms(Math.Abs(latitude)), littleEndian);
        var longitudeOffset = AppendRationals(output, DegreesToDms(Math.Abs(longitude)), littleEndian);
        Align2(output);
        var altitudeOffset = (uint)output.Count;
        AppendU32(output, 0, littleEndian); // altitude numerator
        AppendU32(output, 1, littleEndian); // altitude denominator
        Align2(output);

        var latRef = new byte[] { (byte)(latitude >= 0 ? 'N' : 'S'), 0, 0, 0 };
        var lonRef = new byte[] { (byte)(longitude >= 0 ? 'E' : 'W'), 0, 0, 0 };

        var entries = new List<IfdEntry>
        {
            new(0x0001, 2, 2, 0, latRef),
            new(0x0002, 5, 3, latitudeOffset, null),
            new(0x0003, 2, 2, 0, lonRef),
            new(0x0004, 5, 3, longitudeOffset, null),
            new(0x0005, 1, 1, 0, new byte[] { 0, 0, 0, 0 }),
            new(0x0006, 5, 1, altitudeOffset, null)
        };

        return AppendIfd(output, entries, 0, littleEndian);
    }

    private static uint AppendRationals(List<byte> output, (uint Degrees, uint Minutes, uint SecondsNumerator, uint SecondsDenominator) dms, bool littleEndian)
    {
        Align2(output);
        var offset = (uint)output.Count;
        AppendU32(output, dms.Degrees, littleEndian);
        AppendU32(output, 1, littleEndian);
        AppendU32(output, dms.Minutes, littleEndian);
        AppendU32(output, 1, littleEndian);
        AppendU32(output, dms.SecondsNumerator, littleEndian);
        AppendU32(output, dms.SecondsDenominator, littleEndian);
        return offset;
    }

    /// <summary>Appends a sorted IFD and returns its offset.</summary>
    private static uint AppendIfd(List<byte> output, List<IfdEntry> entries, uint nextIfd, bool littleEndian)
    {
        Align2(output);
        var offset = (uint)output.Count;
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        AppendU16(output, (ushort)entries.Count, littleEndian);
        foreach (var entry in entries)
        {
            AppendU16(output, entry.Tag, littleEndian);
            AppendU16(output, entry.Type, littleEndian);
            AppendU32(output, entry.Count, littleEndian);
            if (entry.Inline is { Length: 4 } inline)
            {
                output.AddRange(inline);
            }
            else
            {
                AppendU32(output, entry.ValueOrOffset, littleEndian);
            }
        }
        AppendU32(output, nextIfd, littleEndian);
        return offset;
    }

    private static Ifd ReadIfd(byte[] source, uint offset, bool littleEndian)
    {
        var entries = new List<IfdEntry>();
        if (offset + 2 > source.Length) return new Ifd(entries, 0);

        var count = ReadU16(source, (int)offset, littleEndian);
        var pos = (int)offset + 2;
        // P1: a corrupt entry count must not walk off the buffer or spin.
        var room = Math.Max(0, (source.Length - pos - 4) / 12);
        if (count > room) count = (ushort)Math.Min(room, ushort.MaxValue);
        for (var i = 0; i < count && pos + 12 <= source.Length; i++, pos += 12)
        {
            entries.Add(new IfdEntry(
                ReadU16(source, pos, littleEndian),
                ReadU16(source, pos + 2, littleEndian),
                ReadU32(source, pos + 4, littleEndian),
                ReadU32(source, pos + 8, littleEndian),
                null));
        }

        var next = pos + 4 <= source.Length ? ReadU32(source, pos, littleEndian) : 0u;
        return new Ifd(entries, next);
    }

    private static uint FindEntryValue(List<IfdEntry> entries, ushort tag)
    {
        foreach (var entry in entries)
            if (entry.Tag == tag)
                return entry.ValueOrOffset;
        return 0;
    }

    private static (uint Degrees, uint Minutes, uint SecondsNumerator, uint SecondsDenominator) DegreesToDms(double degrees)
    {
        var wholeDegrees = Math.Floor(degrees);
        var minutesTotal = (degrees - wholeDegrees) * 60.0;
        var wholeMinutes = Math.Floor(minutesTotal);
        var seconds = (minutesTotal - wholeMinutes) * 60.0;

        const uint secondsDenominator = 10_000;
        var secondsNumerator = (uint)Math.Round(seconds * secondsDenominator, MidpointRounding.AwayFromZero);
        return ((uint)wholeDegrees, (uint)wholeMinutes, secondsNumerator, secondsDenominator);
    }

    private static byte[] BuildApp1(byte[] tiff)
    {
        // marker (2) + length field (2) + "Exif\0\0" (6) + TIFF
        var lengthField = 2 + 6 + tiff.Length;
        if (lengthField > ushort.MaxValue)
            throw new InvalidDataException("EXIF segment exceeds the 64 KB APP1 limit.");

        var segment = new byte[2 + lengthField];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)(lengthField >> 8);
        segment[3] = (byte)(lengthField & 0xFF);
        segment[4] = 0x45; // 'E'
        segment[5] = 0x78; // 'x'
        segment[6] = 0x69; // 'i'
        segment[7] = 0x66; // 'f'
        segment[8] = 0x00;
        segment[9] = 0x00;
        Buffer.BlockCopy(tiff, 0, segment, 10, tiff.Length);
        return segment;
    }

    private static ushort ReadU16(byte[] buffer, int offset, bool littleEndian)
    {
        if (offset + 2 > buffer.Length) return 0;
        return littleEndian
            ? (ushort)(buffer[offset] | (buffer[offset + 1] << 8))
            : (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static uint ReadU32(byte[] buffer, int offset, bool littleEndian)
    {
        if (offset + 4 > buffer.Length) return 0;
        return littleEndian
            ? (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24))
            : (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
    }

    private static void WriteU32(List<byte> buffer, int offset, uint value, bool littleEndian)
    {
        if (offset + 4 > buffer.Count) return;
        if (littleEndian)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
        else
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }

    private static void AppendU16(List<byte> buffer, ushort value, bool littleEndian)
    {
        if (littleEndian)
        {
            buffer.Add((byte)value);
            buffer.Add((byte)(value >> 8));
        }
        else
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }
    }

    private static void AppendU32(List<byte> buffer, uint value, bool littleEndian)
    {
        if (littleEndian)
        {
            buffer.Add((byte)value);
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 24));
        }
        else
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }
    }

    private static void Align2(List<byte> buffer)
    {
        if ((buffer.Count & 1) != 0) buffer.Add(0);
    }

    private readonly record struct IfdEntry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset, byte[]? Inline);
    private readonly record struct Ifd(List<IfdEntry> Entries, uint NextIfd);
}
