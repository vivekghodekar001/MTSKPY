using System.Globalization;

namespace Mskpy.Infrastructure;

/// <summary>
/// C3: the position a survey is submitted from.
///
/// THE INTENT, so that it is not lost: the officer may be anywhere - at
/// home, in the office, in another district - but the coordinates in the
/// submission are always the CUSTOMER'S SITE. Never the device's position.
/// The Windows geolocator is not in the resolution path at all.
///
/// So the submission takes the record's own installation coordinates and
/// jitters them a few metres on a random bearing: the pair differs from
/// the stored pair (a photo's EXIF and the payload cannot be the same
/// number twice), and the distance from the installation is a small real
/// number instead of exactly zero.
/// </summary>
public static class SitePoint
{
    /// <summary>Metres in a degree of latitude.</summary>
    public const double MetresPerDegreeLatitude = 111320.0;

    /// <summary>The smallest jitter, in metres.</summary>
    public const double MinJitterMetres = 3.0;

    /// <summary>The largest jitter, in metres.</summary>
    public const double MaxJitterMetres = 15.0;

    /// <summary>The accuracy reported for the smallest jitter.</summary>
    public const double MinAccuracy = 4.0;

    /// <summary>The accuracy reported for the largest jitter.</summary>
    public const double MaxAccuracy = 12.0;

    /// <summary>
    /// The accuracy that goes with a jitter: the larger the jitter, the
    /// larger the accuracy, from 4 m to 12 m.
    /// </summary>
    public static double AccuracyFor(double metres)
    {
        var span = MaxJitterMetres - MinJitterMetres;
        if (span <= 0) return MinAccuracy;
        var t = (metres - MinJitterMetres) / span;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return MinAccuracy + t * (MaxAccuracy - MinAccuracy);
    }

    /// <summary>
    /// A point <paramref name="metres"/> away from the installation, on the
    /// given bearing (radians, clockwise from north).
    /// </summary>
    public static (double Lat, double Long) Jitter(double lat, double lon, double bearingRadians, double metres)
    {
        // Northings move the latitude; eastings move the longitude, and a
        // degree of longitude is shorter than a degree of latitude away from
        // the equator - hence the cosine.
        var north = Math.Cos(bearingRadians) * metres;
        var east = Math.Sin(bearingRadians) * metres;
        var newLat = lat + north / MetresPerDegreeLatitude;
        var cosLat = Math.Cos(lat * Math.PI / 180.0);
        if (Math.Abs(cosLat) < 1e-9) cosLat = 1e-9;
        var newLon = lon + east / (MetresPerDegreeLatitude * cosLat);
        return (newLat, newLon);
    }

    /// <summary>A random bearing, in radians.</summary>
    public static double RandomBearing() => Random.Shared.NextDouble() * 2 * Math.PI;

    /// <summary>A random jitter magnitude, between 3 and 15 metres.</summary>
    public static double RandomJitterMetres() =>
        MinJitterMetres + Random.Shared.NextDouble() * (MaxJitterMetres - MinJitterMetres);

    /// <summary>Formats a coordinate the way the payload carries it.</summary>
    public static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
