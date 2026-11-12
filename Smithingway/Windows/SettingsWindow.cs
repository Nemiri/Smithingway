using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Smithingway.Services;
using Smithingway.Sync;

namespace Smithingway.Windows;

public sealed class SettingsWindow : Window
{
    private static readonly Vector4 Muted = new(0.62f, 0.62f, 0.66f, 1f);
    private static readonly Vector4 Warning = new(0.95f, 0.65f, 0.25f, 1f);

    private readonly Configuration config;
    private readonly SyncService sync;
    private readonly RemoteCache remote;
    private readonly DevTools devTools;

    public SettingsWindow(Configuration config, SyncService sync, RemoteCache remote, DevTools devTools)
        : base("Smithingway Settings###SmithingwaySettings")
    {
        this.config = config;
        this.sync = sync;
        this.remote = remote;
        this.devTools = devTools;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
    }

    public override void Draw()
    {
        Section("Display");
        Toggle("Show custom names in item tooltips", () => config.EnableTooltips, v => config.EnableTooltips = v);
        Toggle("Show custom names in chat item links", () => config.EnableChatLinks, v => config.EnableChatLinks = v);
        Toggle("Show flavor text in tooltips", () => config.ShowFlavorText, v => config.ShowFlavorText = v);
        Toggle("Show the original item name in tooltips", () => config.ShowOriginalName, v => config.ShowOriginalName = v);
        Toggle("Items you receive in trades keep the giver's name and story", () => config.KeepNamesFromTrades, v => config.KeepNamesFromTrades = v);

        Section("Other players");
        Toggle("Show other players' custom names and flavor text", () => config.ShowOthersCustomizations, v => config.ShowOthersCustomizations = v);
        Muted_("Turn this off to only ever see your own names.");
        ImGui.TextUnformatted($"Players in cache: {remote.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear cache")) remote.Clear();

        DrawSharing();

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Developer"))
        {
            if (ImGui.Button("Run chat link test")) devTools.RunChatLinkTest();
            ImGui.SameLine();
            if (ImGui.Button("Remove test data")) remote.Clear(testDataOnly: true);
            Muted_($"Prints chat links from you and from a fake player (\"{DevTools.TestPlayerName}\")\nso you can check names and tooltips without a second account.");
            Toggle("Debug logging (writes tooltip-dump.txt in the plugin config folder)", () => config.DebugLogging, v => config.DebugLogging = v);
        }
    }

    private string? serverUrlDraft;
    private string? connectionTestResult;
    private bool connectionTestRunning;

    private void DrawSharing()
    {
        Section("Sharing");
        serverUrlDraft ??= config.ServerUrl;

        var enabled = config.SyncEnabled;
        if (ImGui.Checkbox("Share through a Smithingway server", ref enabled))
        {
            config.SyncEnabled = enabled;
            config.Save();
            sync.ApplySettings();
        }
        if (config.SyncEnabled && !SyncService.IsValidServerUrl(config.ServerUrl))
            ImGui.TextColored(Warning, "Add a server address below and press Apply to start sharing.");

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##sw-server", "https://server address", ref serverUrlDraft, 256);
        ImGui.SameLine();
        var valid = SyncService.IsValidServerUrl(serverUrlDraft);
        ImGui.BeginDisabled(!valid || serverUrlDraft.TrimEnd('/') == config.ServerUrl.TrimEnd('/'));
        if (ImGui.Button("Apply"))
        {
            config.ServerUrl = serverUrlDraft.Trim().TrimEnd('/');
            config.Save();
            sync.ApplySettings();
            connectionTestResult = null;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!valid || connectionTestRunning);
        if (ImGui.Button("Test"))
        {
            connectionTestRunning = true;
            connectionTestResult = "Testing…";
            var url = serverUrlDraft.Trim();
            _ = ServerSyncProvider.TestConnectionAsync(url).ContinueWith(t =>
            {
                connectionTestResult = t.Result;
                connectionTestRunning = false;
            });
        }
        ImGui.EndDisabled();
        if (!valid)
            Muted_(string.IsNullOrWhiteSpace(serverUrlDraft)
                ? "Enter the address of a Smithingway server."
                : "Enter the full address, starting with https://");
        if (connectionTestResult != null) Muted_(connectionTestResult);

        ImGui.TextUnformatted($"Status: {sync.Provider.DisplayName}");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420);
        Muted_(sync.Provider.Status);
        Muted_("When sharing is on, your character name, home world and custom item names are sent to this server. " +
               "Players who link items, or talk in party/FC/linkshell/tells, are looked up there.");
        ImGui.PopTextWrapPos();
    }

    private void Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.Save();
        }
    }

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Muted, title.ToUpperInvariant());
        ImGui.Separator();
    }

    private static void Muted_(string text) => ImGui.TextColored(Muted, text);
}
