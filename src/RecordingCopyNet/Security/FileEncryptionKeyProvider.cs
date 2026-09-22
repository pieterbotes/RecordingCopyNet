using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;

namespace RecordingCopyNet.Security;

// Mirrors getEncryptionKey() in the Node app's lib/store.js: a random
// 32-byte key generated once and persisted as a local file. Node used
// mode 0o600; the closest functional equivalent on Windows is an ACL
// restricting the file to the current user, applied after creation.
public class FileEncryptionKeyProvider : IEncryptionKeyProvider
{
    private readonly string _keyPath;

    public FileEncryptionKeyProvider(IOptions<AppConfig> config)
    {
        _keyPath = Path.Combine(config.Value.DataDir, ".key");
    }

    public byte[] GetOrCreateKey()
    {
        var dir = Path.GetDirectoryName(_keyPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(_keyPath))
        {
            return Convert.FromHexString(File.ReadAllText(_keyPath).Trim());
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(_keyPath, Convert.ToHexString(key));
        RestrictToCurrentUser(_keyPath);
        return key;
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        security.SetAccessRuleProtection(true, false); // disable inheritance, drop inherited rules
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            identity, System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        fileInfo.SetAccessControl(security);
    }
}
