namespace RPhotoAlbum.Api.PCloud;

// Persisted pCloud connection (single row, single-user application).
// The token is encrypted at rest via Data Protection — see ARCHITECTURE.md §14.
public class PCloudConnection
{
    public int Id { get; set; }
    public required string Hostname { get; set; }
    public required string EncryptedAccessToken { get; set; }
    public DateTime ConnectedAt { get; set; }
}
