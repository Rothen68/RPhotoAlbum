using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RPhotoAlbum.Api.Auth;

namespace RPhotoAlbum.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IOptions<AppAuthOptions> authOptions) : ControllerBase
{
    private static readonly PasswordHasher<object> Hasher = new();

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var options = authOptions.Value;
        if (string.IsNullOrEmpty(options.AdminUsername) || string.IsNullOrEmpty(options.AdminPasswordHash))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Authentification non configurée (App__AdminUsername / App__AdminPasswordHash)." });
        }

        var validUsername = string.Equals(request.Username, options.AdminUsername, StringComparison.Ordinal);
        var verification = Hasher.VerifyHashedPassword(new object(), options.AdminPasswordHash, request.Password);

        if (!validUsername || verification == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { error = "Identifiants invalides." });
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, options.AdminUsername)],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return Ok(new { username = options.AdminUsername });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok();
    }

    [HttpGet("me")]
    public IActionResult Me() => Ok(new { username = User.Identity?.Name });
}
