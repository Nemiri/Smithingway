using System;
using System.Collections.Generic;
using System.Linq;
using Smithingway.Models;
using Smithingway.Util;

namespace Smithingway.Services;

public sealed class CustomizationStore
{
    private readonly Configuration config;
    private readonly RemoteCache remote;

    public event Action? LocalChanged;

    public CustomizationStore(Configuration config, RemoteCache remote)
    {
        this.config = config;
        this.remote = remote;
    }

    public CharacterProfile? CurrentProfile
    {
        get
        {
            var ps = Plugin.PlayerState;
            if (!ps.IsLoaded || ps.ContentId == 0) return null;

            if (!config.Profiles.TryGetValue(ps.ContentId, out var profile))
            {
                profile = new CharacterProfile();
                config.Profiles[ps.ContentId] = profile;
            }

            if (profile.CharacterName != ps.CharacterName) profile.CharacterName = ps.CharacterName;
            if (profile.HomeWorldId != ps.HomeWorld.RowId) profile.HomeWorldId = ps.HomeWorld.RowId;
            return profile;
        }
    }

    public PlayerKey? LocalKey
    {
        get
        {
            var ps = Plugin.PlayerState;
            return ps.IsLoaded && !string.IsNullOrEmpty(ps.CharacterName) ? new PlayerKey(ps.CharacterName, ps.HomeWorld.RowId) : null;
        }
    }

    public ItemCustomization? GetLocal(uint itemId)
        => CurrentProfile?.Items.TryGetValue(itemId, out var c) == true ? c : null;

    public bool SetLocal(uint itemId, string name, string flavor)
    {
        var profile = CurrentProfile;
        if (profile == null || itemId == 0) return false;

        var entry = new ItemCustomization
        {
            Name = TextSanitizer.CleanName(name),
            FlavorText = TextSanitizer.CleanFlavor(flavor),
            UpdatedUtc = DateTime.UtcNow,
        };

        if (entry.IsEmpty()) profile.Items.Remove(itemId);
        else profile.Items[itemId] = entry;

        config.Save();
        LocalChanged?.Invoke();
        return true;
    }

    public event Action<uint>? ItemReplaced;

    public bool AdoptLocal(uint itemId, ItemCustomization from, string namedBy)
    {
        var profile = CurrentProfile;
        if (profile == null || itemId == 0 || from.IsEmpty()) return false;

        var author = TextSanitizer.CleanName(namedBy);
        if (string.Equals(author, LocalKey?.Name, StringComparison.Ordinal)) author = string.Empty;

        var entry = new ItemCustomization
        {
            Name = TextSanitizer.CleanName(from.Name),
            FlavorText = TextSanitizer.CleanFlavor(from.FlavorText),
            NamedBy = author,
            UpdatedUtc = DateTime.UtcNow,
        };

        if (profile.Items.TryGetValue(itemId, out var existing) &&
            existing.Name == entry.Name && existing.FlavorText == entry.FlavorText && existing.NamedBy == entry.NamedBy)
            return false;

        profile.Items[itemId] = entry;
        config.Save();
        ItemReplaced?.Invoke(itemId);
        LocalChanged?.Invoke();
        return true;
    }

    public bool RemoveLocal(uint itemId)
    {
        var profile = CurrentProfile;
        if (profile == null || !profile.Items.Remove(itemId)) return false;
        config.Save();
        LocalChanged?.Invoke();
        return true;
    }

    public List<(uint ItemId, ItemCustomization Custom)> ItemsFor(PlayerKey? player, bool isSelf)
    {
        if (isSelf) return CurrentProfile?.Items.Select(kv => (kv.Key, kv.Value)).ToList() ?? [];
        return player is { } key && config.ShowOthersCustomizations ? remote.GetItems(key) : [];
    }

    public ItemCustomization? Lookup(PlayerKey? sender, bool isSelf, uint itemId)
    {
        if (isSelf) return GetLocal(itemId);
        if (sender is not { } key || !config.ShowOthersCustomizations) return null;
        return remote.Get(key, itemId);
    }
}
