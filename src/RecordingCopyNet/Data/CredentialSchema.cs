namespace RecordingCopyNet.Data;

public enum CredentialType { Zoom, Google, Settings }

public record CredentialFieldDef(string Name, bool Required, bool Encrypted);

public static class CredentialSchema
{
    public static readonly IReadOnlyDictionary<CredentialType, IReadOnlyList<CredentialFieldDef>> Fields =
        new Dictionary<CredentialType, IReadOnlyList<CredentialFieldDef>>
        {
            [CredentialType.Zoom] = new[]
            {
                new CredentialFieldDef("account_id", true, true),
                new CredentialFieldDef("client_id", true, true),
                new CredentialFieldDef("client_secret", true, true),
            },
            [CredentialType.Google] = new[]
            {
                new CredentialFieldDef("client_email", true, true),
                new CredentialFieldDef("private_key", true, true),
                new CredentialFieldDef("project_id", false, false),
                new CredentialFieldDef("client_id", false, false),
            },
            [CredentialType.Settings] = new[]
            {
                new CredentialFieldDef("google_folder_id", false, false),
                new CredentialFieldDef("google_impersonate_email", false, false),
                new CredentialFieldDef("default_zoom_user", false, false),
                new CredentialFieldDef("transfer_all_users", false, false),
                new CredentialFieldDef("zoom_websocket_url", false, false),
            },
        };

    public static string TableName(CredentialType type) => $"credentials_{type.ToString().ToLowerInvariant()}";

    public static string ColumnName(CredentialFieldDef field) => field.Encrypted ? $"{field.Name}_encrypted" : field.Name;

    public static bool TryParse(string typeName, out CredentialType type) =>
        Enum.TryParse(typeName, ignoreCase: true, out type) && Fields.ContainsKey(type);
}
