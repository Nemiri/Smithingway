using System;
using System.Collections.Generic;
using Smithingway.Models;

namespace Smithingway.Sync;

public interface ISyncProvider : IDisposable
{
    string DisplayName { get; }

    string Status { get; }

    void PublishLocalProfile(PlayerKey self, IReadOnlyDictionary<uint, ItemCustomization> items);

    void RequestProfile(PlayerKey player);
}

public sealed class LocalOnlySyncProvider : ISyncProvider
{
    public string DisplayName => "Local only";
    public string Status => "Not sharing. Your names are only visible to you.";
    public void PublishLocalProfile(PlayerKey self, IReadOnlyDictionary<uint, ItemCustomization> items) { }
    public void RequestProfile(PlayerKey player) { }
    public void Dispose() { }
}
