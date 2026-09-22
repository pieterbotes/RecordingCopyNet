using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/recordings.js.
public class ZoomRecordingsService : IZoomRecordingsService
{
    private readonly HttpClient _http;
    private readonly IZoomAuthService _auth;
    private readonly AppConfig _config;

    public ZoomRecordingsService(HttpClient http, IZoomAuthService auth, IOptions<AppConfig> config)
    {
        _http = http;
        _auth = auth;
        _config = config.Value;
    }

    public async Task<JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString("");
        query["page_size"] = "300";
        if (!string.IsNullOrEmpty(from)) query["from"] = from;
        if (!string.IsNullOrEmpty(to)) query["to"] = to;

        var url = $"{_config.ZoomApiBase}/users/{Uri.EscapeDataString(userId)}/recordings?{query}";
        var json = await SendAuthorizedAsync(url, ct);

        return json.TryGetProperty("meetings", out var meetings) ? meetings : JsonDocument.Parse("[]").RootElement;
    }

    public async Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default)
    {
        var url = $"{_config.ZoomApiBase}/meetings/{Uri.EscapeDataString(meetingId)}/recordings";
        var json = await SendAuthorizedAsync(url, ct);
        return json.Deserialize<ZoomMeetingRecordings>() ?? new ZoomMeetingRecordings();
    }

    public async Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default)
    {
        var url = $"{_config.ZoomApiBase}/users/{Uri.EscapeDataString(userId)}";
        var token = await _auth.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
        return json.TryGetProperty("email", out var email) ? email.GetString() : null;
    }

    private async Task<JsonElement> SendAuthorizedAsync(string url, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Zoom API error ({(int)response.StatusCode}): {body}");
        }
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
    }
}
