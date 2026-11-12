using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Smithingway.Models;

namespace Smithingway;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool EnableTooltips { get; set; } = true;
    public bool EnableChatLinks { get; set; } = true;
    public bool ShowOthersCustomizations { get; set; } = true;
    public bool ShowFlavorText { get; set; } = true;
    public bool ShowOriginalName { get; set; } = true;

    public bool KeepNamesFromTrades { get; set; } = true;
    public bool DebugLogging { get; set; } = false;

    public bool SyncEnabled { get; set; } = false;
    public string ServerUrl { get; set; } = "";

    public Dictionary<string, string> ServerTokens { get; set; } = new();

    public Dictionary<ulong, CharacterProfile> Profiles { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
