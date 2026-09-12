namespace RPhotoAlbum.Api.Models;

// Reverse geocoding cache (Nominatim), keyed on rounded coordinates (~100 m) —
// essential to respect Nominatim's rate limit (4 req/min for recurring use):
// dozens/hundreds of photos taken at the same spot trigger only a
// single call. See GeoLookupService.
public class GeoLocationCache
{
    public int Id { get; set; }
    public double RoundedLatitude { get; set; }
    public double RoundedLongitude { get; set; }
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? County { get; set; }
    public string? City { get; set; }
    public DateTime ResolvedAt { get; set; }
}
