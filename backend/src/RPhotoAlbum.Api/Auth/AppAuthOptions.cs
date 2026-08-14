namespace RPhotoAlbum.Api.Auth;

// Compte applicatif mono-utilisateur — voir ARCHITECTURE.md §5.2.
// Renseigné via App__AdminUsername / App__AdminPasswordHash (variables d'environnement Docker).
public class AppAuthOptions
{
    public string AdminUsername { get; set; } = "";
    public string AdminPasswordHash { get; set; } = "";
}
