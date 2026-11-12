using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Smithingway.Models;
using Smithingway.Util;

namespace Smithingway.Services;

public class RemoteProfile
{
    public Dictionary<uint, ItemCustomization> Items { get; set; } = new();
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public bool IsTestData { get; set; }
}

public sealed class RemoteCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object gate = new();
    private readonly string path;
    private Dictionary<string, RemoteProfile> profiles = new();

    public event Action? Changed;

    public RemoteCache(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        path = Path.Combine(configDirectory, "remote-cache.json");
        Load();
    }

    public int Count { get { lock (gate) return profiles.Count; } }

    public ItemCustomization? Get(PlayerKey player, uint itemId)
    {
        lock (gate)
        {
            return profiles.TryGetValue(player.ToString(), out var p) && p.Items.TryGetValue(itemId, out var c) ? c : null;
        }
    }

    public List<(uint ItemId, ItemCustomization Custom)> GetItems(PlayerKey player)
    {
        lock (gate)
            return profiles.TryGetValue(player.ToString(), out var p) ? p.Items.Select(kv => (kv.Key, kv.Value)).ToList() : [];
    }

    public DateTime? GetReceivedTime(PlayerKey player)
    {
        lock (gate) return profiles.TryGetValue(player.ToString(), out var p) ? p.ReceivedUtc : null;
    }

    public void SetProfile(PlayerKey player, IReadOnlyDictionary<uint, ItemCustomization> items, bool isTestData = false)
    {
        var clean = new Dictionary<uint, ItemCustomization>();
        foreach (var (itemId, c) in items.Take(64))
        {
            if (itemId == 0 || itemId >= 500_000) continue;
            var cleaned = new ItemCustomization
            {
                Name = TextSanitizer.CleanName(c.Name),
                FlavorText = TextSanitizer.CleanFlavor(c.FlavorText),
                UpdatedUtc = c.UpdatedUtc,
                NamedBy = TextSanitizer.CleanName(c.NamedBy),
            };
            if (!cleaned.IsEmpty()) clean[itemId] = cleaned;
        }

        lock (gate)
        {
            profiles[player.ToString()] = new RemoteProfile { Items = clean, ReceivedUtc = DateTime.UtcNow, IsTestData = isTestData };
        }
        Save();
        Changed?.Invoke();
    }

    public void Clear(bool testDataOnly = false)
    {
        lock (gate)
        {
            if (testDataOnly)
                foreach (var key in profiles.Where(kv => kv.Value.IsTestData).Select(kv => kv.Key).ToList()) profiles.Remove(key);
            else
                profiles.Clear();
        }
        Save();
        Changed?.Invoke();
    }

    public List<(string Player, RemoteProfile Profile)> Snapshot()
    {
        lock (gate) return profiles.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, RemoteProfile>>(File.ReadAllText(path), JsonOptions);
            if (loaded != null) profiles = loaded;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read remote cache; starting empty.");
        }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (gate) json = JsonSerializer.Serialize(profiles, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not write remote cache.");
        }
    }
}
