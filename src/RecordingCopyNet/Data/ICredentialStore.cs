namespace RecordingCopyNet.Data;

public interface ICredentialStore
{
    bool Exists(CredentialType type);
    void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields);
    IReadOnlyDictionary<string, string?>? Load(CredentialType type);
    void Delete(CredentialType type);
}
