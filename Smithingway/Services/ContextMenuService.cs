using System;
using Dalamud.Game.Gui.ContextMenu;
using Smithingway.Util;

namespace Smithingway.Services;

public sealed class ContextMenuService : IDisposable
{
    private readonly CustomizationStore store;
    private readonly Action<uint> openEditor;

    public ContextMenuService(CustomizationStore store, Action<uint> openEditor)
    {
        this.store = store;
        this.openEditor = openEditor;
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory) return;
        if (args.Target is not MenuTargetInventory { TargetItem: { IsEmpty: false } item }) return;

        var itemId = item.BaseItemId;
        if (!ItemInfo.IsCustomizable(itemId)) return;

        args.AddMenuItem(new MenuItem
        {
            Name = store.GetLocal(itemId) != null ? "Edit Smithingway name" : "Rename with Smithingway",
            PrefixChar = 'S',
            OnClicked = _ => openEditor(itemId),
        });
    }
}
