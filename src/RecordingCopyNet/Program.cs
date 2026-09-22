using RecordingCopyNet.Config;
using RecordingCopyNet.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));
builder.Services.PostConfigure<AppConfig>(cfg => cfg.ResolvePaths(AppContext.BaseDirectory));

builder.Services.AddSingleton<Db>();

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
