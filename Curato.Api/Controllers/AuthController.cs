using System.Security.Claims;
using Curato.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §15 — a single admin account, created on first run, session-cookie
/// auth thereafter. Password hashing uses ASP.NET Core Identity's
/// <see cref="PasswordHasher{TUser}"/> (PBKDF2, per-user salt, a proper work
/// factor) rather than a hand-rolled scheme — <see cref="UserRecord"/> is
/// just a marker type for it, not a real Identity user store.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(ServerDatabase database) : ControllerBase
{
    private static readonly PasswordHasher<UserRecord> Hasher = new();

    public sealed record StatusResponse(bool NeedsSetup, bool IsAuthenticated, string? Username);
    public sealed record CredentialsRequest(string Username, string Password);

    [HttpGet("status")]
    [AllowAnonymous]
    public ActionResult<StatusResponse> GetStatus()
    {
        var repository = new UserRepository(database);
        return Ok(new StatusResponse(
            NeedsSetup: !repository.HasAnyUser(),
            IsAuthenticated: User.Identity?.IsAuthenticated ?? false,
            Username: User.Identity?.Name));
    }

    /// <summary>
    /// Only works once — the moment any user exists, this always 409s. There
    /// is no re-running setup to add a second account (§15: single admin
    /// account, multi-user is a possible later extension, not this).
    /// </summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Setup([FromBody] CredentialsRequest request)
    {
        var repository = new UserRepository(database);
        if (repository.HasAnyUser())
        {
            return Conflict("An admin account already exists.");
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Username and password are required.");
        }

        if (request.Password.Length < 8)
        {
            return BadRequest("Password must be at least 8 characters.");
        }

        var placeholder = new UserRecord(0, request.Username, string.Empty);
        var hash = Hasher.HashPassword(placeholder, request.Password);
        repository.CreateUser(request.Username, hash);

        await SignInAsync(request.Username);
        return Ok();
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] CredentialsRequest request)
    {
        var repository = new UserRepository(database);
        var user = repository.FindByUsername(request.Username);
        if (user is null)
        {
            return Unauthorized();
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Unauthorized();
        }

        await SignInAsync(user.Username);
        return Ok();
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok();
    }

    private Task SignInAsync(string username)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, username)], CookieAuthenticationDefaults.AuthenticationScheme);
        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
