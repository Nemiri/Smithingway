namespace Smithingway.Models;

public readonly record struct PlayerKey(string Name, uint WorldId)
{
    public override string ToString() => $"{Name}@{WorldId}";

    public static bool TryParse(string? value, out PlayerKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value)) return false;
        var at = value.LastIndexOf('@');
        if (at <= 0 || !uint.TryParse(value[(at + 1)..], out var world)) return false;
        key = new PlayerKey(value[..at], world);
        return true;
    }
}
