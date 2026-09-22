using RecordingCopyNet.Security;

namespace RecordingCopyNet.Data;

// Single-row-per-type table (id=1), upserted. Mirrors lib/store.js: field
// names/required/encrypted flags come from CredentialSchema, columns are
// migrated in (added if missing) the same way store.js's migrateSchema()
// ALTERs columns onto credentials_<type> tables.
public class CredentialStore : ICredentialStore
{
    private readonly Db _db;
    private readonly IFieldCipher _cipher;
    private readonly HashSet<CredentialType> _migrated = new();

    public CredentialStore(Db db, IFieldCipher cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    public bool Exists(CredentialType type) => Load(type) != null;

    public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields)
    {
        EnsureColumns(type);
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();

        var columns = new List<string> { "id" };
        var placeholders = new List<string> { "1" };
        using var cmd = conn.CreateCommand();

        foreach (var def in CredentialSchema.Fields[type])
        {
            if (!fields.TryGetValue(def.Name, out var value) || value is null) continue;
            var column = CredentialSchema.ColumnName(def);
            columns.Add(column);
            var paramName = $"@{column}";
            placeholders.Add(paramName);
            cmd.Parameters.AddWithValue(paramName, def.Encrypted ? _cipher.Encrypt(value) : value);
        }

        cmd.CommandText = $"INSERT OR REPLACE INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)})";
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, string?>? Load(CredentialType type)
    {
        EnsureColumns(type);
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var result = new Dictionary<string, string?>();
        foreach (var def in CredentialSchema.Fields[type])
        {
            var column = CredentialSchema.ColumnName(def);
            var ordinal = reader.GetOrdinal(column);
            if (reader.IsDBNull(ordinal)) { result[def.Name] = null; continue; }
            var raw = reader.GetString(ordinal);
            result[def.Name] = def.Encrypted ? _cipher.Decrypt(raw) : raw;
        }
        return result;
    }

    public void Delete(CredentialType type)
    {
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = 1";
        cmd.ExecuteNonQuery();
    }

    private void EnsureColumns(CredentialType type)
    {
        if (!_migrated.Add(type)) return;
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(1));
        }

        foreach (var def in CredentialSchema.Fields[type])
        {
            var column = CredentialSchema.ColumnName(def);
            if (existing.Contains(column)) continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT";
            alter.ExecuteNonQuery();
        }
    }
}
