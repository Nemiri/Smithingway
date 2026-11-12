using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Smithingway.Contracts;

namespace Smithingway.Server;

public sealed class ProfileStore
{
    private readonly string connectionString;

    public ProfileStore(string databasePath)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var db = Open();
        Exec(db, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS characters (
                world_id    INTEGER NOT NULL,
                name        TEXT    NOT NULL COLLATE NOCASE,
                token_hash  BLOB    NOT NULL,
                created_utc TEXT    NOT NULL,
                PRIMARY KEY (world_id, name)
            );
            CREATE TABLE IF NOT EXISTS profiles (
                world_id    INTEGER NOT NULL,
                name        TEXT    NOT NULL COLLATE NOCASE,
                items_json  TEXT    NOT NULL,
                updated_utc TEXT    NOT NULL,
                PRIMARY KEY (world_id, name)
            );
            CREATE INDEX IF NOT EXISTS profiles_updated ON profiles (updated_utc);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);

        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO meta (key, value) VALUES ('server_id', $id)";
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        insert.ExecuteNonQuery();

        using var select = db.CreateCommand();
        select.CommandText = "SELECT value FROM meta WHERE key = 'server_id'";
        ServerId = (string)select.ExecuteScalar()!;
    }

    public string ServerId { get; }

    public string? TryRegister(string name, uint worldId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO characters (world_id, name, token_hash, created_utc) VALUES ($w, $n, $h, $t)";
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$h", Hash(token));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1 ? token : null;
    }

    public bool CheckToken(string name, uint worldId, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT token_hash FROM characters WHERE world_id = $w AND name = $n";
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is byte[] stored && CryptographicOperations.FixedTimeEquals(stored, Hash(token));
    }

    public ProfileDto? GetProfile(string name, uint worldId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT items_json, updated_utc FROM profiles WHERE world_id = $w AND name = $n";
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var items = JsonSerializer.Deserialize<Dictionary<uint, ItemEntry>>(reader.GetString(0)) ?? new();
        return new ProfileDto(items, DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
    }

    public void SaveProfile(string name, uint worldId, Dictionary<uint, ItemEntry> items)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles (world_id, name, items_json, updated_utc) VALUES ($w, $n, $j, $t)
            ON CONFLICT (world_id, name) DO UPDATE SET items_json = excluded.items_json, updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(items));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void DeleteProfile(string name, uint worldId) => SaveProfile(name, worldId, new Dictionary<uint, ItemEntry>());

    public List<ChangedPlayer> ChangedSince(DateTime sinceUtc, int limit = 500)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, world_id, updated_utc FROM profiles WHERE updated_utc > $t ORDER BY updated_utc LIMIT $l";
        cmd.Parameters.AddWithValue("$t", sinceUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$l", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<ChangedPlayer>();
        while (reader.Read())
            result.Add(new ChangedPlayer(reader.GetString(0), (uint)reader.GetInt64(1), DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()));
        return result;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
