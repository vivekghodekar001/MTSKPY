using System.Globalization;
using System.Text;
using Mskpy.Infrastructure;

// Usage:
//   Exif selftest
//   Exif <input.jpg> <output.jpg> <lat> <lon> <yyyy-MM-ddTHH:mm:ssZ>
if (args.Length == 1 && args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase))
    return SelfTest();

if (args.Length < 5)
{
    Console.Error.WriteLine("usage: Exif selftest");
    Console.Error.WriteLine("       Exif <input.jpg> <output.jpg> <lat> <lon> <iso-utc>");
    return 2;
}

var latitude = double.Parse(args[2], CultureInfo.InvariantCulture);
var longitude = double.Parse(args[3], CultureInfo.InvariantCulture);
var timestamp = DateTime.Parse(args[4], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

var source = File.ReadAllBytes(args[0]);
var stamped = JpegExifStamper.StampJpeg(source, latitude, longitude, timestamp);
File.WriteAllBytes(args[1], stamped);

Console.WriteLine($"bytes: {source.Length} -> {stamped.Length}");
Console.WriteLine("segments: " + string.Join(" ", SegmentMarkers(stamped)));
Report(stamped);
return 0;

static void Report(byte[] jpeg)
{
    var exif = ExifReader.Read(jpeg);
    Console.WriteLine($"Make: {exif.Make ?? "(none)"}");
    Console.WriteLine($"Model: {exif.Model ?? "(none)"}");
    Console.WriteLine($"Software: {exif.Software ?? "(none)"}");
    Console.WriteLine($"Orientation: {exif.Orientation?.ToString() ?? "(none)"}");
    Console.WriteLine($"DateTimeOriginal: {exif.DateTimeOriginal ?? "(none)"}");
    Console.WriteLine($"GPS: {(exif.GpsLatitude.HasValue ? exif.GpsLatitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "(none)")},{(exif.GpsLongitude.HasValue ? exif.GpsLongitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "(none)")}");
}

static int SelfTest()
{
    var failures = 0;
    void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
        if (!ok) failures++;
    }

    // A structurally valid JPEG with APP0 but no EXIF APP1: this is the exact
    // shape that exposed the APP1 insertion-offset bug.
    var plain = MinimalJpeg();
    var stamped = JpegExifStamper.StampJpeg(plain, 19.276093, 74.390961, new DateTime(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc));

    var markers = SegmentMarkers(stamped).ToList();
    Check(markers.Count >= 4, "stamped JPEG has a full segment chain");
    Check(markers.Count(x => x == "FFE1") == 1, "exactly one APP1 segment");
    Check(markers.IndexOf("FFE1") == 1 && markers[0] == "FFE0", "APP1 follows APP0");
    Check(WellFormed(stamped), "every segment length lands on the next marker");
    Check(App1HasExifHeader(stamped), "APP1 payload starts with Exif\\0\\0");

    var first = ExifReader.Read(stamped);
    Check(Math.Abs((first.GpsLatitude ?? 0) - 19.276093) < 1e-5, "GPS latitude matches");
    Check(Math.Abs((first.GpsLongitude ?? 0) - 74.390961) < 1e-5, "GPS longitude matches");
    Check(first.DateTimeOriginal == "2026:11:10 06:30:00", "DateTimeOriginal written");

    // Re-stamping exercises the merge path and must not accumulate APP1 segments.
    var restamped = JpegExifStamper.StampJpeg(stamped, -33.8688, 151.2093, new DateTime(2026, 11, 10, 7, 0, 0, DateTimeKind.Utc));
    var second = SegmentMarkers(restamped).ToList();
    Check(second.Count(x => x == "FFE1") == 1, "re-stamp still has exactly one APP1");
    Check(WellFormed(restamped), "re-stamped segments remain well formed");
    var merged = ExifReader.Read(restamped);
    Check(Math.Abs((merged.GpsLatitude ?? 0) + 33.8688) < 1e-5, "re-stamp replaces GPS latitude (S)");
    Check(Math.Abs((merged.GpsLongitude ?? 0) - 151.2093) < 1e-5, "re-stamp replaces GPS longitude (E)");
    Check(merged.DateTimeOriginal == "2026:11:10 07:00:00", "re-stamp replaces DateTimeOriginal");

    Console.WriteLine(failures == 0 ? "RESULT=success" : $"RESULT=failure ({failures})");
    return failures == 0 ? 0 : 1;
}

