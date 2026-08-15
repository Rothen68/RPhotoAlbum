namespace RPhotoAlbum.Api.PCloud;

// pCloud renvoie normalement `hostname` directement lors du callback OAuth ;
// ce repli couvre le cas où seul `locationid` serait fourni — voir ARCHITECTURE.md §5.3.
public static class PCloudHostnameResolver
{
    public static string Resolve(string? hostname, int? locationId)
    {
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            return hostname;
        }

        return locationId == 2 ? "eapi.pcloud.com" : "api.pcloud.com";
    }
}
