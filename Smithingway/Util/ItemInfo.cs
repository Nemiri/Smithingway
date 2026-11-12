using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Excel.Sheets;

namespace Smithingway.Util;

public static class ItemInfo
{
    public static readonly IReadOnlyList<(int Slot, string Label)> EquipSlots =
    [
        (0, "Main Hand"), (1, "Off Hand"), (2, "Head"), (3, "Body"), (4, "Hands"), (6, "Legs"), (7, "Feet"),
        (8, "Earrings"), (9, "Necklace"), (10, "Bracelets"), (11, "Ring"), (12, "Ring"),
    ];

    public static uint ToBaseId(ulong rawId)
    {
        if (rawId == 0 || rawId >= 2_000_000) return 0;
        return (uint)(rawId % 500_000);
    }

    public static Item? GetItem(uint itemId)
        => itemId == 0 ? null : Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);

    public static string GetName(uint itemId)
        => GetItem(itemId)?.Name.ExtractText() is { Length: > 0 } name ? name : $"Item #{itemId}";

    public static List<string> GetNameForms(uint itemId)
    {
        var forms = new List<string>();
        if (GetItem(itemId) is not { } item) return forms;
        foreach (var s in new[] { item.Name, item.Singular, item.Plural })
        {
            var text = s.ExtractText();
            if (text.Length > 0 && !forms.Exists(f => f.Equals(text, System.StringComparison.OrdinalIgnoreCase))) forms.Add(text);
        }
        forms.Sort((a, b) => b.Length.CompareTo(a.Length));
        return forms;
    }

    public static bool IsEquippable(uint itemId) => GetItem(itemId) is { } item && item.EquipSlotCategory.RowId != 0;

    private const byte HousingFilterGroup = 14;
    private const uint PaintingUiCategory = 95;
    private const uint UnobtainableUiCategory = 39;

    public static bool IsHousing(Item item) => item.FilterGroup == HousingFilterGroup || item.ItemUICategory.RowId == PaintingUiCategory;

    public static bool IsCustomizable(uint itemId) => GetItem(itemId) is { } item && IsCustomizable(item);

    public static bool IsCustomizable(Item item) => !item.Name.IsEmpty && !IsHousing(item);

    private static List<(uint Id, string Name)>? searchIndex;

    public static List<(uint Id, string Name)> Search(string query, int limit = 100)
    {
        query = query.Trim();
        if (query.Length < 2) return [];

        searchIndex ??= BuildSearchIndex();
        var results = new List<(uint Id, string Name, bool Prefix)>();
        foreach (var (id, name) in searchIndex)
        {
            var at = name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase);
            if (at >= 0) results.Add((id, name, at == 0));
        }

        results.Sort((a, b) => a.Prefix != b.Prefix ? (a.Prefix ? -1 : 1) : string.Compare(a.Name, b.Name, System.StringComparison.CurrentCultureIgnoreCase));
        return results.Count > limit
            ? results.GetRange(0, limit).ConvertAll(r => (r.Id, r.Name))
            : results.ConvertAll(r => (r.Id, r.Name));
    }

    private static List<(uint Id, string Name)> BuildSearchIndex()
    {
        var index = new List<(uint, string)>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
        {
            if (item.RowId == 0 || item.ItemUICategory.RowId == UnobtainableUiCategory || !IsCustomizable(item)) continue;
            index.Add((item.RowId, item.Name.ExtractText()));
        }
        return index;
    }

    public static IDalamudTextureWrap GetIcon(uint itemId, bool hq = false)
    {
        var iconId = GetItem(itemId)?.Icon ?? 0u;
        return Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, hq, false, null)).GetWrapOrEmpty();
    }
}
