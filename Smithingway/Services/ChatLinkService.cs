using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using Smithingway.Models;
using Smithingway.Sync;
using Smithingway.Util;

namespace Smithingway.Services;

public sealed class ChatLinkService : IDisposable
{
    public readonly record struct LinkSource(PlayerKey? Sender, bool IsSelf, DateTime SeenUtc, string DisplayText);

    private const int MaxLinksPerItem = 20;

    private const int LinkTextSearchWindow = 8;

    private static readonly HashSet<XivChatType> PrefetchChannels =
    [
        XivChatType.Party, XivChatType.CrossParty, XivChatType.Alliance, XivChatType.FreeCompany, XivChatType.TellIncoming,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4, XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    ];
    private static readonly TimeSpan LinkMemory = TimeSpan.FromHours(2);

    private readonly Configuration config;
    private readonly CustomizationStore store;
    private readonly SyncService sync;
    private readonly TradeService trade;
    private readonly Dictionary<uint, List<LinkSource>> recentLinks = new();

    public ChatLinkService(Configuration config, CustomizationStore store, SyncService sync, TradeService trade)
    {
        this.config = config;
        this.store = store;
        this.sync = sync;
        this.trade = trade;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose() => Plugin.ChatGui.ChatMessage -= OnChatMessage;

    public bool TryGetLink(uint itemId, string? hoveredText, out LinkSource source)
    {
        source = default;
        if (!recentLinks.TryGetValue(itemId, out var links)) return false;
        links.RemoveAll(l => DateTime.UtcNow - l.SeenUtc > LinkMemory);
        if (links.Count == 0) return false;

        if (!string.IsNullOrEmpty(hoveredText))
        {
            for (var i = links.Count - 1; i >= 0; i--)
            {
                if (links[i].DisplayText == hoveredText)
                {
                    source = links[i];
                    return true;
                }
            }
        }

        source = links[^1];
        return true;
    }

    public static string GetLinkText(IReadOnlyList<Payload> payloads, int linkIndex)
    {
        var text = new System.Text.StringBuilder();
        var end = Math.Min(payloads.Count, linkIndex + 1 + LinkTextSearchWindow);
        for (var j = linkIndex + 1; j < end; j++)
        {
            if (payloads[j] is ItemPayload or RawPayload) break;
            if (payloads[j] is TextPayload { Text: { } t }) text.Append(t);
        }
        return NormalizeLinkText(text.ToString());
    }

    public static string NormalizeLinkText(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (c is not (>= '\uE000' and <= '\uF8FF') && !char.IsControl(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    private void OnChatMessage(IHandleableChatMessage chat)
    {
        try
        {
            var message = chat.Message;
            var payloads = message.Payloads;
            var hasLinks = payloads.Any(p => p is ItemPayload);
            var prefetch = PrefetchChannels.Contains(chat.LogKind);
            if (!hasLinks && !prefetch) return;

            PlayerKey? tradedFrom = hasLinks && string.IsNullOrWhiteSpace(chat.Sender.TextValue) && trade.TryConsumeReceivedItem(out var partner)
                ? partner
                : null;

            var (sender, isSelf) = tradedFrom is { } giver ? ((PlayerKey?)giver, giver == store.LocalKey) : ResolveSender(chat);

            if (prefetch && !isSelf && sender is { } talker) sync.EnsureProfile(talker);
            if (!hasLinks) return;

            var modified = false;

            for (var i = 0; i < payloads.Count; i++)
            {
                if (payloads[i] is not ItemPayload link || link.Kind == ItemKind.EventItem) continue;

                var itemId = ItemInfo.ToBaseId(link.RawItemId);
                if (itemId == 0) continue;

                var custom = store.Lookup(sender, isSelf, itemId);
                if (custom == null && !isSelf && sender is { } other)
                    sync.EnsureProfile(other);

                if (config.EnableChatLinks && custom != null && custom.HasName())
                    modified |= ReplaceLinkText(payloads, i, itemId, custom.Name);

                if (!recentLinks.TryGetValue(itemId, out var links)) recentLinks[itemId] = links = new();
                links.Add(new LinkSource(sender, isSelf, DateTime.UtcNow, GetLinkText(payloads, i)));
                if (links.Count > MaxLinksPerItem) links.RemoveAt(0);

                if (tradedFrom is { } from) trade.AdoptReceivedItem(from, itemId);
            }

            if (modified)
            {
                chat.Message = message;
                if (config.DebugLogging)
                    Plugin.Log.Debug("Rewrote item link(s) from {Sender} (self={Self})", sender?.ToString() ?? "?", isSelf);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to process chat message for item links.");
        }
    }

    private static bool ReplaceLinkText(List<Payload> payloads, int linkIndex, uint itemId, string replacement)
    {
        var forms = ItemInfo.GetNameForms(itemId);
        var end = Math.Min(payloads.Count, linkIndex + 1 + LinkTextSearchWindow);
        for (var j = linkIndex + 1; j < end; j++)
        {
            if (payloads[j] is ItemPayload) break;
            if (payloads[j] is not TextPayload { Text: { } text }) continue;

            foreach (var form in forms)
            {
                var at = text.IndexOf(form, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                payloads[j] = new TextPayload(text[..at] + replacement + text[(at + form.Length)..]);
                return true;
            }
        }
        return false;
    }

    private (PlayerKey? Sender, bool IsSelf) ResolveSender(IChatMessage chat)
    {
        var local = store.LocalKey;

        if (chat.SourceKind == XivChatRelationKind.LocalPlayer || chat.LogKind == XivChatType.TellOutgoing)
            return (local, true);

        var player = chat.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player != null && !string.IsNullOrEmpty(player.PlayerName))
        {
            var key = new PlayerKey(player.PlayerName, player.World.RowId);
            return (key, key == local);
        }

        if (local is { } me && chat.Sender.TextValue.Contains(me.Name, StringComparison.Ordinal))
            return (me, true);

        if (local is { } self && string.IsNullOrWhiteSpace(chat.Sender.TextValue))
            return (self, true);

        return (null, false);
    }
}
