using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Smithingway.Models;
using Smithingway.Services;
using Smithingway.Sync;
using Smithingway.Util;

namespace Smithingway.Windows;

public sealed class ArmoryWindow : Window
{
    private static readonly Vector4 Gold = new(0.96f, 0.80f, 0.42f, 1f);
    private static readonly Vector4 LinkColor = new(0.98f, 0.72f, 0.38f, 1f);
    private static readonly Vector4 FlavorColor = new(0.86f, 0.83f, 0.76f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.62f, 0.66f, 1f);
    private static readonly Vector4 Warning = new(0.95f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 Danger = new(0.92f, 0.38f, 0.38f, 1f);
    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    private sealed class Draft
    {
        public string Name = string.Empty;
        public string Flavor = string.Empty;
    }

    private readonly CustomizationStore store;
    private readonly SyncService sync;
    private readonly Dictionary<uint, Draft> drafts = new();

    private uint selectedItemId;
    private bool selectedHq;

    private string searchQuery = string.Empty;
    private string lastSearchQuery = "\0";
    private List<(uint Id, string Name)> searchResults = [];
    private const int SearchLimit = 100;

    public ArmoryWindow(CustomizationStore store, SyncService sync, Action openSettings)
        : base("Smithingway — Armory###SmithingwayArmory")
    {
        this.store = store;
        this.sync = sync;
        store.ItemReplaced += itemId => drafts.Remove(itemId);

        Size = new Vector2(820, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(660, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            Click = _ => openSettings(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
    }

    public void OpenFor(uint itemId)
    {
        selectedItemId = itemId;
        selectedHq = false;
        foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems))
            if (item.BaseItemId == itemId) selectedHq = item.IsHq;
        IsOpen = true;
        BringToFront();
    }

    public void ResetState()
    {
        drafts.Clear();
        selectedItemId = 0;
    }

    public override void Draw()
    {
        var profile = store.CurrentProfile;
        if (profile == null)
        {
            CenteredDisabled("Log in to a character to rename your gear.");
            return;
        }

        var listWidth = 290f * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("##sw-list", new Vector2(listWidth, 0), true))
            DrawList(profile);
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##sw-editor", Vector2.Zero, true))
            DrawEditor();
        ImGui.EndChild();
    }

    private void DrawList(CharacterProfile profile)
    {
        if (!ImGui.BeginTabBar("##sw-tabs")) return;

        if (ImGui.BeginTabItem("Equipped"))
        {
            DrawEquipped();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Renamed ({profile.Items.Count})###sw-renamed"))
        {
            DrawRenamed(profile);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Search"))
        {
            DrawSearch();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawSearch()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sw-search", "Search any item…", ref searchQuery, 64);
        if (searchQuery != lastSearchQuery)
        {
            lastSearchQuery = searchQuery;
            searchResults = ItemInfo.Search(searchQuery, SearchLimit);
        }

        ImGui.Spacing();
        if (searchQuery.Trim().Length < 2)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Muted, "Type at least 2 letters. Any item works except housing items (furniture, house parts and paintings).");
            ImGui.PopTextWrapPos();
            return;
        }
        if (searchResults.Count == 0)
        {
            ImGui.TextColored(Muted, "No matching items.");
            return;
        }
        if (searchResults.Count >= SearchLimit)
            ImGui.TextColored(Muted, $"Showing the first {SearchLimit} matches.");

        foreach (var (id, _) in searchResults)
            DrawItemRow($"sr{id}", id, false, ItemInfo.GetItem(id)?.ItemUICategory.ValueNullable?.Name.ExtractText() ?? string.Empty);
    }

    private void DrawEquipped()
    {
        var equipped = Plugin.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems);
        foreach (var (slot, label) in ItemInfo.EquipSlots)
        {
            GameInventoryItem? found = null;
            foreach (var item in equipped)
            {
                if (item.InventorySlot == slot && !item.IsEmpty)
                {
                    found = item;
                    break;
                }
            }

            if (found is { } equippedItem)
                DrawItemRow($"eq{slot}", equippedItem.BaseItemId, equippedItem.IsHq, label);
            else
                DrawEmptyRow(label);
        }
    }

    private void DrawRenamed(CharacterProfile profile)
    {
        if (profile.Items.Count == 0)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Muted, "Nothing renamed yet. Pick an item on the Equipped or Search tab, or right-click an item in your inventory and choose \"Rename with Smithingway\".");
            ImGui.PopTextWrapPos();
            return;
        }

