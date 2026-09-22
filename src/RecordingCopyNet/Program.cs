using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));
builder.Services.PostConfigure<AppConfig>(cfg => cfg.ResolvePaths(AppContext.BaseDirectory));

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<RecordingCopyNet.Security.IEncryptionKeyProvider, RecordingCopyNet.Security.FileEncryptionKeyProvider>();
builder.Services.AddSingleton<RecordingCopyNet.Security.IFieldCipher>(sp =>
    new RecordingCopyNet.Security.AesGcmFieldCipher(
        sp.GetRequiredService<RecordingCopyNet.Security.IEncryptionKeyProvider>().GetOrCreateKey()));
builder.Services.AddSingleton<ICredentialStore, CredentialStore>();
builder.Services.AddSingleton<IEventsRepository, EventsRepository>();
builder.Services.AddSingleton<IRequestsRepository, RequestsRepository>();
builder.Services.AddHttpClient("ZoomAuth");
builder.Services.AddSingleton<RecordingCopyNet.Services.Zoom.IZoomAuthService>(sp =>
    new RecordingCopyNet.Services.Zoom.ZoomAuthService(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("ZoomAuth"),
        sp.GetRequiredService<ICredentialStore>(),
        sp.GetRequiredService<IOptions<AppConfig>>()));
builder.Services.AddHttpClient<RecordingCopyNet.Services.Zoom.IZoomRecordingsService, RecordingCopyNet.Services.Zoom.ZoomRecordingsService>();
builder.Services.AddHttpClient<RecordingCopyNet.Services.Zoom.IZoomDownloadService, RecordingCopyNet.Services.Zoom.ZoomDownloadService>();

builder.Services.AddSingleton<RecordingCopyNet.Services.Google.IGoogleAuthService, RecordingCopyNet.Services.Google.GoogleAuthService>();
builder.Services.AddSingleton<RecordingCopyNet.Services.Google.IGoogleDriveService, RecordingCopyNet.Services.Google.GoogleDriveService>();

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var port = context.Configuration.GetSection("AppConfig")["Port"];
    options.ListenAnyIP(int.Parse(port ?? "3900"));
});

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);

var app = builder.Build();

app.Services.GetRequiredService<Db>().InitializeSchema();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory-style integration tests in later tasks.
public partial class Program { }
