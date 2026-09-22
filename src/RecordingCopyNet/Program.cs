using RecordingCopyNet.Config;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));
builder.Services.PostConfigure<AppConfig>(cfg => cfg.ResolvePaths(AppContext.BaseDirectory));

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var port = context.Configuration.GetSection("AppConfig")["Port"];
    options.ListenAnyIP(int.Parse(port ?? "3900"));
});

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory-style integration tests in later tasks.
public partial class Program { }
