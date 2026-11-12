using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Smithingway.Util;
using static FFXIVClientStructs.FFXIV.Component.GUI.AtkTooltipManager;

namespace Smithingway.Services;

public sealed unsafe class TooltipSource : IDisposable
{
    private static readonly string[] ChatPanels = ["ChatLog", "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3"];

    public static string? AddonName { get; private set; }
    public static DetailKind Kind { get; private set; }

    public static uint ItemId { get; private set; }

    public static string? HoveredLinkText { get; private set; }
    public static uint HoveredLinkItemId { get; private set; }

    public static bool IsChatLog => AddonName != null && AddonName.StartsWith("ChatLog", StringComparison.Ordinal);

    public static bool IsExamine => AddonName == "CharacterInspect";

    private readonly Configuration config;

    public TooltipSource(Configuration config)
    {
        this.config = config;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, ChatPanels, OnChatPanelEvent);
    }

    public void Dispose() => Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, ChatPanels, OnChatPanelEvent);

    public static void RecordTooltip(ushort parentAddonId, AtkTooltipArgs* args)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager != null ? manager->GetAddonById(parentAddonId) : null;
        AddonName = addon != null ? addon->NameString : null;
        Kind = args->ItemArgs.Kind;
        ItemId = Kind is DetailKind.Item or DetailKind.ItemId ? (uint)args->ItemArgs.ItemId : 0;
    }

    private void OnChatPanelEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonReceiveEventArgs e || (int)e.AtkEventType != (int)AtkEventType.LinkMouseOver || e.AtkEventData == 0) return;
            var link = ((AtkEventData*)e.AtkEventData)->LinkData;
            if (link == null || link->Payload == null) return;

            var (itemId, text) = ReadItemLink(link->Payload);
            if (itemId == 0) return;
            HoveredLinkItemId = itemId;
            HoveredLinkText = text;

            if (config.DebugLogging)
                TooltipService.DebugLine($"link-hover: panel={e.AddonName} item={itemId} text=\"{text}\"");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read hovered chat link.");
        }
    }

    private static (uint ItemId, string Text) ReadItemLink(byte* payload)
    {
        var length = 0;
        while (length < 4096 && payload[length] != 0) length++;

        var payloads = SeString.Parse(new ReadOnlySpan<byte>(payload, length)).Payloads;
        var index = payloads.FindIndex(p => p is ItemPayload);
        if (index < 0) return default;

        var itemId = ItemInfo.ToBaseId(((ItemPayload)payloads[index]).RawItemId);
        return (itemId, ChatLinkService.GetLinkText(payloads, index));
    }
}
