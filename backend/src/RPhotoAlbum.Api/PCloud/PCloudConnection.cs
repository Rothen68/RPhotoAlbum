namespace RPhotoAlbum.Api.PCloud;

// Connexion pCloud persistée (ligne unique, application mono-utilisateur).
// Le jeton est chiffré au repos via Data Protection — voir ARCHITECTURE.md §14.
public class PCloudConnection
{
    public int Id { get; set; }
    public required string Hostname { get; set; }
    public required string EncryptedAccessToken { get; set; }
    public DateTime ConnectedAt { get; set; }
}
