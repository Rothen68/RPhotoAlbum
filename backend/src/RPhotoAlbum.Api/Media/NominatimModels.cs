using System.Text.Json.Serialization;

namespace RPhotoAlbum.Api.Media;

public class NominatimReverseResponse
{
    [JsonPropertyName("address")]
    public NominatimAddress? Address { get; set; }
}

// Nominatim only fills in the administrative levels relevant to the place found —
// "city" doesn't exist for a rural location, hence the town/village fallbacks (see the Reverse
// API docs). The mapping to French administrative divisions (region/county) is
// approximate: it depends on the quality of the OSM data for the area concerned.
public class NominatimAddress
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("county")]
    public string? County { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("town")]
    public string? Town { get; set; }

    [JsonPropertyName("village")]
    public string? Village { get; set; }
}
