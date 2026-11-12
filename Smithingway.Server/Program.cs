using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Smithingway.Contracts;
using Smithingway.Server;
using Smithingway.Util;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://localhost:5280");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ApiLimits.MaxRequestBytes);

var dbPath = builder.Configuration["Database"] ?? Path.Combine(AppContext.BaseDirectory, "smithingway.db");
builder.Services.AddSingleton(new ProfileStore(dbPath));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("read", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("write", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseRateLimiter();
app.Logger.LogInformation("Database: {Path}", dbPath);

app.MapGet("/health", (ProfileStore store) => Results.Ok(new HealthResponse("ok", 1, store.ServerId)));

var v1 = app.MapGroup("/v1");

v1.MapPost("/register", (RegisterRequest request, ProfileStore store) =>
{
    if (!Validation.IsCharacter(request.Name, request.WorldId)) return Results.BadRequest(new { error = "invalid character" });
    var token = store.TryRegister(request.Name, request.WorldId);
    return token is null
        ? Results.Conflict(new { error = "This character is already registered by another install." })
        : Results.Ok(new RegisterResponse(token));
}).RequireRateLimiting("write");

v1.MapGet("/changes", (string? since, ProfileStore store) =>
{
    var now = DateTime.UtcNow;
    var from = DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
        ? parsed.ToUniversalTime()
        : now.AddMinutes(-5);
    if (from < now.AddDays(-2)) from = now.AddDays(-2);
    return Results.Ok(new ChangesResponse(now, store.ChangedSince(from)));
}).RequireRateLimiting("read");

v1.MapGet("/profiles/{worldId}/{name}", (uint worldId, string name, ProfileStore store) =>
{
    if (!Validation.IsCharacter(name, worldId)) return Results.BadRequest();
    return store.GetProfile(name, worldId) is { } profile ? Results.Ok(profile) : Results.NotFound();
}).RequireRateLimiting("read");

v1.MapPut("/profiles/{worldId}/{name}", (uint worldId, string name, ProfileDto body, HttpRequest http, ProfileStore store) =>
{
    if (!Validation.IsCharacter(name, worldId)) return Results.BadRequest();
    if (!store.CheckToken(name, worldId, BearerToken(http))) return Results.Unauthorized();

    var clean = new Dictionary<uint, ItemEntry>();
    foreach (var (itemId, entry) in body.Items ?? new())
    {
        if (clean.Count >= ApiLimits.MaxItemsPerProfile) break;
        if (itemId == 0 || itemId > ApiLimits.MaxItemId || entry is null) continue;
        var namedBy = Validation.IsCharacterName(entry.NamedBy) ? entry.NamedBy : null;
        var cleaned = new ItemEntry(TextSanitizer.CleanName(entry.Name), TextSanitizer.CleanFlavor(entry.FlavorText), namedBy);
        if (cleaned.Name.Length > 0 || cleaned.FlavorText.Length > 0) clean[itemId] = cleaned;
    }

    store.SaveProfile(name, worldId, clean);
    return Results.NoContent();
}).RequireRateLimiting("write");

v1.MapDelete("/profiles/{worldId}/{name}", (uint worldId, string name, HttpRequest http, ProfileStore store) =>
{
    if (!Validation.IsCharacter(name, worldId)) return Results.BadRequest();
    if (!store.CheckToken(name, worldId, BearerToken(http))) return Results.Unauthorized();
    store.DeleteProfile(name, worldId);
    return Results.NoContent();
}).RequireRateLimiting("write");

app.Run();

static string? BearerToken(HttpRequest http)
{
    var header = http.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
}

static string ClientKey(HttpContext ctx)
    => ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[^1].Trim()
       ?? ctx.Connection.RemoteIpAddress?.ToString()
       ?? "unknown";

static partial class Validation
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z'\-]{1,14} [A-Za-z][A-Za-z'\-]{1,14}$")]
    private static partial Regex CharacterName();

    public static bool IsCharacterName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= 21 && CharacterName().IsMatch(name);

    public static bool IsCharacter(string? name, uint worldId)
        => IsCharacterName(name) && worldId is > 0 and < 10_000;
}
