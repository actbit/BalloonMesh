using Npgsql;

namespace BalloonMesh.Server.Data;

public sealed record BalloonRecord(
    ushort MeshAddress,
    string Name,
    int GroupId,
    string GroupName,
    double? InitialLat,
    double? InitialLon,
    double? Lat,
    double? Lon,
    double? Confidence,
    byte R,
    byte G,
    byte B,
    int QueueCount,
    int EstimatedQueueCount,
    string? LastMethod,
    DateTimeOffset? UpdatedAt);

public sealed record AnchorRecord(
    byte AnchorId,
    string Name,
    double Lat,
    double Lon,
    double AltM,
    bool TimeValid,
    bool PositionValid,
    DateTimeOffset UpdatedAt);

public sealed record GroupRecord(int Id, string Name, ushort MeshGroupAddress);

public sealed record PositionSampleRecord(
    long Id,
    ushort MeshAddress,
    double Lat,
    double Lon,
    double Confidence,
    byte BatchId,
    string Method,
    DateTimeOffset MeasuredAt,
    DateTimeOffset CreatedAt);

public sealed record ShowStepRecord(
    long Id,
    string Name,
    string TargetKind,
    string Target,
    DateTimeOffset ExecuteAt,
    byte R,
    byte G,
    byte B,
    string Status);

public sealed record GatewayMeshIdentityRecord(
    string GatewayId,
    string DeviceUuid,
    ushort MeshAddress);

/// <summary>
/// PostgreSQL persistence.  The live state is small (about 50 balloons), so
/// commands are straightforward SQL over Npgsql connections.  The optional
/// schema setting lets tests use an isolated namespace in the same database.
/// </summary>
public sealed class DataStore
{
    private const string DefaultConnectionString =
        "Host=127.0.0.1;Port=5432;Database=balloonmesh;Username=balloonmesh;Password=balloonmesh;Pooling=true;Maximum Pool Size=20";

    private readonly object _writeGate = new();
    private readonly string _connectionString;
    private readonly string? _schema;

    public DataStore(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetConnectionString("BalloonMesh")
            ?? Environment.GetEnvironmentVariable("BALLOONMESH_CONNECTION")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? DefaultConnectionString;
        _schema = configuration["Data:Schema"];
        if (!string.IsNullOrWhiteSpace(_schema))
        {
            ValidateSchema(_schema);
        }

        var builder = new NpgsqlConnectionStringBuilder(configured);
        if (!string.IsNullOrWhiteSpace(_schema))
        {
            builder.SearchPath = _schema;
        }
        _connectionString = builder.ConnectionString;
        DatabaseDescription = $"{builder.Host}:{builder.Port}/{builder.Database}" +
            (string.IsNullOrWhiteSpace(_schema) ? string.Empty : $" schema={_schema}");
    }

