using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/auth.js: Server-to-Server OAuth (account_credentials
// grant), in-memory token cache with a 60s expiry safety margin.
public class ZoomAuthService : IZoomAuthService
{
    private readonly HttpClient _http;
    private readonly ICredentialStore _store;
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public ZoomAuthService(HttpClient http, ICredentialStore store, IOptions<AppConfig> config)
    {
        _http = http;
        _store = store;
        _config = config.Value;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_token != null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60))
            return _token;

        await _lock.WaitAsync(ct);
        try
        {
            if (_token != null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60))
                return _token;

            var creds = _store.Load(CredentialType.Zoom)
                ?? throw new InvalidOperationException("Zoom credentials not configured");

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds["client_id"]}:{creds["client_secret"]}"));

            using var request = new HttpRequestMessage(HttpMethod.Post, _config.ZoomAuthUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "account_credentials",
                    ["account_id"] = creds["account_id"] ?? "",
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Zoom auth failed ({(int)response.StatusCode}): {body}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
            _token = json.GetProperty("access_token").GetString();
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32());
            return _token!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void ClearTokenCache()
    {
        _token = null;
        _expiresAt = DateTimeOffset.MinValue;
    }
}
