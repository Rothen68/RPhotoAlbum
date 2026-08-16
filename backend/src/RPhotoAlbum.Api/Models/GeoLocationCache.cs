namespace RPhotoAlbum.Api.Models;

// Cache de géocodage inverse (Nominatim), clé sur des coordonnées arrondies (~100 m) —
// indispensable pour respecter la limite de débit de Nominatim (4 req/min pour un usage
// récurrent) : des dizaines/centaines de photos prises au même endroit ne déclenchent qu'un
// seul appel. Voir GeoLookupService.
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
