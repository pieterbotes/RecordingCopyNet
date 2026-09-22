using System.Security.Cryptography;
using System.Text;

namespace RecordingCopyNet.Security;

// Layout: base64( nonce[12] || ciphertext || tag[16] )
public class AesGcmFieldCipher : IFieldCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmFieldCipher(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes (AES-256)", nameof(key));
        _key = key;
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[NonceSize + cipherBytes.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(cipherBytes, 0, combined, NonceSize, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + cipherBytes.Length, TagSize);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string ciphertextBase64)
    {
        var combined = Convert.FromBase64String(ciphertextBase64);
        var nonce = combined.AsSpan(0, NonceSize);
        var cipherLen = combined.Length - NonceSize - TagSize;
        var cipherBytes = combined.AsSpan(NonceSize, cipherLen);
        var tag = combined.AsSpan(NonceSize + cipherLen, TagSize);

        var plainBytes = new byte[cipherLen];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