        var entries = profile.Items
            .Select(kv => (Id: kv.Key, Custom: kv.Value))
            .OrderBy(e => e.Custom.HasName() ? e.Custom.Name : ItemInfo.GetName(e.Id), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var (id, custom) in entries)
            DrawItemRow($"rn{id}", id, false, custom.HasName() ? ItemInfo.GetName(id) : "Flavor text only");
    }

    private void DrawItemRow(string rowId, uint itemId, bool hq, string subtitle)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rowHeight = 42f * scale;
        var iconSize = 34f * scale;
        var custom = store.GetLocal(itemId);
        var original = ItemInfo.GetName(itemId);

        var start = ImGui.GetCursorPos();
        if (ImGui.Selectable($"##{rowId}", selectedItemId == itemId, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
        {
            selectedItemId = itemId;
            selectedHq = hq;
        }
        if (ImGui.IsItemHovered() && custom?.HasName() == true)
            ImGui.SetTooltip(original);
        var end = ImGui.GetCursorPos();

        ImGui.SetCursorPos(start + new Vector2(4 * scale, (rowHeight - iconSize) / 2));
        ImGui.Image(ItemInfo.GetIcon(itemId, hq).Handle, new Vector2(iconSize));

        ImGui.SetCursorPos(start + new Vector2(iconSize + 12 * scale, 3 * scale));
        ImGui.BeginGroup();
        Colored(Muted, HasUnsavedChanges(itemId) ? $"{subtitle}  •  unsaved" : subtitle);
        if (custom?.HasName() == true) Colored(Gold, custom.Name);
        else ImGui.TextUnformatted(original);
        ImGui.EndGroup();

        ImGui.SetCursorPos(end);
    }

    private static void DrawEmptyRow(string label)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorPos();
        ImGui.Dummy(new Vector2(0, 42f * scale));
        var end = ImGui.GetCursorPos();
        ImGui.SetCursorPos(start + new Vector2(46f * scale, 3 * scale));
        ImGui.BeginGroup();
        Colored(Muted, label);
        ImGui.TextDisabled("Nothing equipped");
        ImGui.EndGroup();
        ImGui.SetCursorPos(end);
    }

    private void DrawEditor()
    {
        if (selectedItemId == 0 || ItemInfo.GetItem(selectedItemId) is not { } item)
        {
            CenteredDisabled("Select an item to rename it.");
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var itemId = selectedItemId;
        var original = ItemInfo.GetName(itemId);
        var category = item.ItemUICategory.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var saved = store.GetLocal(itemId);
        var draft = GetDraft(itemId, saved);

        ImGui.Image(ItemInfo.GetIcon(itemId, selectedHq).Handle, new Vector2(48 * scale));
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(original);
        var details = ItemInfo.IsEquippable(itemId)
            ? (category.Length > 0 ? $"{category}  ·  Item Level {item.LevelItem.RowId}" : $"Item Level {item.LevelItem.RowId}")
            : category;
        if (details.Length > 0) Colored(Muted, details);
        if (!string.IsNullOrEmpty(saved?.NamedBy)) Colored(Gold, $"Received in a trade  ·  named by {saved.NamedBy}");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Custom name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sw-name", original, ref draft.Name, TextSanitizer.MaxNameLength * 4);
        DrawCounter(draft.Name.Trim().Length, TextSanitizer.MaxNameLength);

        ImGui.TextUnformatted("Flavor text");
        ImGui.SameLine();
        Colored(Muted, $"(shown in the tooltip, up to {TextSanitizer.MaxFlavorLines} lines)");
        ImGui.InputTextMultiline("##sw-flavor", ref draft.Flavor, TextSanitizer.MaxFlavorLength * 4, new Vector2(-1, 92 * scale));
        DrawCounter(draft.Flavor.Replace("\r", "").Trim().Length, TextSanitizer.MaxFlavorLength,
                    draft.Flavor.Split('\n').Length > TextSanitizer.MaxFlavorLines ? $"only the first {TextSanitizer.MaxFlavorLines} lines are kept" : null);

        if (TextSanitizer.HasUnsupportedCharacters(draft.Name, false) || TextSanitizer.HasUnsupportedCharacters(draft.Flavor, true))
            Colored(Warning, "Some characters (emoji, game icons, control codes) can't be shown in game and will be removed.");

        var cleanName = TextSanitizer.CleanName(draft.Name);
        var cleanFlavor = TextSanitizer.CleanFlavor(draft.Flavor);

        ImGui.Spacing();
        ImGui.TextUnformatted("Preview");
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + 6 * scale;
        var previewHeight = Math.Max(110 * scale, ImGui.GetContentRegionAvail().Y - footerHeight);
        if (ImGui.BeginChild("##sw-preview", new Vector2(-1, previewHeight), true))
            DrawPreview(original, category, cleanName, cleanFlavor);
        ImGui.EndChild();

        DrawFooter(itemId, draft, saved);
    }

    private static void DrawPreview(string original, string category, string name, string flavor)
    {
        Colored(Muted, "Tooltip");
        Colored(name.Length > 0 ? Gold : White, name.Length > 0 ? name : original);
        if (category.Length > 0) Colored(Muted, category);

        if (flavor.Length > 0)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            Colored(FlavorColor, flavor);
            ImGui.PopTextWrapPos();
        }

        if (name.Length > 0)
        {
            ImGui.Spacing();
            Colored(Muted, $"[Smithingway] Originally: {original}");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Colored(Muted, "Chat link");
        ImGui.SameLine();
        Colored(LinkColor, $"» {(name.Length > 0 ? name : original)}");
    }

    private void DrawFooter(uint itemId, Draft draft, ItemCustomization? saved)
    {
        var dirty = IsDirty(draft, saved);

        ImGui.BeginDisabled(!dirty);
        if (ImGui.Button("Save"))
        {
            store.SetLocal(itemId, draft.Name, draft.Flavor);
            drafts.Remove(itemId);
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert"))
            drafts.Remove(itemId);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(saved == null);
        if (ImGui.Button("Remove") && ImGui.GetIO().KeyCtrl)
        {
            store.RemoveLocal(itemId);
            drafts.Remove(itemId);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click to remove this item's custom name and flavor text.");

        ImGui.SameLine();
        if (dirty) Colored(Warning, "Unsaved changes");
        else if (saved != null) Colored(Muted, $"Saved  ·  Sharing: {sync.Provider.DisplayName}");
        else Colored(Muted, "Not renamed");
    }

    private void DrawCounter(int length, int max, string? extraWarning = null)
    {
        var over = length > max;
        var text = over ? $"{length}/{max}, will be cut to {max}" : $"{length}/{max}";
        if (extraWarning != null) text += $"  ·  {extraWarning}";
        Colored(over || extraWarning != null ? Danger : Muted, text);
    }

    private Draft GetDraft(uint itemId, ItemCustomization? saved)
    {
        if (!drafts.TryGetValue(itemId, out var draft))
        {
            draft = new Draft { Name = saved?.Name ?? string.Empty, Flavor = saved?.FlavorText ?? string.Empty };
            drafts[itemId] = draft;
        }
        return draft;
    }

    private bool HasUnsavedChanges(uint itemId)
        => drafts.TryGetValue(itemId, out var draft) && IsDirty(draft, store.GetLocal(itemId));

    private static bool IsDirty(Draft draft, ItemCustomization? saved)
        => TextSanitizer.CleanName(draft.Name) != (saved?.Name ?? string.Empty)
        || TextSanitizer.CleanFlavor(draft.Flavor) != (saved?.FlavorText ?? string.Empty);

    private static void Colored(Vector4 color, string text) => ImGui.TextColored(color, text);

    private static void CenteredDisabled(string text)
    {
        var avail = ImGui.GetContentRegionAvail();
        var size = ImGui.CalcTextSize(text);
        ImGui.SetCursorPos(ImGui.GetCursorPos() + Vector2.Max(Vector2.Zero, (avail - size) / 2));
        ImGui.TextDisabled(text);
    }
}
