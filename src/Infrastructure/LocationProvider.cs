using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Mskpy.Infrastructure;

/// <summary>
/// Where the position in a submission comes from.
///
/// C3: the Windows geolocator is gone from the resolution path. The
/// submission's position is the customer's site - the record's own
/// installation coordinates, jittered a few metres - or, when the record
/// carries no coordinates, what the officer types (Manual).
/// </summary>
public enum LocationSource { ManualOverride, SiteCoords, None }

public sealed class LocationResult
{
    public required LocationSource Source { get; init; }
    public required string? Lat { get; init; }
    public required string? Long { get; init; }
    public required string? Accuracy { get; init; }
    public required bool Mocked { get; init; }
    public required string? Error { get; init; }
    /// <summary>C3: the distance from the installation, in whole metres.</summary>
    public double? DistanceMetres { get; init; }
    /// <summary>C3: the jitter that was applied to the installation, in metres.</summary>
    public double? JitterMetres { get; init; }
    public bool HasCoords => !string.IsNullOrWhiteSpace(Lat) && !string.IsNullOrWhiteSpace(Long);
}

/// <summary>
/// Injected geolocator lookup. C3 removed the geolocator from the
/// resolution path: the submission is made from the customer's site, never
/// from the device's position. The delegate is kept so nothing that
/// referenced it stops compiling, but nothing calls it any more.
/// </summary>
public delegate Task<LocationResult?> GeolocatorLookup(CancellationToken ct);

/// <summary>
/// Great-circle distance between two WGS-84 points, in metres (P4).
/// Used for distanceFromInst: the distance between the position actually used
/// for the upload and the installation coordinates on the record.
/// </summary>
public static class GeoDistance
{
    public const double EarthRadiusMeters = 6371000d;

    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(a))) * EarthRadiusMeters;
    }
}

/// <summary>
/// C3: where a submission's position comes from.
///
/// 1. What the officer typed (C4) - for a record whose registered
///    coordinates are plainly wrong.
/// 2. The customer's site: the record's own installation coordinates,
///    jittered 3-15 m on a random bearing. The officer may be anywhere; the
///    submission is about the site.
/// 3. Nothing: the record carries no coordinates and there is NO device
///    fallback - the officer has to enter them.
///
/// There is no device position in this path at all.
/// </summary>
public sealed class LocationProvider
{
    public async Task<LocationResult> ResolveAsync(
        string? manualLat,
        string? manualLong,
        string? manualAccuracy,
        string? installLat,
        string? installLong,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // 1. What the officer typed.
        if (!string.IsNullOrWhiteSpace(manualLat) && !string.IsNullOrWhiteSpace(manualLong))
        {
            return new LocationResult
            {
                Source = LocationSource.ManualOverride,
                Lat = manualLat.Trim(),
                Long = manualLong.Trim(),
                Accuracy = string.IsNullOrWhiteSpace(manualAccuracy) ? null : manualAccuracy.Trim(),
                Mocked = false,
                DistanceMetres = DistanceToInstall(manualLat, manualLong, installLat, installLong),
                Error = null
            };
        }

        // 2. The customer's site.
        if (TryNumber(installLat, out var lat) && TryNumber(installLong, out var lon))
        {
            var metres = SitePoint.RandomJitterMetres();
            var bearing = SitePoint.RandomBearing();
            var (jitteredLat, jitteredLon) = SitePoint.Jitter(lat, lon, bearing, metres);
            // The pair is rounded to what the payload will carry, and the
            // distance is measured on THAT pair - so the number in the payload
            // and the number that says how far off the installation it is
            // describe the same point.
            var sentLat = SitePoint.Format(jitteredLat);
            var sentLon = SitePoint.Format(jitteredLon);
            var distance = GeoDistance.Meters(
                double.Parse(sentLat, CultureInfo.InvariantCulture),
                double.Parse(sentLon, CultureInfo.InvariantCulture), lat, lon);
            return new LocationResult
            {
                Source = LocationSource.SiteCoords,
                Lat = sentLat,
                Long = sentLon,
                Accuracy = SitePoint.AccuracyFor(metres).ToString("0", CultureInfo.InvariantCulture),
                Mocked = false,
                DistanceMetres = distance,
                JitterMetres = metres,
                Error = null
            };
        }

        // 3. Nothing to go on - the officer has to enter the coordinates.
        return new LocationResult
        {
            Source = LocationSource.None,
            Lat = null,
            Long = null,
            Accuracy = null,
            Mocked = false,
            DistanceMetres = null,
            Error = "This record carries no coordinates. Enter them by hand before submitting."
        };
    }

    static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    static double? DistanceToInstall(string lat, string lon, string? installLat, string? installLong)
    {
        if (!TryNumber(installLat, out var ilat) || !TryNumber(installLong, out var ilon)) return null;
        if (!TryNumber(lat, out var lat2) || !TryNumber(lon, out var lon2)) return null;
        return GeoDistance.Meters(lat2, lon2, ilat, ilon);
    }
}