    public string DatabaseDescription { get; }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void ValidateSchema(string schema)
    {
        foreach (var character in schema)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                throw new ArgumentException($"Invalid database schema name '{schema}'.");
            }
        }
    }

    public void Initialize()
    {
        using var connection = Open();
        if (!string.IsNullOrWhiteSpace(_schema))
        {
            using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{_schema}\"";
            schemaCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS balloon_groups(
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                mesh_group_address INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS balloons(
                mesh_address INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                group_id INTEGER NOT NULL REFERENCES balloon_groups(id),
                initial_lat DOUBLE PRECISION NULL,
                initial_lon DOUBLE PRECISION NULL,
                lat DOUBLE PRECISION NULL,
                lon DOUBLE PRECISION NULL,
                confidence DOUBLE PRECISION NULL,
                r SMALLINT NOT NULL DEFAULT 0,
                g SMALLINT NOT NULL DEFAULT 0,
                b SMALLINT NOT NULL DEFAULT 0,
                queue_count INTEGER NOT NULL DEFAULT 0,
                estimated_queue_count INTEGER NOT NULL DEFAULT 0,
                last_method TEXT NULL,
                updated_at TIMESTAMPTZ NULL);
            CREATE TABLE IF NOT EXISTS anchors(
                anchor_id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                lat DOUBLE PRECISION NOT NULL,
                lon DOUBLE PRECISION NOT NULL,
                alt_m DOUBLE PRECISION NOT NULL,
                time_valid BOOLEAN NOT NULL DEFAULT FALSE,
                position_valid BOOLEAN NOT NULL DEFAULT FALSE,
                updated_at TIMESTAMPTZ NOT NULL);
            CREATE TABLE IF NOT EXISTS positions(
                id BIGSERIAL PRIMARY KEY,
                mesh_address INTEGER NOT NULL,
                lat DOUBLE PRECISION NOT NULL,
                lon DOUBLE PRECISION NOT NULL,
                confidence DOUBLE PRECISION NOT NULL,
                batch_id SMALLINT NOT NULL,
                method TEXT NOT NULL,
                measured_at TIMESTAMPTZ NOT NULL,
                created_at TIMESTAMPTZ NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_positions_balloon
                ON positions(mesh_address, id DESC);
            CREATE TABLE IF NOT EXISTS show_steps(
                id BIGSERIAL PRIMARY KEY,
                name TEXT NOT NULL,
                target_kind TEXT NOT NULL,
                target TEXT NOT NULL,
                execute_at TIMESTAMPTZ NOT NULL,
                r SMALLINT NOT NULL,
                g SMALLINT NOT NULL,
                b SMALLINT NOT NULL,
                status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS gateway_mesh_addresses(
                gateway_id TEXT PRIMARY KEY,
                device_uuid TEXT NOT NULL UNIQUE,
                mesh_address INTEGER NOT NULL UNIQUE,
                updated_at TIMESTAMPTZ NOT NULL);
            """;
        command.ExecuteNonQuery();
        Seed(connection);
    }

    /// <summary>
    /// Clears queue counters that only describe the previous server process.
    /// The estimate must be rebuilt from commands and QUEUE_STATUS reports.
    /// </summary>
    public void ResetRuntimeQueueState()
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE balloons SET queue_count = 0, estimated_queue_count = 0";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Test/demo helper: clears all rows and re-creates the seed data.</summary>
    public void Reset()
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                TRUNCATE TABLE positions, show_steps, gateway_mesh_addresses,
                    balloons, anchors, balloon_groups
                RESTART IDENTITY CASCADE;
                """;
            command.ExecuteNonQuery();
            Seed(connection);
        }
    }

    /// <summary>
    /// Returns the stable development Mesh identity for a gateway, allocating
    /// the first free gateway address when the device is seen for the first
    /// time.  The table lock and unique constraints make concurrent requests
    /// safe across multiple server instances.
    /// </summary>
    public GatewayMeshIdentityRecord GetOrAllocateGatewayMeshIdentity(
        string gatewayId, string deviceUuid)
    {
        if (string.IsNullOrWhiteSpace(gatewayId) || gatewayId.Length > 64)
        {
            throw new ArgumentException("Gateway ID is required.", nameof(gatewayId));
        }
        if (string.IsNullOrWhiteSpace(deviceUuid) || deviceUuid.Length > 64)
        {
            throw new ArgumentException("Device UUID is required.", nameof(deviceUuid));
        }

        gatewayId = gatewayId.Trim();
        deviceUuid = deviceUuid.Trim().ToUpperInvariant();

        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            using (var tableLock = connection.CreateCommand())
            {
                tableLock.Transaction = transaction;
                tableLock.CommandText = "LOCK TABLE gateway_mesh_addresses IN EXCLUSIVE MODE";
                tableLock.ExecuteNonQuery();
            }

            GatewayMeshIdentityRecord? existingIdentity = null;
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT gateway_id, device_uuid, mesh_address
                    FROM gateway_mesh_addresses
                    WHERE gateway_id = @gateway_id OR device_uuid = @device_uuid;
                    """;
                existing.Parameters.AddWithValue("gateway_id", gatewayId);
                existing.Parameters.AddWithValue("device_uuid", deviceUuid);
                using (var reader = existing.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var existingGatewayId = reader.GetString(0);
                        var existingDeviceUuid = reader.GetString(1);
                        var existingAddress = (ushort)reader.GetInt32(2);
                        if (!string.Equals(existingGatewayId, gatewayId, StringComparison.Ordinal) ||
                            !string.Equals(existingDeviceUuid, deviceUuid, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "Gateway ID and device UUID are already assigned to different devices.");
                        }

                        existingIdentity = new GatewayMeshIdentityRecord(
                            existingGatewayId, existingDeviceUuid, existingAddress);
                    }
                }
            }

            if (existingIdentity is not null)
            {
                transaction.Commit();
                return existingIdentity;
            }

            var address = 0x4000;
            using (var used = connection.CreateCommand())
            {
                used.Transaction = transaction;
                used.CommandText = """
                    SELECT mesh_address
                    FROM gateway_mesh_addresses
                    ORDER BY mesh_address;
                    """;
                using var reader = used.ExecuteReader();
                while (reader.Read())
                {
                    var usedAddress = reader.GetInt32(0);
                    if (usedAddress == address)
                    {
                        address++;
                    }
                    else if (usedAddress > address)
                    {
                        break;
                    }
                }
            }

            if (address > 0x7FFF)
            {
                throw new InvalidOperationException("No development gateway Mesh address is available.");
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO gateway_mesh_addresses(
                        gateway_id, device_uuid, mesh_address, updated_at)
                    VALUES(@gateway_id, @device_uuid, @mesh_address, @updated_at);
                    """;
                insert.Parameters.AddWithValue("gateway_id", gatewayId);
                insert.Parameters.AddWithValue("device_uuid", deviceUuid);
                insert.Parameters.AddWithValue("mesh_address", (int)address);
                insert.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new GatewayMeshIdentityRecord(gatewayId, deviceUuid, (ushort)address);
        }
    }

    private static void Seed(NpgsqlConnection connection)
    {
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM balloons";
            var balloons = Convert.ToInt32(count.ExecuteScalar());
            if (balloons > 0)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        var groups = new (int Id, string Name, ushort Address)[]
        {
            (1, "Zone A", 0xC100),
            (2, "Zone B", 0xC101),
            (3, "Zone C", 0xC102),
            (4, "Zone D", 0xC103),
            (5, "All balloons", 0xC010)
        };
        foreach (var group in groups)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO balloon_groups(id, name, mesh_group_address)
                VALUES(@id, @name, @address)
                ON CONFLICT (id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("id", group.Id);
            insert.Parameters.AddWithValue("name", group.Name);
            insert.Parameters.AddWithValue("address", (int)group.Address);
            insert.ExecuteNonQuery();
        }

        // Odaiba, Tokyo.  A 10 x 5 grid with roughly 35 m spacing.
        const double centerLat = 35.6300;
        const double centerLon = 139.7750;
        for (var i = 0; i < 50; i++)
        {
            var row = i / 10;
            var column = i % 10;
            var lat = centerLat + (row - 2) * 0.00032;
            var lon = centerLon + (column - 4.5) * 0.00040;
            var groupId = i % 4 + 1;
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO balloons(
                    mesh_address, name, group_id, initial_lat, initial_lon,
                    lat, lon, confidence, r, g, b, queue_count,
                    estimated_queue_count, last_method, updated_at)
                VALUES(@addr, @name, @group, @lat, @lon, @lat, @lon, 0.7, 0, 0, 0, 0, 0,
                       'seed', @updated)
                ON CONFLICT (mesh_address) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("addr", 0x0101 + i);
            insert.Parameters.AddWithValue("name", $"B-{i + 1:00}");
            insert.Parameters.AddWithValue("group", groupId);
            insert.Parameters.AddWithValue("lat", lat);
            insert.Parameters.AddWithValue("lon", lon);
            insert.Parameters.AddWithValue("updated", DateTimeOffset.UtcNow);
            insert.ExecuteNonQuery();
        }

        var anchors = new (byte Id, string Name, double Lat, double Lon, double Alt)[]
        {
            (1, "Anchor 1 / NW", 35.6320, 139.7725, 8.0),
            (2, "Anchor 2 / NE", 35.6320, 139.7775, 8.0),
            (3, "Anchor 3 / SE", 35.6280, 139.7775, 8.0),
            (4, "Anchor 4 / SW", 35.6280, 139.7725, 8.0)
        };
        foreach (var anchor in anchors)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO anchors(anchor_id, name, lat, lon, alt_m, time_valid, position_valid, updated_at)
                VALUES(@id, @name, @lat, @lon, @alt, TRUE, TRUE, @updated)
                ON CONFLICT (anchor_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("id", (int)anchor.Id);
            insert.Parameters.AddWithValue("name", anchor.Name);
            insert.Parameters.AddWithValue("lat", anchor.Lat);
            insert.Parameters.AddWithValue("lon", anchor.Lon);
            insert.Parameters.AddWithValue("alt", anchor.Alt);
            insert.Parameters.AddWithValue("updated", DateTimeOffset.UtcNow);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<BalloonRecord> GetBalloons()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.mesh_address, b.name, b.group_id, COALESCE(g.name, ''),
                   b.initial_lat, b.initial_lon, b.lat, b.lon, b.confidence,
                   b.r, b.g, b.b, b.queue_count, b.estimated_queue_count,
                   b.last_method, b.updated_at
            FROM balloons b LEFT JOIN balloon_groups g ON g.id = b.group_id
            ORDER BY b.mesh_address;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<BalloonRecord>();
        while (reader.Read())
        {
            result.Add(ReadBalloon(reader));
        }
        return result;
    }

    public BalloonRecord? GetBalloon(ushort meshAddress)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.mesh_address, b.name, b.group_id, COALESCE(g.name, ''),
                   b.initial_lat, b.initial_lon, b.lat, b.lon, b.confidence,
                   b.r, b.g, b.b, b.queue_count, b.estimated_queue_count,
                   b.last_method, b.updated_at
            FROM balloons b LEFT JOIN balloon_groups g ON g.id = b.group_id
            WHERE b.mesh_address = @addr;
            """;
        command.Parameters.AddWithValue("addr", (int)meshAddress);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBalloon(reader) : null;
    }

    private static BalloonRecord ReadBalloon(NpgsqlDataReader reader) => new(
        (ushort)reader.GetInt32(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetDouble(5),
        reader.IsDBNull(6) ? null : reader.GetDouble(6),
        reader.IsDBNull(7) ? null : reader.GetDouble(7),
        reader.IsDBNull(8) ? null : reader.GetDouble(8),
        (byte)reader.GetInt16(9),
        (byte)reader.GetInt16(10),
        (byte)reader.GetInt16(11),
        reader.GetInt32(12),
        reader.GetInt32(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15));

    public IReadOnlyList<GroupRecord> GetGroups()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, mesh_group_address FROM balloon_groups ORDER BY id";
        using var reader = command.ExecuteReader();
        var result = new List<GroupRecord>();
        while (reader.Read())
        {
            result.Add(new GroupRecord(reader.GetInt32(0), reader.GetString(1),
                (ushort)reader.GetInt32(2)));
        }
        return result;
    }

    public GroupRecord? GetGroup(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, mesh_group_address FROM balloon_groups WHERE id = @id";
        command.Parameters.AddWithValue("id", id);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new GroupRecord(reader.GetInt32(0), reader.GetString(1), (ushort)reader.GetInt32(2))
            : null;
    }

    public IReadOnlyList<BalloonRecord> GetBalloonsInGroup(int groupId) =>
        GetBalloons().Where(balloon => balloon.GroupId == groupId).ToArray();

    public GroupRecord CreateGroup(string name, ushort meshGroupAddress)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            int id;
            using (var next = connection.CreateCommand())
            {
                next.Transaction = transaction;
                next.CommandText = "SELECT COALESCE(MAX(id), 0) + 1 FROM balloon_groups";
                id = Convert.ToInt32(next.ExecuteScalar());
            }
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO balloon_groups(id, name, mesh_group_address)
                    VALUES(@id, @name, @address);
                    """;
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("name", name);
                insert.Parameters.AddWithValue("address", (int)meshGroupAddress);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new GroupRecord(id, name, meshGroupAddress);
        }
    }

    public bool DeleteGroup(int id)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM balloon_groups g
                WHERE g.id = @id
                  AND g.mesh_group_address <> 0xC010
                  AND NOT EXISTS (SELECT 1 FROM balloons b WHERE b.group_id = g.id);
                """;
            command.Parameters.AddWithValue("id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool AssignBalloonToGroup(ushort meshAddress, int groupId)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE balloons SET group_id = @group
                WHERE mesh_address = @addr
                  AND EXISTS (SELECT 1 FROM balloon_groups g
                              WHERE g.id = @group AND g.mesh_group_address <> 0xC010);
                """;
            command.Parameters.AddWithValue("group", groupId);
            command.Parameters.AddWithValue("addr", (int)meshAddress);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<AnchorRecord> GetAnchors()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anchor_id, name, lat, lon, alt_m, time_valid, position_valid, updated_at
            FROM anchors ORDER BY anchor_id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<AnchorRecord>();
        while (reader.Read())
        {
            result.Add(new AnchorRecord(
                (byte)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return result;
    }

    public void UpsertAnchor(AnchorRecord anchor)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO anchors(anchor_id, name, lat, lon, alt_m, time_valid, position_valid, updated_at)
                VALUES(@id, @name, @lat, @lon, @alt, @time, @position, @updated)
                ON CONFLICT (anchor_id) DO UPDATE SET
                    name = excluded.name, lat = excluded.lat, lon = excluded.lon,
                    alt_m = excluded.alt_m, time_valid = excluded.time_valid,
                    position_valid = excluded.position_valid, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("id", (int)anchor.AnchorId);
            command.Parameters.AddWithValue("name", anchor.Name);
            command.Parameters.AddWithValue("lat", anchor.Lat);
            command.Parameters.AddWithValue("lon", anchor.Lon);
            command.Parameters.AddWithValue("alt", anchor.AltM);
            command.Parameters.AddWithValue("time", anchor.TimeValid);
            command.Parameters.AddWithValue("position", anchor.PositionValid);
            command.Parameters.AddWithValue("updated", anchor.UpdatedAt);
            command.ExecuteNonQuery();
        }
    }

    public void SetInitialPosition(ushort meshAddress, double lat, double lon)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE balloons
                SET initial_lat = @lat, initial_lon = @lon,
                    lat = CASE WHEN last_method IS NULL OR last_method IN ('seed', 'initial')
                               THEN @lat ELSE lat END,
                    lon = CASE WHEN last_method IS NULL OR last_method IN ('seed', 'initial')
                               THEN @lon ELSE lon END,
                    last_method = CASE WHEN last_method IS NULL OR last_method IN ('seed', 'initial')
                                       THEN 'initial' ELSE last_method END,
                    updated_at = CASE WHEN last_method IS NULL OR last_method IN ('seed', 'initial')
                                      THEN @updated ELSE updated_at END
                WHERE mesh_address = @addr;
                """;
            command.Parameters.AddWithValue("lat", lat);
            command.Parameters.AddWithValue("lon", lon);
            command.Parameters.AddWithValue("addr", (int)meshAddress);
            command.Parameters.AddWithValue("updated", DateTimeOffset.UtcNow);
            command.ExecuteNonQuery();
        }
    }

    public void UpdatePosition(ushort meshAddress, double lat, double lon, double confidence,
        byte batchId, string method, DateTimeOffset measuredAt)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE balloons
                    SET lat = @lat, lon = @lon, confidence = @confidence,
                        last_method = @method, updated_at = @updated
                    WHERE mesh_address = @addr;
                    """;
                update.Parameters.AddWithValue("lat", lat);
                update.Parameters.AddWithValue("lon", lon);
                update.Parameters.AddWithValue("confidence", confidence);
                update.Parameters.AddWithValue("method", method);
                update.Parameters.AddWithValue("updated", DateTimeOffset.UtcNow);
                update.Parameters.AddWithValue("addr", (int)meshAddress);
                update.ExecuteNonQuery();
            }
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO positions(
                        mesh_address, lat, lon, confidence, batch_id, method, measured_at, created_at)
                    VALUES(@addr, @lat, @lon, @confidence, @batch, @method, @measured, @created);
                    """;
                insert.Parameters.AddWithValue("addr", (int)meshAddress);
                insert.Parameters.AddWithValue("lat", lat);
                insert.Parameters.AddWithValue("lon", lon);
                insert.Parameters.AddWithValue("confidence", confidence);
                insert.Parameters.AddWithValue("batch", (short)batchId);
                insert.Parameters.AddWithValue("method", method);
                insert.Parameters.AddWithValue("measured", measuredAt);
                insert.Parameters.AddWithValue("created", DateTimeOffset.UtcNow);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void UpdateQueueCount(ushort meshAddress, int queueCount)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE balloons SET queue_count = @count, estimated_queue_count = @count,
                    updated_at = @updated WHERE mesh_address = @addr;
                """;
            command.Parameters.AddWithValue("count", queueCount);
            command.Parameters.AddWithValue("updated", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("addr", (int)meshAddress);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateEstimatedQueueCount(ushort meshAddress, int estimated)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE balloons SET estimated_queue_count = @count WHERE mesh_address = @addr;
                """;
            command.Parameters.AddWithValue("count", estimated);
            command.Parameters.AddWithValue("addr", (int)meshAddress);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateColor(ushort meshAddress, byte r, byte g, byte b)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE balloons SET r = @r, g = @g, b = @b WHERE mesh_address = @addr;
                """;
            command.Parameters.AddWithValue("r", (short)r);
            command.Parameters.AddWithValue("g", (short)g);
            command.Parameters.AddWithValue("b", (short)b);
            command.Parameters.AddWithValue("addr", (int)meshAddress);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PositionSampleRecord> GetHistory(ushort meshAddress, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mesh_address, lat, lon, confidence, batch_id, method, measured_at, created_at
            FROM positions WHERE mesh_address = @addr ORDER BY id DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("addr", (int)meshAddress);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var result = new List<PositionSampleRecord>();
        while (reader.Read())
        {
            result.Add(new PositionSampleRecord(
                reader.GetInt64(0),
                (ushort)reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                (byte)reader.GetInt16(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return result;
    }

    public long AddShowStep(string name, string targetKind, string target,
        DateTimeOffset executeAt, byte r, byte g, byte b, string status)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO show_steps(name, target_kind, target, execute_at, r, g, b, status)
                VALUES(@name, @kind, @target, @execute, @r, @g, @b, @status)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("kind", targetKind);
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("execute", executeAt);
            command.Parameters.AddWithValue("r", (short)r);
            command.Parameters.AddWithValue("g", (short)g);
            command.Parameters.AddWithValue("b", (short)b);
            command.Parameters.AddWithValue("status", status);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Marks queued steps that were already due as expired.</summary>
    public int ExpireQueuedShowSteps(DateTimeOffset before)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE show_steps SET status = 'expired'
                WHERE status = 'queued' AND execute_at <= @before;
                """;
            command.Parameters.AddWithValue("before", before);
            return command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ShowStepRecord> GetFutureShowSteps(string status, DateTimeOffset after)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, target_kind, target, execute_at, r, g, b, status
            FROM show_steps
            WHERE status = @status AND execute_at > @after
            ORDER BY execute_at, id;
            """;
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("after", after);
        using var reader = command.ExecuteReader();
        var result = new List<ShowStepRecord>();
        while (reader.Read())
        {
            result.Add(ReadShowStep(reader));
        }
        return result;
    }

    public IReadOnlyList<ShowStepRecord> GetPendingShowSteps(DateTimeOffset after)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, target_kind, target, execute_at, r, g, b, status
            FROM show_steps
            WHERE status = 'queued' AND execute_at > @after
            ORDER BY execute_at, id;
            """;
        command.Parameters.AddWithValue("after", after);
        using var reader = command.ExecuteReader();
        var result = new List<ShowStepRecord>();
        while (reader.Read())
        {
            result.Add(ReadShowStep(reader));
        }
        return result;
    }

    private static ShowStepRecord ReadShowStep(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        (byte)reader.GetInt16(5), (byte)reader.GetInt16(6), (byte)reader.GetInt16(7),
        reader.GetString(8));

    public ShowStepRecord? GetShowStep(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, target_kind, target, execute_at, r, g, b, status
            FROM show_steps WHERE id = @id;
            """;
        command.Parameters.AddWithValue("id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadShowStep(reader) : null;
    }

    public void UpdateShowStepStatus(long id, string status)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE show_steps SET status = @status WHERE id = @id";
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("id", id);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ShowStepRecord> GetShowSteps(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, target_kind, target, execute_at, r, g, b, status
            FROM show_steps ORDER BY id DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var result = new List<ShowStepRecord>();
        while (reader.Read())
        {
            result.Add(ReadShowStep(reader));
        }
        return result;
    }
}
