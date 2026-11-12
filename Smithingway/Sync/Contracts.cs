using System;
using System.Collections.Generic;

namespace Smithingway.Contracts;

public sealed record HealthResponse(string Status, int Api, string ServerId);

public sealed record RegisterRequest(string Name, uint WorldId);

public sealed record RegisterResponse(string Token);

public sealed record ItemEntry(string Name, string FlavorText, string? NamedBy = null);

public sealed record ProfileDto(Dictionary<uint, ItemEntry> Items, DateTime UpdatedUtc);

public sealed record ChangedPlayer(string Name, uint WorldId, DateTime UpdatedUtc);

public sealed record ChangesResponse(DateTime ServerNowUtc, List<ChangedPlayer> Changed);

public static class ApiLimits
{
    public const int MaxItemsPerProfile = 64;
    public const uint MaxItemId = 499_999;
    public const int MaxRequestBytes = 64 * 1024;
}
