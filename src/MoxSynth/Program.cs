using Dapper;
using MoxSynth.Cards;
using MoxSynth.Collection;
using MoxSynth.Components;
using MoxSynth.Infrastructure;

// Map snake_case columns (collection db) onto PascalCase properties.
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                ?? new DatabaseOptions();
var paths = DatabasePaths.Resolve(dbOptions, builder.Environment.ContentRootPath);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<CardRecognizer>();
builder.Services.AddSingleton<CollectionRepository>();

var app = builder.Build();

// Card database (read-only, MTGJSON AllPrintings) – extract from zip on first run if needed.
CardDatabaseBootstrapper.EnsureAvailable(paths, app.Logger);

// Collection database (read-write) – create / upgrade schema.
CollectionSchema.Migrate(paths, app.Logger);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
