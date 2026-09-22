namespace RecordingCopyNet.Security;

public interface IFieldCipher
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertextBase64);
}