static byte[] MinimalJpeg()
{
    var bytes = new List<byte>();
    bytes.AddRange(new byte[] { 0xFF, 0xD8 }); // SOI
    // APP0 JFIF, length 16.
    bytes.AddRange(new byte[]
    {
        0xFF, 0xE0, 0x00, 0x10,
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
        0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00
    });
    // DQT, length 67 (1 byte precision/id + 64 coefficients).
    bytes.AddRange(new byte[] { 0xFF, 0xDB, 0x00, 0x43, 0x00 });
    bytes.AddRange(new byte[64]);
    // SOF0, length 11, 8x8, one component.
    bytes.AddRange(new byte[]
    {
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08, 0x01, 0x01, 0x11, 0x00
    });
    // SOS, length 8, one component.
    bytes.AddRange(new byte[]
    {
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00
    });
    bytes.AddRange(new byte[] { 0x00, 0x00 }); // scan data
    bytes.AddRange(new byte[] { 0xFF, 0xD9 }); // EOI
    return bytes.ToArray();
}

static bool WellFormed(byte[] jpeg)
{
    var pos = 2;
    while (pos + 4 <= jpeg.Length)
    {
        if (jpeg[pos] != 0xFF) return false;
        var marker = jpeg[pos + 1];
        if (marker is 0xD8 or 0xD9 or 0xDA) return true;
        var length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
        if (length < 2) return false;
        var next = pos + 2 + length;
        if (next > jpeg.Length) return false;
        pos = next;
    }
    return false;
}

static bool App1HasExifHeader(byte[] jpeg)
{
    var pos = 2;
    while (pos + 4 <= jpeg.Length)
    {
        if (jpeg[pos] != 0xFF) return false;
        var marker = jpeg[pos + 1];
        if (marker is 0xD8 or 0xD9 or 0xDA) return false;
        var length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
        if (length < 2) return false;
        if (marker == 0xE1 && length >= 8 &&
            jpeg[pos + 4] == 'E' && jpeg[pos + 5] == 'x' && jpeg[pos + 6] == 'i' &&
            jpeg[pos + 7] == 'f' && jpeg[pos + 8] == 0 && jpeg[pos + 9] == 0)
            return true;
        pos += 2 + length;
    }
    return false;
}

static IEnumerable<string> SegmentMarkers(byte[] jpeg)
{
    var pos = 2;
    while (pos + 4 <= jpeg.Length)
    {
        if (jpeg[pos] != 0xFF) yield break;
        var marker = jpeg[pos + 1];
        yield return $"FF{marker:X2}";
        if (marker is 0xD8 or 0xD9 or 0xDA) yield break;
        var length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
        if (length < 2) yield break;
        pos += 2 + length;
    }
}

internal sealed record ExifValues(
    string? Make,
    string? Model,
    string? Software,
    int? Orientation,
    string? DateTimeOriginal,
    double? GpsLatitude,
    double? GpsLongitude);

internal static class ExifReader
{
    public static ExifValues Read(byte[] jpeg)
    {
        var (offset, length) = FindExifApp1(jpeg);
        if (offset < 0) return new ExifValues(null, null, null, null, null, null, null);

        var tiff = new byte[length - 2 - 6];
        Buffer.BlockCopy(jpeg, offset + 4 + 6, tiff, 0, tiff.Length);
        var little = tiff[0] == 0x49;
        var ifd0Offset = ReadU32(tiff, 4, little);
        var ifd0 = ReadIfd(tiff, ifd0Offset, little);

        var make = Ascii(tiff, ifd0, 0x010F, little);
        var model = Ascii(tiff, ifd0, 0x0110, little);
        var software = Ascii(tiff, ifd0, 0x0131, little);
        var orientation = ifd0.TryGetValue(0x0112, out var o) ? (int?)ReadScalar(tiff, o, little) : null;

        var exifPointer = ifd0.TryGetValue(0x8769, out var ep) ? ep.ValueOrOffset : 0u;
        var exifIfd = exifPointer >= 8 ? ReadIfd(tiff, exifPointer, little) : new Dictionary<ushort, Entry>();
        var dateTime = Ascii(tiff, exifIfd, 0x9003, little);

        double? gpsLat = null, gpsLon = null;
        // GPSInfoIFDPointer lives in IFD0 per the EXIF spec.
        var gpsPointer = ifd0.TryGetValue(0x8825, out var gp) ? gp.ValueOrOffset : 0u;
        if (gpsPointer >= 8)
        {
            var gps = ReadIfd(tiff, gpsPointer, little);
            gpsLat = RationalsToDegrees(tiff, gps, 0x0002, little);
            gpsLon = RationalsToDegrees(tiff, gps, 0x0004, little);
            if (gps.TryGetValue(0x0001, out var latRef) && AsciiRef(latRef) == "S" && gpsLat.HasValue) gpsLat = -gpsLat;
            if (gps.TryGetValue(0x0003, out var lonRef) && AsciiRef(lonRef) == "W" && gpsLon.HasValue) gpsLon = -gpsLon;
        }

        return new ExifValues(make, model, software, orientation, dateTime, gpsLat, gpsLon);
    }

