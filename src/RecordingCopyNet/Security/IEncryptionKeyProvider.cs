namespace RecordingCopyNet.Security;

public interface IEncryptionKeyProvider
{
    byte[] GetOrCreateKey();
}
