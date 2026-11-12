using System;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Smithingway.Models;
using Smithingway.Util;

namespace Smithingway.Services;

public sealed unsafe class TooltipService : IDisposable
{
    private const string AddonName = "ItemDetail";
    private const int NameField = 0;
    private const int DescriptionField = 13;
    private const int VisibilityFlagsSlot = 5;
    private const int DescriptionVisibleBit = 2;

    private delegate void TooltipArraysDelegate(AddonItemDetail* addon, NumberArrayData* numbers, StringArrayData* strings);

    private readonly Configuration config;
    private readonly CustomizationStore store;
    private readonly ChatLinkService chatLinks;
    private readonly TradeService trade;
    private readonly Hook<TooltipArraysDelegate>? generateHook;

    public TooltipService(Configuration config, CustomizationStore store, ChatLinkService chatLinks, TradeService trade)
    {
        this.config = config;
        this.store = store;
        this.chatLinks = chatLinks;
        this.trade = trade;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, AddonName, OnItemDetailUpdate);
        generateHook = TryHook(AddonItemDetail.Addresses.GenerateItemTooltip.Value, GenerateItemTooltipDetour, "GenerateItemTooltip");
    }

    private static Hook<TooltipArraysDelegate>? TryHook(IntPtr address, TooltipArraysDelegate detour, string name)
    {
        if (address == IntPtr.Zero)
        {
            Plugin.Log.Warning("{Name} address not resolved; tooltips may not update in every case.", name);
            return null;
        }
        var hook = Plugin.GameInterop.HookFromAddress(address, detour);
        hook.Enable();
        return hook;
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, AddonName, OnItemDetailUpdate);
        generateHook?.Dispose();
    }

    private void GenerateItemTooltipDetour(AddonItemDetail* addon, NumberArrayData* numbers, StringArrayData* strings)
    {
        try { Modify(numbers, strings, "generate"); }
        catch (Exception ex) { Plugin.Log.Error(ex, "Failed to modify item tooltip."); }
        generateHook!.Original(addon, numbers, strings);
    }

    private void OnItemDetailUpdate(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRequestedUpdateArgs update) return;
        try
        {
            var stringArrays = (StringArrayData**)update.StringArrayData;
            var numberArrays = (NumberArrayData**)update.NumberArrayData;
            if (stringArrays == null || numberArrays == null) return;
            Modify(numberArrays[(int)NumberArrayType.ItemDetail], stringArrays[(int)StringArrayType.ItemDetail], "pre-update");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to modify item tooltip.");
        }
    }

    private void Modify(NumberArrayData* numbers, StringArrayData* strings, string source)
    {
        if (!config.EnableTooltips || strings == null) return;

        var itemId = ItemInfo.ToBaseId(Plugin.GameGui.HoveredItem);
        if (itemId == 0) return;

        if (config.DebugLogging) TraceCall(source, itemId, numbers);
        var dump = config.DebugLogging && itemId != lastDumpedItem ? BeginDump(itemId, strings, numbers, source) : null;

        var (custom, linkedBy) = Resolve(itemId);
        if (custom == null || custom.IsEmpty())
        {
            if (dump != null) FlushDump(dump.AppendLine("-- not customized"));
            return;
        }

        var hasVisibility = numbers != null && numbers->Size > VisibilityFlagsSlot;
        var descriptionVisible = hasVisibility && (numbers->IntArray[VisibilityFlagsSlot] & DescriptionVisibleBit) != 0;

        var original = ItemInfo.GetName(itemId);
        if (custom.HasName()) ApplyName(strings, original, custom.Name);
        var flavorResult = ApplyFlavor(strings, original, custom, linkedBy, descriptionVisible);

        if (flavorResult is "written" or "already applied" && hasVisibility)
            numbers->IntArray[VisibilityFlagsSlot] |= DescriptionVisibleBit;

        if (dump != null)
        {
            dump.AppendLine($"-- description section was {(descriptionVisible ? "visible" : "hidden")}; flavor step: {flavorResult}");
            dump.AppendLine($"-- after: [{NameField}] {Quote(Read(strings, NameField))}");
            dump.AppendLine($"-- after: [{DescriptionField}] {Quote(Read(strings, DescriptionField))}");
            if (hasVisibility) dump.AppendLine($"-- after: visibility flags = {numbers->IntArray[VisibilityFlagsSlot]}");
            FlushDump(dump);
        }
    }

    private (ItemCustomization? Custom, string? LinkedBy) Resolve(uint itemId)
    {
        var agent = AgentItemDetail.Instance();
        if (agent == null) return default;

        var kind = agent->DetailKind;
        switch (kind)
        {
            case DetailKind.TradeRemote:
            {
                if (trade.CurrentPartner is not { } partner) return default;
                var custom = store.Lookup(partner, partner == store.LocalKey, itemId);
                if (config.DebugLogging) DebugLine($"resolve: item={itemId} kind={kind} tradePartner={partner} found={custom != null}");
                return (custom, partner.Name);
            }

            case DetailKind.InventoryItem when TooltipSource.IsExamine:
            case DetailKind.ItemInspection:
            {
                if (InspectService.CurrentlyInspected is not { } owner) return default;
                var isSelf = owner == store.LocalKey;
                var custom = store.Lookup(owner, isSelf, itemId);
                if (config.DebugLogging) DebugLine($"resolve: item={itemId} kind={kind} examined={owner} self={isSelf} found={custom != null}");
                return (custom, isSelf ? null : owner.Name);
            }

            case DetailKind.InventoryItem:
            case DetailKind.InventoryContext:
            case DetailKind.GearSet:
                return (store.GetLocal(itemId), null);

            case DetailKind.Item:
            case DetailKind.ItemId:
            {
                var fromChat = TooltipSource.IsChatLog
                               && (TooltipSource.ItemId == 0 || ItemInfo.ToBaseId(TooltipSource.ItemId) == itemId);
                var hoveredText = TooltipSource.HoveredLinkItemId == itemId ? TooltipSource.HoveredLinkText : null;
                var hasLink = chatLinks.TryGetLink(itemId, hoveredText, out var source);
                var custom = fromChat && hasLink ? store.Lookup(source.Sender, source.IsSelf, itemId) : null;

                if (config.DebugLogging)
                    DebugLine($"resolve: item={itemId} kind={kind} tooltipFrom={TooltipSource.AddonName} fromChat={fromChat} " +
                              $"hovered=\"{hoveredText}\" matched=\"{source.DisplayText}\" linkBy={(hasLink ? source.Sender?.ToString() ?? "?" : "none")} " +
                              $"self={source.IsSelf} found={custom != null}");

                return custom == null ? default : (custom, source.IsSelf ? null : source.Sender?.Name);
            }

            default:
                return default;
        }
    }

    private static void ApplyName(StringArrayData* strings, string original, string replacement)
    {
        var current = Read(strings, NameField);
        if (current == null || !current.TextValue.Contains(original, StringComparison.Ordinal)) return;
        if (replacement.Contains(original, StringComparison.Ordinal) && current.TextValue.Contains(replacement, StringComparison.Ordinal)) return;

        var payloads = current.Payloads;
        for (var i = 0; i < payloads.Count; i++)
        {
            if (payloads[i] is TextPayload { Text: { } text } && text.Contains(original, StringComparison.Ordinal))
            {
                payloads[i] = new TextPayload(text.Replace(original, replacement, StringComparison.Ordinal));
                Write(strings, NameField, current);
                return;
            }
        }
    }

    private string ApplyFlavor(StringArrayData* strings, string original, ItemCustomization custom, string? linkedBy, bool descriptionVisible)
    {
        var flavor = BuildFlavorPart(custom);
        var meta = BuildMetaPart(original, custom, linkedBy);
        if (flavor == null && meta == null) return "nothing to show (all flavor options off)";

        var current = Read(strings, DescriptionField);
        if (current == null) return $"field {DescriptionField} out of range (size {strings->Size})";

        var keep = descriptionVisible && current.TextValue.Length > 0 ? current : null;

        if (keep != null &&
            ((flavor != null && keep.TextValue.StartsWith(flavor.TextValue, StringComparison.Ordinal)) ||
             (meta != null && keep.TextValue.EndsWith(meta.TextValue, StringComparison.Ordinal))))
            return "already applied";

        var result = new SeString();
        if (flavor != null) result.Append(flavor);
        if (keep != null)
        {
            if (result.Payloads.Count > 0) AddBlankLine(result);
            result.Append(keep);
        }
        if (meta != null)
        {
            if (result.Payloads.Count > 0) AddBlankLine(result);
            result.Append(meta);
        }

        if (result.TextValue == current.TextValue) return "already applied";
        Write(strings, DescriptionField, result);
        return "written";
    }

    private SeString? BuildFlavorPart(ItemCustomization custom)
    {
        if (!config.ShowFlavorText || !custom.HasFlavor()) return null;

        var b = new SeStringBuilder().AddItalicsOn();
        var lines = custom.FlavorText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) b.Add(NewLinePayload.Payload);
            b.AddText(lines[i]);
        }
        return b.AddItalicsOff().Build();
    }

    private SeString? BuildMetaPart(string original, ItemCustomization custom, string? linkedBy)
    {
        var showOriginal = config.ShowOriginalName && custom.HasName();

        var namedBy = !string.IsNullOrEmpty(custom.NamedBy) ? custom.NamedBy : linkedBy;
        if (namedBy == store.LocalKey?.Name) namedBy = null;
        if (!showOriginal && namedBy == null) return null;

        var meta = showOriginal ? $"Originally: {original}" : string.Empty;
        if (namedBy != null) meta += (meta.Length > 0 ? " · " : string.Empty) + $"Named by {namedBy}";
        return new SeStringBuilder().AddText($"[Smithingway] {meta}").Build();
    }

    private static void AddBlankLine(SeString s)
    {
        s.Payloads.Add(NewLinePayload.Payload);
        s.Payloads.Add(NewLinePayload.Payload);
    }

    private static SeString? Read(StringArrayData* strings, int index)
    {
        if (index < 0 || index >= strings->Size) return null;
        var ptr = strings->StringArray[index];
        return ptr.HasValue ? SeString.Parse(ptr.AsSpan()) : new SeString();
    }

    private static void Write(StringArrayData* strings, int index, SeString value)
    {
        var encoded = value.Encode();
        var buffer = new byte[encoded.Length + 1];
        encoded.CopyTo(buffer, 0);
        strings->SetValue(index, buffer, false, true, false);
    }

    private uint lastDumpedItem;

    private static void TraceCall(string source, uint itemId, NumberArrayData* numbers)
    {
        var flags = numbers != null && numbers->Size > VisibilityFlagsSlot ? numbers->IntArray[VisibilityFlagsSlot] : -1;
        var hovered = Plugin.GameGui.HoveredItem;
        FlushDump(new StringBuilder($"{DateTime.Now:HH:mm:ss.fff} call {source,-15} item {itemId} raw {hovered} flags {flags} desc={((flags & DescriptionVisibleBit) != 0 ? "on" : "off")}"));
    }

    private StringBuilder BeginDump(uint itemId, StringArrayData* strings, NumberArrayData* numbers, string source)
    {
        lastDumpedItem = itemId;
        var agent = AgentItemDetail.Instance();
        var sb = new StringBuilder();
        sb.AppendLine($"=== {DateTime.Now:HH:mm:ss} item {itemId} \"{ItemInfo.GetName(itemId)}\" kind={(agent != null ? agent->DetailKind.ToString() : "?")} via={source}");
        sb.AppendLine($"-- description in game data: {JsonSerializer.Serialize(ItemInfo.GetItem(itemId)?.Description.ExtractText() ?? "")}");
        sb.AppendLine($"-- strings (size {strings->Size}):");
        for (var i = 0; i < strings->Size; i++)
        {
            var s = Read(strings, i);
            if (s != null && s.TextValue.Length > 0) sb.AppendLine($"  [{i}] {Quote(s)}");
        }

        if (numbers != null)
        {
            sb.Append($"-- numbers (size {numbers->Size}):");
            for (var i = 0; i < numbers->Size; i++)
            {
                if (i % 10 == 0) sb.Append($"\n  [{i,3}]");
                sb.Append($" {numbers->IntArray[i]}");
            }
            sb.AppendLine();
        }
        return sb;
    }

    internal static void DebugLine(string line) => FlushDump(new StringBuilder($"{DateTime.Now:HH:mm:ss.fff} {line}"));

    private static void FlushDump(StringBuilder sb)
    {
        try
        {
            var path = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "tooltip-dump.txt");
            System.IO.File.AppendAllText(path, sb.ToString() + "\n");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not write tooltip dump.");
        }
    }

    internal static string Quote(SeString? s) => s == null ? "(null)" : JsonSerializer.Serialize(s.TextValue);
}
