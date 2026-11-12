using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Smithingway.Contracts;
using Smithingway.Models;
using Smithingway.Services;

namespace Smithingway.Sync;

public sealed class ServerSyncProvider : ISyncProvider
{
    private static readonly TimeSpan PublishDelay = TimeSpan.FromSeconds(1.5);

    private readonly Configuration config;
    private readonly RemoteCache remote;
    private readonly string serverUrl;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object publishGate = new();
    private CancellationTokenSource? pendingPublish;
    private volatile string status = "Waiting for the first sync…";
    private string? serverId;

    public ServerSyncProvider(Configuration config, RemoteCache remote, string serverUrl)
    {
        this.config = config;
        this.remote = remote;
        this.serverUrl = serverUrl.TrimEnd('/');
        http = new HttpClient { BaseAddress = new Uri(this.serverUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Smithingway/0.1");
        _ = Task.Run(PollChangesLoop);
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollOverlap = TimeSpan.FromSeconds(5);

    private async Task PollChangesLoop()
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var seen = new Dictionary<PlayerKey, DateTime>();
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var changes = await http.GetFromJsonAsync<ChangesResponse>(
                    $"v1/changes?since={Uri.EscapeDataString(since.ToString("O"))}", lifetime.Token);
                if (changes != null)
                {
                    foreach (var changed in changes.Changed)
                    {
                        var player = new PlayerKey(changed.Name, changed.WorldId);
                        if (seen.TryGetValue(player, out var last) && last >= changed.UpdatedUtc) continue;
                        seen[player] = changed.UpdatedUtc;
                        if (remote.GetReceivedTime(player) is { } received && changed.UpdatedUtc > received.AddSeconds(-30))
                            RequestProfile(player);
                    }
                    since = changes.ServerNowUtc - PollOverlap;
                    foreach (var stale in seen.Where(kv => kv.Value < since).Select(kv => kv.Key).ToList())
                        seen.Remove(stale);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Plugin.Log.Debug(ex, "[Smithingway] Change poll failed; will retry."); }

            try { await Task.Delay(PollInterval, lifetime.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public string DisplayName => $"Server ({new Uri(serverUrl).Authority})";
    public string Status => status;

    public void PublishLocalProfile(PlayerKey self, IReadOnlyDictionary<uint, ItemCustomization> items)
    {
        var snapshot = items.ToDictionary(kv => kv.Key, kv => new ItemEntry(
            kv.Value.Name, kv.Value.FlavorText, string.IsNullOrEmpty(kv.Value.NamedBy) ? null : kv.Value.NamedBy));

        CancellationTokenSource cts;
        lock (publishGate)
        {
            pendingPublish?.Cancel();
            cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            pendingPublish = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PublishDelay, cts.Token);
                await PublishAsync(self, snapshot, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail("Upload failed", ex); }
        });
    }

    public void RequestProfile(PlayerKey player)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await http.GetAsync(ProfilePath(player), lifetime.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    remote.SetProfile(player, new Dictionary<uint, ItemCustomization>());
                    return;
                }
                response.EnsureSuccessStatusCode();

                var profile = await response.Content.ReadFromJsonAsync<ProfileDto>(lifetime.Token);
                var items = (profile?.Items ?? new()).ToDictionary(
                    kv => kv.Key,
                    kv => new ItemCustomization
                    {
                        Name = kv.Value.Name ?? "",
                        FlavorText = kv.Value.FlavorText ?? "",
                        NamedBy = kv.Value.NamedBy ?? "",
                        UpdatedUtc = profile!.UpdatedUtc,
                    });
                remote.SetProfile(player, items);
                Plugin.Log.Debug("Received {Count} customization(s) for {Player}", items.Count, player.ToString());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail($"Lookup for {player.Name} failed", ex); }
        });
    }

    private async Task PublishAsync(PlayerKey self, Dictionary<uint, ItemEntry> items, CancellationToken ct)
    {
        var token = await EnsureTokenAsync(self, ct);
        if (token == null) return;

        using var request = new HttpRequestMessage(HttpMethod.Put, ProfilePath(self))
        {
            Content = JsonContent.Create(new ProfileDto(items, DateTime.UtcNow)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            status = "The server rejected this character's key. Another install may own this character on this server.";
            return;
        }
        response.EnsureSuccessStatusCode();
        status = $"Connected. Shared {items.Count} item(s) at {DateTime.Now:HH:mm}.";
    }

    private async Task<string?> EnsureTokenAsync(PlayerKey self, CancellationToken ct)
    {
        serverId ??= (await http.GetFromJsonAsync<HealthResponse>("health", ct))?.ServerId
                     ?? throw new InvalidOperationException("Server didn't report its ID.");
        var key = $"{serverId}|{self}";
        var existing = await Plugin.Framework.RunOnFrameworkThread(() => config.ServerTokens.GetValueOrDefault(key));
        if (!string.IsNullOrEmpty(existing)) return existing;

        using var response = await http.PostAsJsonAsync("v1/register", new RegisterRequest(self.Name, self.WorldId), ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            status = $"{self.Name} is already registered on this server by another install, so your names can't be shared from here.";
            return null;
        }
        response.EnsureSuccessStatusCode();

        var token = (await response.Content.ReadFromJsonAsync<RegisterResponse>(ct))?.Token;
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Server returned no token.");

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            config.ServerTokens[key] = token;
            config.Save();
        });
        return token;
    }

    private static string ProfilePath(PlayerKey player) => $"v1/profiles/{player.WorldId}/{Uri.EscapeDataString(player.Name)}";

    private void Fail(string what, Exception ex)
    {
        status = ex is HttpRequestException or TaskCanceledException
            ? $"{what}: can't reach {serverUrl}. Is the server running?"
            : $"{what}: {ex.Message}";
        Plugin.Log.Warning(ex, "[Smithingway] {What}", what);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        http.Dispose();
    }

    public static async Task<string> TestConnectionAsync(string serverUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(serverUrl.TrimEnd('/') + "/health");
            return response.IsSuccessStatusCode ? "Server reachable ✓" : $"Server answered {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            return $"Can't reach the server ({ex.GetBaseException().Message})";
        }
    }
}
