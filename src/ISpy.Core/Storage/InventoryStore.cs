using ISpy.Core.Model;
using ISpy.Core.Security;
using Microsoft.Data.Sqlite;

namespace ISpy.Core.Storage;

/// <summary>
/// The local cache of everything the app already knows: devices, their channels, saved layouts and
/// settings. The startup path reads from here and nothing else, so the window can paint before a
/// single network packet is sent.
/// </summary>
public sealed class InventoryStore : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly ISecretProtector _protector;

    private InventoryStore(SqliteConnection connection, ISecretProtector protector)
    {
        _connection = connection;
        _protector = protector;
    }

    public static InventoryStore Open(string databasePath, ISecretProtector protector)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());

        connection.Open();

        // WAL keeps reads from blocking behind the background discovery writes.
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");

        var store = new InventoryStore(connection, protector);
        store.Migrate();
        return store;
    }

    private void Migrate()
    {
        var current = Convert.ToInt32(ScalarObject("PRAGMA user_version;") ?? 0);
        if (current >= SchemaVersion) return;

        if (current < 1)
        {
            Execute(_connection, """
                CREATE TABLE IF NOT EXISTS devices (
                    id            TEXT PRIMARY KEY,
                    host          TEXT NOT NULL,
                    display_name  TEXT NOT NULL DEFAULT '',
                    http_port     INTEGER NOT NULL DEFAULT 80,
                    rtsp_port     INTEGER NOT NULL DEFAULT 554,
                    model         TEXT,
                    serial_number TEXT,
                    firmware      TEXT,
                    username      TEXT,
                    secret        BLOB,
                    source        INTEGER NOT NULL DEFAULT 0,
                    added_utc     TEXT NOT NULL,
                    last_seen_utc TEXT
                );

                CREATE TABLE IF NOT EXISTS channels (
                    device_id   TEXT NOT NULL,
                    number      INTEGER NOT NULL,
                    name        TEXT NOT NULL DEFAULT '',
                    enabled     INTEGER NOT NULL DEFAULT 1,
                    supports_ptz INTEGER NOT NULL DEFAULT 0,
                    main_codec  TEXT,
                    sub_codec   TEXT,
                    is_offline  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (device_id, number),
                    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """);
        }

        Execute(_connection, $"PRAGMA user_version={SchemaVersion};");
    }

    // ---- devices ---------------------------------------------------------

    /// <summary>
    /// Inserts or updates a device. Pass <paramref name="password"/> to (re)encrypt the stored
    /// secret; pass null to leave any existing secret untouched, so a rediscovery that refreshes
    /// firmware/model info cannot wipe working credentials.
    /// </summary>
    public void UpsertDevice(Device device, string? password = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices (id, host, display_name, http_port, rtsp_port, model, serial_number,
                                 firmware, username, secret, source, added_utc, last_seen_utc)
            VALUES ($id, $host, $name, $http, $rtsp, $model, $serial, $firmware, $user,
                    $secret, $source, $added, $seen)
            ON CONFLICT(id) DO UPDATE SET
                host          = excluded.host,
                display_name  = excluded.display_name,
                http_port     = excluded.http_port,
                rtsp_port     = excluded.rtsp_port,
                model         = COALESCE(excluded.model, devices.model),
                serial_number = COALESCE(excluded.serial_number, devices.serial_number),
                firmware      = COALESCE(excluded.firmware, devices.firmware),
                username      = COALESCE(excluded.username, devices.username),
                secret        = COALESCE(excluded.secret, devices.secret),
                source        = excluded.source,
                last_seen_utc = excluded.last_seen_utc;
            """;

        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$host", device.Host);
        command.Parameters.AddWithValue("$name", device.DisplayName);
        command.Parameters.AddWithValue("$http", device.HttpPort);
        command.Parameters.AddWithValue("$rtsp", device.RtspPort);
        command.Parameters.AddWithValue("$model", (object?)device.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$serial", (object?)device.SerialNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$firmware", (object?)device.FirmwareVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$user", (object?)device.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret",
            password is null ? DBNull.Value : _protector.Protect(password));
        command.Parameters.AddWithValue("$source", (int)device.Source);
        command.Parameters.AddWithValue("$added", device.AddedUtc.ToString("O"));
        command.Parameters.AddWithValue("$seen",
            (object?)device.LastSeenUtc?.ToString("O") ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Device> GetDevices()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, host, display_name, http_port, rtsp_port, model, serial_number, firmware,
                   username, source, added_utc, last_seen_utc
            FROM devices ORDER BY display_name, host;
            """;

        var results = new List<Device>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Device
            {
                Id = reader.GetString(0),
                Host = reader.GetString(1),
                DisplayName = reader.GetString(2),
                HttpPort = reader.GetInt32(3),
                RtspPort = reader.GetInt32(4),
                Model = reader.IsDBNull(5) ? null : reader.GetString(5),
                SerialNumber = reader.IsDBNull(6) ? null : reader.GetString(6),
                FirmwareVersion = reader.IsDBNull(7) ? null : reader.GetString(7),
                Username = reader.IsDBNull(8) ? null : reader.GetString(8),
                Source = (DiscoverySource)reader.GetInt32(9),
                AddedUtc = DateTimeOffset.Parse(reader.GetString(10)),
                LastSeenUtc = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            });
        }

        return results;
    }

    public void DeleteDevice(string deviceId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM channels WHERE device_id = $id; DELETE FROM devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the decrypted password, or null when none is stored for this device.</summary>
    public string? GetPassword(string deviceId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT secret FROM devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);

        return command.ExecuteScalar() is byte[] blob && blob.Length > 0
            ? _protector.Unprotect(blob)
            : null;
    }

    // ---- channels --------------------------------------------------------

    /// <summary>
    /// Replaces the cached channel list for a device in one transaction. Channels the device no
    /// longer reports are removed, so a camera unplugged from the NVR stops appearing in the grid.
    /// </summary>
    public void ReplaceChannels(string deviceId, IEnumerable<Channel> channels)
    {
        using var transaction = _connection.BeginTransaction();

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM channels WHERE device_id = $id;";
            delete.Parameters.AddWithValue("$id", deviceId);
            delete.ExecuteNonQuery();
        }

        foreach (var channel in channels)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO channels (device_id, number, name, enabled, supports_ptz,
                                      main_codec, sub_codec, is_offline)
                VALUES ($device, $number, $name, $enabled, $ptz, $main, $sub, $offline);
                """;
            insert.Parameters.AddWithValue("$device", deviceId);
            insert.Parameters.AddWithValue("$number", channel.Number);
            insert.Parameters.AddWithValue("$name", channel.Name);
            insert.Parameters.AddWithValue("$enabled", channel.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$ptz", channel.SupportsPtz ? 1 : 0);
            insert.Parameters.AddWithValue("$main", (object?)channel.MainCodec ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sub", (object?)channel.SubCodec ?? DBNull.Value);
            insert.Parameters.AddWithValue("$offline", channel.IsOffline ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<Channel> GetChannels(string deviceId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, number, name, enabled, supports_ptz, main_codec, sub_codec, is_offline
            FROM channels WHERE device_id = $id ORDER BY number;
            """;
        command.Parameters.AddWithValue("$id", deviceId);

        var results = new List<Channel>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Channel
            {
                DeviceId = reader.GetString(0),
                Number = reader.GetInt32(1),
                Name = reader.GetString(2),
                Enabled = reader.GetInt32(3) != 0,
                SupportsPtz = reader.GetInt32(4) != 0,
                MainCodec = reader.IsDBNull(5) ? null : reader.GetString(5),
                SubCodec = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsOffline = reader.GetInt32(7) != 0,
            });
        }

        return results;
    }

    // ---- settings --------------------------------------------------------

    public string? GetSetting(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    // ---- helpers ---------------------------------------------------------

    private object? ScalarObject(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
    }
}
