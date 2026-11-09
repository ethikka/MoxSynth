using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace MoxSynth.Profiles;

/// <summary>
/// Sign-in / sign-out / password endpoints. They are plain HTML form posts (with antiforgery token) because only
/// an HTTP response can set the authentication cookie; after each one the browser reloads and a new Blazor circuit
/// starts for the signed-in profile.
/// </summary>
public static class ProfileEndpoints
{
    public const string StampClaim = "moxsynth:password-stamp";

    /// <summary>The signed-in profile, or null when not signed in or the password changed since signing in.</summary>
    public static Profile? GetSignedInProfile(HttpContext? http, ProfileStore store) => GetProfile(http?.User, store);

    /// <summary>
    /// The profile of a cookie / bearer-token principal, or null when it isn't authenticated, the profile is gone,
    /// or its password changed since the principal was issued.
    /// </summary>
    public static Profile? GetProfile(ClaimsPrincipal? user, ProfileStore store)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;
        if (!long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;

        var profile = store.Get(id);
        return profile is { HasPassword: true } && profile.PasswordStamp == user.FindFirstValue(StampClaim) ? profile : null;
    }

    /// <summary>Claims of a signed-in profile (cookie and API tokens).</summary>
    public static ClaimsPrincipal CreatePrincipal(Profile profile, string authenticationType) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, profile.Id.ToString()),
            new Claim(ClaimTypes.Name, profile.Name),
            new Claim(StampClaim, profile.PasswordStamp ?? ""),
        ], authenticationType));

    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/profile");

        // Sign in with a password.
        group.MapPost("/login", async ([FromForm] long id, [FromForm] string? password, [FromForm] string? returnUrl,
            ProfileStore store, HttpContext http) =>
        {
            switch (store.VerifyPassword(id, password))
            {
                case LoginResult.Ok:
                    await SignInAsync(http, store.Get(id)!);
                    return Results.LocalRedirect(SafeReturnUrl(returnUrl));
                case LoginResult.NoPassword:
                    return Results.LocalRedirect($"/?profile={id}");
                case LoginResult.LockedOut:
                    return Results.LocalRedirect($"/?profile={id}&error={ProfileErrors.LockedOut}");
                case LoginResult.NotFound:
                    return Results.LocalRedirect("/");
                default:
                    await Task.Delay(400); // slow down guessing a little
                    return Results.LocalRedirect($"/?profile={id}&error={ProfileErrors.WrongPassword}");
            }
        });

        // First start: create the first (admin) profile and sign in.
        group.MapPost("/setup", async ([FromForm] string? name, [FromForm] string? color, [FromForm] string? password,
            [FromForm] string? confirm, ProfileStore store, HttpContext http) =>
        {
            if (store.Any()) return Results.LocalRedirect($"/?error={ProfileErrors.NotAllowed}");
            try
            {
                var profile = store.Create(name ?? "", ValidColor(color), isAdmin: true, password, confirm);
                await SignInAsync(http, profile);
                return Results.LocalRedirect("/");
            }
            catch (ProfileException ex)
            {
                return Results.LocalRedirect($"/?error={ex.Code}");
            }
        });

        // A profile without password (adopted from before passwords existed) gets one on first use.
        group.MapPost("/set-password", async ([FromForm] long id, [FromForm] string? password, [FromForm] string? confirm,
            [FromForm] string? returnUrl, ProfileStore store, HttpContext http) =>
        {
            var existing = store.Get(id);
            if (existing is null || existing.HasPassword)
                return Results.LocalRedirect($"/?profile={id}&error={ProfileErrors.NotAllowed}");
            try
            {
                var profile = store.SetPassword(id, password, confirm);
                await SignInAsync(http, profile);
                return Results.LocalRedirect(SafeReturnUrl(returnUrl));
            }
            catch (ProfileException ex)
            {
                return Results.LocalRedirect($"/?profile={id}&error={ex.Code}");
            }
        });

        // Change your own password (requires the current one); stays signed in with the new password.
        group.MapPost("/change-password", async ([FromForm] string? current, [FromForm] string? password,
            [FromForm] string? confirm, ProfileStore store, HttpContext http) =>
        {
            var me = GetSignedInProfile(http, store);
            if (me is null) return Results.LocalRedirect("/");

            var check = store.VerifyPassword(me.Id, current);
            if (check != LoginResult.Ok)
            {
                var code = check == LoginResult.LockedOut ? ProfileErrors.LockedOut : ProfileErrors.WrongPassword;
                return Results.LocalRedirect($"/account?error={code}");
            }
            try
            {
                var updated = store.SetPassword(me.Id, password, confirm);
                await SignInAsync(http, updated);
                return Results.LocalRedirect("/account?changed=1");
            }
            catch (ProfileException ex)
            {
                return Results.LocalRedirect($"/account?error={ex.Code}");
            }
        });

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/");
        });
    }

    private static Task SignInAsync(HttpContext http, Profile profile) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(profile, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = true });

    private static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";

    private static string ValidColor(string? color) =>
        color is not null && ProfileColors.All.Contains(color) ? color : ProfileColors.All[3];
}
