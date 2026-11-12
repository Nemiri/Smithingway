using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Enums;
using Smithingway.Models;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Smithingway.Util;
using static FFXIVClientStructs.FFXIV.Component.GUI.AtkTooltipManager;

namespace Smithingway.Services;

public sealed unsafe class ItemNameLabelService : IDisposable
{
    private delegate void ShowTooltipDelegate(
        AtkTooltipManager* manager, AtkTooltipType type, ushort parentId, AtkResNode* targetNode,
        AtkTooltipArgs* args, nint unkDelegate, bool unk7, bool unk8);

    private readonly Configuration config;
    private readonly CustomizationStore store;
    private readonly TradeService trade;
    private readonly Hook<ShowTooltipDelegate>? hook;

    private nint buffer;
    private int bufferSize;

    public ItemNameLabelService(Configuration config, CustomizationStore store, TradeService trade)
    {
        this.config = config;
        this.store = store;
        this.trade = trade;

        var address = AtkTooltipManager.Addresses.ShowTooltip.Value;
        if (address == IntPtr.Zero)
        {
            Plugin.Log.Warning("ShowTooltip address not resolved; item name labels won't be renamed.");
            return;
        }
        hook = Plugin.GameInterop.HookFromAddress<ShowTooltipDelegate>(address, ShowTooltipDetour);
        hook.Enable();
    }

    public void Dispose()
    {
        hook?.Dispose();
        if (buffer != 0) Marshal.FreeHGlobal(buffer);
        buffer = 0;
    }

    private void ShowTooltipDetour(
        AtkTooltipManager* manager, AtkTooltipType type, ushort parentId, AtkResNode* targetNode,
        AtkTooltipArgs* args, nint unkDelegate, bool unk7, bool unk8)
    {
        try
        {
            if (args != null && type.HasFlag(AtkTooltipType.Item))
            {
                TooltipSource.RecordTooltip(parentId, args);
                if (config.DebugLogging)
                    TooltipService.DebugLine($"show-tooltip: type={type} addon={TooltipSource.AddonName} kind={TooltipSource.Kind} item={TooltipSource.ItemId}");
            }

            if (config.EnableTooltips && args != null &&
                type.HasFlag(AtkTooltipType.Text) && type.HasFlag(AtkTooltipType.Item))
                TryRename(args);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to rename item name label.");
        }

        hook!.Original(manager, type, parentId, targetNode, args, unkDelegate, unk7, unk8);
    }

    private void TryRename(AtkTooltipArgs* args)
    {
        if (!args->TextArgs.Text.HasValue) return;

        List<(uint ItemId, ItemCustomization Custom)> candidates;
        switch (args->ItemArgs.Kind)
        {
            case DetailKind.TradeRemote:
                if (trade.CurrentPartner is not { } partner) return;
                candidates = store.ItemsFor(partner, isSelf: partner == store.LocalKey);
                break;
            case DetailKind.InventoryItem when TooltipSource.IsExamine:
            case DetailKind.ItemInspection:
                if (InspectService.CurrentlyInspected is not { } owner) return;
                candidates = store.ItemsFor(owner, isSelf: owner == store.LocalKey);
                break;
            case DetailKind.InventoryItem:
                candidates = store.ItemsFor(store.LocalKey, isSelf: true);
                break;
            default:
                return;
        }

        var text = SeString.Parse(args->TextArgs.Text.AsSpan());
        var (itemId, original, newName) = MatchRenamedItem(text.TextValue, candidates);
        if (config.DebugLogging)
            TooltipService.DebugLine($"label: kind={args->ItemArgs.Kind} matched={itemId} text={TooltipService.Quote(text)}");
        if (itemId == 0) return;

        var payloads = text.Payloads;
        var replaced = false;
        for (var i = 0; i < payloads.Count; i++)
        {
            if (payloads[i] is TextPayload { Text: { } t } && t.Contains(original, StringComparison.Ordinal))
            {
                payloads[i] = new TextPayload(t.Replace(original, newName, StringComparison.Ordinal));
                replaced = true;
                break;
            }
        }
        if (!replaced) return;

        var encoded = text.Encode();
        EnsureBuffer(encoded.Length + 1);
        Marshal.Copy(encoded, 0, buffer, encoded.Length);
        ((byte*)buffer)[encoded.Length] = 0;
        args->TextArgs.Text = (byte*)buffer;
    }

    private static (uint ItemId, string Original, string NewName) MatchRenamedItem(
        string labelText, List<(uint ItemId, ItemCustomization Custom)> candidates)
    {
        var bare = StripTrailingGlyphs(labelText);
        foreach (var (itemId, custom) in candidates)
        {
            if (!custom.HasName()) continue;
            var original = ItemInfo.GetName(itemId);
            if (bare.Equals(original, StringComparison.Ordinal)) return (itemId, original, custom.Name);
        }
        return default;
    }

    private static string StripTrailingGlyphs(string s)
    {
        var end = s.Length;
        while (end > 0 && (char.IsWhiteSpace(s[end - 1]) || s[end - 1] is >= '\uE000' and <= '\uF8FF')) end--;
        return s[..end];
    }

    private void EnsureBuffer(int size)
    {
        if (size <= bufferSize) return;
        if (buffer != 0) Marshal.FreeHGlobal(buffer);
        bufferSize = Math.Max(size, 256);
        buffer = Marshal.AllocHGlobal(bufferSize);
    }
}