    private static string AsciiRef(Entry entry) => ((char)entry.Inline[0]).ToString();

    private static (int Offset, int Length) FindExifApp1(byte[] jpeg)
    {
        var pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) break;
            var marker = jpeg[pos + 1];
            if (marker is 0xD8 or 0xD9 or 0xDA) break;
            var length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (length < 2 || pos + 2 + length > jpeg.Length) break;
            if (marker == 0xE1 && length >= 8 &&
                jpeg[pos + 4] == 'E' && jpeg[pos + 5] == 'x' && jpeg[pos + 6] == 'i' &&
                jpeg[pos + 7] == 'f' && jpeg[pos + 8] == 0 && jpeg[pos + 9] == 0)
                return (pos, length);
            pos += 2 + length;
        }
        return (-1, 0);
    }

    private static Dictionary<ushort, Entry> ReadIfd(byte[] tiff, uint offset, bool little)
    {
        var entries = new Dictionary<ushort, Entry>();
        if (offset + 2 > tiff.Length) return entries;
        var count = ReadU16(tiff, (int)offset, little);
        var pos = (int)offset + 2;
        for (var i = 0; i < count && pos + 12 <= tiff.Length; i++, pos += 12)
        {
            var tag = ReadU16(tiff, pos, little);
            entries[tag] = new Entry(
                tag,
                ReadU16(tiff, pos + 2, little),
                ReadU32(tiff, pos + 4, little),
                ReadU32(tiff, pos + 8, little),
                tiff.AsSpan(pos + 8, 4).ToArray());
        }
        return entries;
    }

    private static string? Ascii(byte[] tiff, Dictionary<ushort, Entry> ifd, ushort tag, bool little)
    {
        if (!ifd.TryGetValue(tag, out var entry)) return null;
        var size = (int)entry.Count;
        if (size <= 0) return null;
        byte[] data;
        if (size <= 4)
        {
            data = entry.Inline;
        }
        else
        {
            if (entry.ValueOrOffset + size > tiff.Length) return null;
            data = tiff.AsSpan((int)entry.ValueOrOffset, size).ToArray();
        }
        var end = Array.IndexOf(data, (byte)0);
        if (end < 0) end = data.Length;
        return Encoding.ASCII.GetString(data, 0, end);
    }

    private static double? RationalsToDegrees(byte[] tiff, Dictionary<ushort, Entry> ifd, ushort tag, bool little)
    {
        if (!ifd.TryGetValue(tag, out var entry) || entry.Count < 3) return null;
        var offset = (int)entry.ValueOrOffset;
        if (offset + 24 > tiff.Length) return null;
        var degrees = Rational(tiff, offset, little);
        var minutes = Rational(tiff, offset + 8, little);
        var seconds = Rational(tiff, offset + 16, little);
        return degrees + minutes / 60.0 + seconds / 3600.0;
    }

    private static double Rational(byte[] tiff, int offset, bool little)
    {
        var numerator = ReadU32(tiff, offset, little);
        var denominator = ReadU32(tiff, offset + 4, little);
        return denominator == 0 ? 0 : (double)numerator / denominator;
    }

    private static uint ReadScalar(byte[] tiff, Entry entry, bool little)
    {
        // Inline values are left-justified in the 4-byte field regardless of endianness.
        if (entry.Count <= 4)
        {
            if (entry.Type == 3) // SHORT
                return little
                    ? (uint)(entry.Inline[0] | (entry.Inline[1] << 8))
                    : (uint)((entry.Inline[0] << 8) | entry.Inline[1]);
            if (entry.Type == 4) // LONG
                return little
                    ? (uint)(entry.Inline[0] | (entry.Inline[1] << 8) | (entry.Inline[2] << 16) | (entry.Inline[3] << 24))
                    : (uint)((entry.Inline[0] << 24) | (entry.Inline[1] << 16) | (entry.Inline[2] << 8) | entry.Inline[3]);
        }
        return entry.ValueOrOffset;
    }

    private static ushort ReadU16(byte[] b, int o, bool little) => little
        ? (ushort)(b[o] | (b[o + 1] << 8))
        : (ushort)((b[o] << 8) | b[o + 1]);

    private static uint ReadU32(byte[] b, int o, bool little) => little
        ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24))
        : (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset, byte[] Inline);
}
