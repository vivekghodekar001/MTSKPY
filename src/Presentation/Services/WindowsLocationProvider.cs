using System.Threading;
using System.Threading.Tasks;
using Mskpy.Infrastructure;

namespace Mskpy.Presentation.Services;

/// <summary>
/// Windows geolocator lookup for the location provider (P6).
///
/// The real Windows.Devices.Geolocation API requires WinRT contract references.
/// This implementation attempts the WinRT call when available; otherwise it
/// reports the geolocator as unavailable and the LocationProvider falls back
/// to record installation coordinates (locationMocked = "true").
/// locationMocked = "false" only when the Geolocator source actually succeeds.
/// </summary>
public sealed class WindowsLocationProvider
{
    private readonly TimeSpan _timeout;

    public WindowsLocationProvider(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<LocationResult?> GetLocationAsync(CancellationToken ct)
    {
        try
        {
            // Attempt WinRT Geolocator via reflection so the assembly does not
            // require Windows.Devices.Geolocation at compile time.
            var type = Type.GetType("Windows.Devices.Geolocation.Geolocator, Windows, ContentType=WindowsRuntime");
            if (type == null)
            {
                return new LocationResult
                {
                    Source = LocationSource.None,
                    Lat = null,
                    Long = null,
                    Accuracy = null,
                    Mocked = true,
                    Error = "Windows.Devices.Geolocation not available in this build."
                };
            }

            var geolocator = Activator.CreateInstance(type)!;
            var desiredAccuracy = type.GetProperty("DesiredAccuracyInMeters");
            desiredAccuracy?.SetValue(geolocator, 10);

            var getPosMethod = type.GetMethod("GetGeopositionAsync");
            var task = (Task<object?>)getPosMethod!.Invoke(geolocator, Array.Empty<object>())!;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            if (await Task.WhenAny(task, Task.Delay(-1, cts.Token)) != task)
                return new LocationResult { Source = LocationSource.None, Lat = null, Long = null, Accuracy = null, Mocked = true, Error = "Geolocator timed out after " + _timeout.TotalSeconds + "s." };

            var pos = await task;
            var coord = pos!.GetType().GetProperty("Coordinate")!.GetValue(pos)!;
            var lat = coord.GetType().GetProperty("Latitude")!.GetValue(coord);
            var lon = coord.GetType().GetProperty("Longitude")!.GetValue(coord);
            var acc = coord.GetType().GetProperty("Accuracy")!.GetValue(coord);

            return new LocationResult
            {
                Source = LocationSource.Geolocator,
                Lat = lat?.ToString(),
                Long = lon?.ToString(),
                Accuracy = acc?.ToString(),
                Mocked = false,
                Error = null
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new LocationResult
            {
                Source = LocationSource.None,
                Lat = null,
                Long = null,
                Accuracy = null,
                Mocked = true,
                Error = "Geolocator unavailable: " + ex.Message
            };
        }
    }
}
