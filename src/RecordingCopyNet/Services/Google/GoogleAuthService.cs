using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using RecordingCopyNet.Data;

namespace RecordingCopyNet.Services.Google;

// Direct port of lib/google/auth.js: a JWT service-account credential,
// optionally impersonating a user via domain-wide delegation (the
// "subject" claim), cached until credentials/settings change.
public class GoogleAuthService : IGoogleAuthService
{
    private readonly ICredentialStore _store;
    private readonly object _clientLock = new();
    private DriveService? _client;

    public GoogleAuthService(ICredentialStore store) => _store = store;

    public DriveService GetDriveService()
    {
        // Singleton-cached client accessed from multiple request/background threads;
        // without this lock, two concurrent first-callers could each build and leak a
        // separate DriveService.
        lock (_clientLock)
        {
            if (_client != null) return _client;

            var creds = _store.Load(CredentialType.Google)
                ?? throw new InvalidOperationException("Google credentials not configured");
            var settings = _store.Load(CredentialType.Settings);

            var initializer = new ServiceAccountCredential.Initializer(creds["client_email"])
            {
                Scopes = new[] { DriveService.Scope.Drive },
                User = settings != null && settings.TryGetValue("google_impersonate_email", out var impersonate) ? impersonate : null,
            }.FromPrivateKey(creds["private_key"]);

            var credential = new ServiceAccountCredential(initializer);

            _client = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
            });
            return _client;
        }
    }

    public void ClearDriveClient()
    {
        // Deliberately does NOT call .Dispose() on the outgoing client: a background
        // transfer may still be mid-upload holding this same singleton-cached instance,
        // and disposing it here would fault that transfer with ObjectDisposedException.
        // Matches the original Node app (lib/google/auth.js), which just drops the
        // reference and lets garbage collection handle cleanup.
        lock (_clientLock)
        {
            _client = null;
        }
    }
}
