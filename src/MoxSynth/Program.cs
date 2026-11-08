using Dapper;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using MoxSynth.Cards;
using MoxSynth.Collection;
using MoxSynth.Components;
using MoxSynth.Infrastructure;
using MoxSynth.Profiles;

// Map snake_case columns (collection db) onto PascalCase properties.
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                ?? new DatabaseOptions();
var paths = DatabasePaths.Resolve(dbOptions, builder.Environment.ContentRootPath);

builder.Services.AddSingleton(paths);

// Profile sign-in: encrypted cookie (contains profile id + password stamp). Keys are kept in App_Data/keys so
// sign-ins survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("MoxSynth")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(paths.ProfilesPath)!, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "moxsynth-auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/"; // the profile picker
    });
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<CardRecognizer>();
builder.Services.AddSingleton<CardIndex>();
builder.Services.AddSingleton<ArchidektImporter>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddScoped<CurrentProfile>();
builder.Services.AddScoped<CollectionRepository>(); // per circuit: uses the current profile's database

var app = builder.Build();

// Card database (read-only, MTGJSON AllPrintings) – extract from zip on first run if needed.
CardDatabaseBootstrapper.EnsureAvailable(paths, app.Logger);

// Profiles + their collection databases – create / upgrade schemas.
app.Services.GetRequiredService<ProfileStore>().Initialize();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAuthentication();
app.UseAntiforgery();

app.MapStaticAssets();

// Sign in / out / passwords (form posts; they set the encrypted authentication cookie).
app.MapProfileEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
