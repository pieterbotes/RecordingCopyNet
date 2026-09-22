using System.Security.Cryptography;
using RecordingCopyNet.Security;
using Xunit;

namespace RecordingCopyNet.Tests.Security;

public class AesGcmFieldCipherTests
{
    [Fact]
    public void Decrypt_ReturnsOriginalPlaintext_AfterEncrypt()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = new AesGcmFieldCipher(key);

        var ciphertext = cipher.Encrypt("super-secret-value");
        var plaintext = cipher.Decrypt(ciphertext);

        Assert.Equal("super-secret-value", plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachCall()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = new AesGcmFieldCipher(key);

        var a = cipher.Encrypt("same-value");
        var b = cipher.Encrypt("same-value");

        Assert.NotEqual(a, b); // fresh nonce per call
    }

    [Fact]
    public void Decrypt_ThrowsOnWrongKey()
    {
        var cipher1 = new AesGcmFieldCipher(RandomNumberGenerator.GetBytes(32));
        var cipher2 = new AesGcmFieldCipher(RandomNumberGenerator.GetBytes(32));

        var ciphertext = cipher1.Encrypt("value");

        Assert.ThrowsAny<CryptographicException>(() => cipher2.Decrypt(ciphertext));
    }
}
