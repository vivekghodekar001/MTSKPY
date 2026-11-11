using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mskpy.Infrastructure;

/// <summary>
/// Location source priority (P6/P7):
///   1. Per-record manual override (user-supplied lat/long)
///   2. Windows Geolocator (10s timeout) — locationMocked = "false"
///   3. Record installation coordinates — locationMocked = "true"
///
/// Refuse POST only when all three are empty. Reports which source was used.
///
/// The geolocator lookup is injected via <see cref="Geolocator"/> delegate so the
/// Infrastructure project (net10.0) does not depend on Windows.Devices.Geolocation;
/// the Presentation layer (net10.0-windows) supplies the Windows implementation.
/// </summary>
public enum LocationSource { ManualOverride, Geolocator, InstallationCoords, None }

public sealed class LocationResult
{
    public required LocationSource Source { get; init; }
    public required string? Lat { get; init; }
    public required string? Long { get; init; }
    public required string? Accuracy { get; init; }
    public required bool Mocked { get; init; }
    public required string? Error { get; init; }
    public bool HasCoords => !string.IsNullOrWhiteSpace(Lat) && !string.IsNullOrWhiteSpace(Long);
}

/// <summary>Injected geolocator lookup, supplied by the Windows Presentation layer.</summary>
public delegate Task<LocationResult?> GeolocatorLookup(CancellationToken ct);

/// <summary>
/// Great-circle distance between two WGS-84 points, in metres (P4).
/// Used for distanceFromInst: the distance between the position actually used
/// for the upload and the installation coordinates on the record.
/// </summary>
public static class GeoDistance
{
    private const double EarthRadiusMeters = 6371000d;

    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        const double toRadians = Math.PI / 180d;
        var dLat = (lat2 - lat1) * toRadians;
        var dLon = (lon2 - lon1) * toRadians;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * toRadians) * Math.Cos(lat2 * toRadians) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1d, Math.Sqrt(a)));
    }
}

public sealed class LocationProvider
{
    /// <summary>10 second timeout for the Windows Geolocator (P6).</summary>
    public TimeSpan GeolocatorTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Injected Windows Geolocator lookup (null = not available).</summary>
    public GeolocatorLookup? Geolocator { get; set; }

    public async Task<LocationResult> ResolveAsync(
        string? manualLat,
        string? manualLong,
        string? manualAccuracy,
        string? installLat,
        string? installLong,
        CancellationToken ct = default)
    {
        // 1. Per-record manual override — highest priority.
        // S5: manual override is officer-supplied, not mocked.
        if (!string.IsNullOrWhiteSpace(manualLat) && !string.IsNullOrWhiteSpace(manualLong))
        {
            return new LocationResult
            {
                Source = LocationSource.ManualOverride,
                Lat = manualLat.Trim(),
                Long = manualLong.Trim(),
                Accuracy = string.IsNullOrWhiteSpace(manualAccuracy) ? null : manualAccuracy.Trim(),
                Mocked = false,
                Error = null
            };
        }

        // 2. Windows Geolocator (10s timeout) — locationMocked = "false".
        if (Geolocator != null)
        {
            try
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(GeolocatorTimeout);
                var geo = await Geolocator(cts.Token);
                if (geo is not null && geo.HasCoords)
                {
                    return new LocationResult
                    {
                        Source = LocationSource.Geolocator,
                        Lat = geo.Lat,
                        Long = geo.Long,
                        Accuracy = geo.Accuracy,
                        Mocked = false,
                        Error = null
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Geolocator failed — fall through to installation coords.
            }
        }

        // 3. Record installation coordinates — locationMocked = "true".
        if (!string.IsNullOrWhiteSpace(installLat) && !string.IsNullOrWhiteSpace(installLong))
        {
            return new LocationResult
            {
                Source = LocationSource.InstallationCoords,
                Lat = installLat.Trim(),
                Long = installLong.Trim(),
                Accuracy = null,
                Mocked = true,
                Error = null
            };
        }

        return new LocationResult
        {
            Source = LocationSource.None,
            Lat = null,
            Long = null,
            Accuracy = null,
            Mocked = true,
            Error = "No location source available: manual override, Geolocator, and installation coordinates are all empty."
        };
    }
}
