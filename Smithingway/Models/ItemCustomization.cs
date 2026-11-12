using System;
using System.Collections.Generic;

namespace Smithingway.Models;

[Serializable]
public class ItemCustomization
{
    public string Name { get; set; } = string.Empty;
    public string FlavorText { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public string NamedBy { get; set; } = string.Empty;

    public bool HasName() => !string.IsNullOrWhiteSpace(Name);
    public bool HasFlavor() => !string.IsNullOrWhiteSpace(FlavorText);
    public bool IsEmpty() => !HasName() && !HasFlavor();

    public ItemCustomization Clone() => new() { Name = Name, FlavorText = FlavorText, UpdatedUtc = UpdatedUtc, NamedBy = NamedBy };
}

[Serializable]
public class CharacterProfile
{
    public string CharacterName { get; set; } = string.Empty;
    public uint HomeWorldId { get; set; }
    public Dictionary<uint, ItemCustomization> Items { get; set; } = new();
}
