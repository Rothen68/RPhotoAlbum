namespace RPhotoAlbum.Api.Auth;

// Single-user application account — see ARCHITECTURE.md §5.2.
// Set via App__AdminUsername / App__AdminPasswordHash (Docker environment variables).
public class AppAuthOptions
{
    public string AdminUsername { get; set; } = "";
    public string AdminPasswordHash { get; set; } = "";
}
