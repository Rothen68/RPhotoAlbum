using System.Text.Json.Serialization;

namespace RPhotoAlbum.Api.Media;

public class NominatimReverseResponse
{
    [JsonPropertyName("address")]
    public NominatimAddress? Address { get; set; }
}

// Nominatim ne renseigne que les niveaux administratifs pertinents pour l'endroit trouvé —
// "city" n'existe pas pour un lieu rural, d'où les alternatives town/village (voir doc API
// Reverse). La correspondance avec les découpages français (région/département) est
// approximative : dépend de la qualité des données OSM de la zone concernée.
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
