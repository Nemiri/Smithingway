using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Smithingway.Models;
using Smithingway.Sync;

namespace Smithingway.Services;

public sealed unsafe class InspectService : IDisposable
{
    private const string AddonName = "CharacterInspect";

    private readonly Configuration config;
    private readonly CustomizationStore store;
    private readonly SyncService sync;

    public InspectService(Configuration config, CustomizationStore store, SyncService sync)
    {
        this.config = config;
        this.store = store;
        this.sync = sync;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnInspectChanged);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnInspectChanged);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnInspectChanged);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnInspectChanged);
    }

    public static PlayerKey? CurrentlyInspected
    {
        get
        {
            var ui = UIState.Instance();
            if (ui == null) return null;
            var name = ui->Inspect.NameString;
            var world = ui->Inspect.WorldId;
            return string.IsNullOrWhiteSpace(name) || world == 0 ? null : new PlayerKey(name, world);
        }
    }

    private void OnInspectChanged(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (CurrentlyInspected is not { } player) return;
            if (config.DebugLogging) TooltipService.DebugLine($"inspect: {type} {player}");
            if (player != store.LocalKey) sync.EnsureProfile(player);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to handle Examine window update.");
        }
    }
}
