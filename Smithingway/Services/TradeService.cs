using System;
using Dalamud.Game.Chat;
using Smithingway.Models;
using Smithingway.Sync;

namespace Smithingway.Services;

public sealed class TradeService : IDisposable
{
    private const uint TradeRequestSent = 32;
    private const uint AwaitingConfirmation = 33;
    private const uint TradeRequested = 34;
    private const uint TradeCanceled = 36;
    private const uint TradeComplete = 38;
    private const uint TradeReceivedItem = 10922;

    private static readonly TimeSpan PartnerLingers = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ReceiveWindow = TimeSpan.FromSeconds(3);

    private readonly Configuration config;
    private readonly CustomizationStore store;
    private readonly SyncService sync;

    private PlayerKey? partner;
    private DateTime? endedUtc;
    private int pendingReceives;
    private DateTime pendingSinceUtc;

    public TradeService(Configuration config, CustomizationStore store, SyncService sync)
    {
        this.config = config;
        this.store = store;
        this.sync = sync;
        Plugin.ChatGui.LogMessage += OnLogMessage;
    }

    public void Dispose() => Plugin.ChatGui.LogMessage -= OnLogMessage;

    public PlayerKey? CurrentPartner
        => partner is { } p && (endedUtc == null || DateTime.UtcNow - endedUtc < PartnerLingers) ? p : null;

    public bool TryConsumeReceivedItem(out PlayerKey from)
    {
        from = default;
        if (pendingReceives <= 0) return false;
        if (DateTime.UtcNow - pendingSinceUtc > ReceiveWindow || CurrentPartner is not { } p)
        {
            pendingReceives = 0;
            return false;
        }

        pendingReceives--;
        from = p;
        return true;
    }

    public void AdoptReceivedItem(PlayerKey from, uint itemId)
    {
        if (!config.KeepNamesFromTrades) return;

        var theirs = store.Lookup(from, isSelf: false, itemId);
        if (config.DebugLogging) TooltipService.DebugLine($"trade: received item {itemId} from {from}, their name: {theirs?.Name ?? "(none)"}");
        if (theirs == null || theirs.IsEmpty()) return;

        var author = string.IsNullOrEmpty(theirs.NamedBy) ? from.Name : theirs.NamedBy;
        if (!store.AdoptLocal(itemId, theirs, author)) return;

        var label = theirs.HasName() ? $"\"{theirs.Name}\"" : Util.ItemInfo.GetName(itemId);
        var byline = author == store.LocalKey?.Name ? "named by you" : $"named by {author}";
        Plugin.ChatGui.Print($"[Smithingway] {label} arrived with its name and story ({byline}).");
    }

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            switch (message.LogMessageId)
            {
                case TradeRequestSent:
                case AwaitingConfirmation:
                case TradeRequested:
                    if (FindPartner(message) is not { } found) return;
                    partner = found;
                    endedUtc = null;
                    pendingReceives = 0;
                    if (config.DebugLogging) TooltipService.DebugLine($"trade: partner {found} (log {message.LogMessageId})");
                    sync.RefreshProfile(found);
                    break;

                case TradeCanceled:
                case TradeComplete:
                    endedUtc = DateTime.UtcNow;
                    break;

                case TradeReceivedItem:
                    pendingReceives++;
                    pendingSinceUtc = DateTime.UtcNow;
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to handle trade log message.");
        }
    }

    private PlayerKey? FindPartner(ILogMessage message)
    {
        foreach (var entity in new[] { message.SourceEntity, message.TargetEntity })
        {
            if (entity is not { IsPlayer: true }) continue;
            var name = entity.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name) || entity.HomeWorldId == 0) continue;
            var key = new PlayerKey(name, entity.HomeWorldId);
            if (key != store.LocalKey) return key;
        }
        return null;
    }
}
