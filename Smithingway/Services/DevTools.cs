using System.Collections.Generic;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Smithingway.Models;
using Smithingway.Util;

namespace Smithingway.Services;

public sealed class DevTools
{
    public const string TestPlayerName = "Test Smith";

    private readonly CustomizationStore store;
    private readonly RemoteCache remote;

    public DevTools(CustomizationStore store, RemoteCache remote)
    {
        this.store = store;
        this.remote = remote;
    }

    public void RunChatLinkTest(uint? requestedItemId = null)
    {
        var ps = Plugin.PlayerState;
        if (!ps.IsLoaded)
        {
            Plugin.ChatGui.PrintError("[Smithingway] Log in to a character first.");
            return;
        }

        var itemId = requestedItemId ?? GetMainHandItemId();
        if (itemId == 0 || !ItemInfo.IsCustomizable(itemId))
        {
            Plugin.ChatGui.PrintError("[Smithingway] Couldn't find an item to test with (housing items aren't supported). Try /smithingway test <itemId>.");
            return;
        }

        var world = ps.HomeWorld.RowId;
        var testPlayer = new PlayerKey(TestPlayerName, world);
        remote.SetProfile(testPlayer, new Dictionary<uint, ItemCustomization>
        {
            [itemId] = new()
            {
                Name = "Testblade of the Smithingway",
                FlavorText = "Forged in a test environment.\nNo real players were harmed in its making.",
            },
        }, isTestData: true);

        Print(new PlayerKey(TestPlayerName, world), "This is what other players' renamed gear looks like: ", itemId);

        if (store.LocalKey is { } me)
            Print(me, store.GetLocal(itemId)?.HasName() == true ? "And here's your version: " : "You haven't renamed this one yet: ", itemId);

        Plugin.ChatGui.Print("[Smithingway] Hover the links above to check the tooltips. Remove the test data from Settings → Developer.");
    }

    private static void Print(PlayerKey from, string text, uint itemId)
    {
        var sender = new SeStringBuilder()
            .Add(new PlayerPayload(from.Name, from.WorldId))
            .AddText(from.Name)
            .Add(RawPayload.LinkTerminator)
            .Build();

        var message = new SeStringBuilder().AddText(text).AddItemLink(itemId, false).Build();
        Plugin.ChatGui.Print(new XivChatEntry { Type = XivChatType.Say, Name = sender, Message = message });
    }

    private static uint GetMainHandItemId()
    {
        foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems))
            if (item.InventorySlot == 0 && !item.IsEmpty) return item.BaseItemId;
        return 0;
    }
}
