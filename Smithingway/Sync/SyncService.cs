using System;
using System.Collections.Generic;
using Smithingway.Models;
using Smithingway.Services;

namespace Smithingway.Sync;

public sealed class SyncService : IDisposable
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(10);

    private readonly Configuration config;
    private readonly CustomizationStore store;
    private readonly RemoteCache remote;
    private readonly Dictionary<PlayerKey, DateTime> lastRequested = new();

    public ISyncProvider Provider { get; private set; }

    public SyncService(Configuration config, CustomizationStore store, RemoteCache remote)
    {
        this.config = config;
        this.store = store;
        this.remote = remote;
        Provider = CreateProvider();
        store.LocalChanged += PublishLocal;
    }

    public void ApplySettings()
    {
        Provider.Dispose();
        Provider = CreateProvider();
        lastRequested.Clear();
        PublishLocal();
    }

    private ISyncProvider CreateProvider()
    {
        if (config.SyncEnabled && IsValidServerUrl(config.ServerUrl))
            return new ServerSyncProvider(config, remote, config.ServerUrl);
        return new LocalOnlySyncProvider();
    }

    public static bool IsValidServerUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public void PublishLocal()
    {
        if (store.LocalKey is not { } self || store.CurrentProfile is not { } profile) return;
        try { Provider.PublishLocalProfile(self, profile.Items); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Publishing customizations failed."); }
    }

    public void RefreshProfile(PlayerKey player)
    {
        if (!config.ShowOthersCustomizations || player == store.LocalKey) return;
        var now = DateTime.UtcNow;
        if (lastRequested.TryGetValue(player, out var asked) && now - asked < TimeSpan.FromSeconds(3)) return;
        lastRequested[player] = now;
        try { Provider.RequestProfile(player); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Refreshing customizations for {Player} failed.", player.ToString()); }
    }

    public void EnsureProfile(PlayerKey player)
    {
        if (!config.ShowOthersCustomizations || player == store.LocalKey) return;

        var now = DateTime.UtcNow;
        if (remote.GetReceivedTime(player) is { } received && now - received < RefreshAfter) return;
        if (lastRequested.TryGetValue(player, out var asked) && now - asked < RefreshAfter) return;

        lastRequested[player] = now;
        try { Provider.RequestProfile(player); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Requesting customizations for {Player} failed.", player.ToString()); }
    }

    public void Dispose()
    {
        store.LocalChanged -= PublishLocal;
        Provider.Dispose();
    }
}
