using OneDevice.MasterClient.Agent;
using OneDevice.MasterClient.Agent.Components;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Discovery;
using OneDevice.MasterClient.Core.Runtime;
using Serilog;

// Runs as a Windows service (or from the console) and serves the setup page on localhost only.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var dataDir = Path.GetDirectoryName(builder.Configuration["Agent:ConfigPath"] ?? ConfigurationStore.DefaultPath)!;
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(dataDir, "logs", "agent-.log"), rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();
builder.Services.AddSerilog();

builder.Host.UseWindowsService(o => o.ServiceName = "OneDevice Master Client");

// Local page only: never reachable from the network.
var url = builder.Configuration["Agent:Url"] ?? "http://localhost:5080";
if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
    throw new InvalidOperationException($"Agent:Url must be a localhost address, got '{url}'.");
builder.WebHost.UseUrls(url);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(new ConfigurationStore(builder.Configuration["Agent:ConfigPath"] ?? ConfigurationStore.DefaultPath));
builder.Services.AddSingleton<ISerialPortScanner, SerialPortScanner>();
builder.Services.AddSingleton(sp => new AgentRuntime(sp.GetRequiredService<ConfigurationStore>(),
    sp.GetRequiredService<ILoggerFactory>(), scanner: sp.GetRequiredService<ISerialPortScanner>()));

// Registration on the OneDevice website ("Register this master").
var portalUrl = RegistrationClient.ParsePortalUrl(builder.Configuration["Cloud:PortalUrl"]);
builder.Services.AddHttpClient("onedevice", http => http.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IRegistrationClient>(sp =>
    new RegistrationClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("onedevice"), portalUrl));
builder.Services.AddSingleton(sp =>
{
    var runtime = sp.GetRequiredService<AgentRuntime>();
    return new MasterRegistrar(sp.GetRequiredService<IRegistrationClient>(), sp.GetRequiredService<ISerialPortScanner>(),
        id => runtime.Configuration.Masters.FirstOrDefault(m => m.Id == id), m => runtime.SaveMasterAsync(m),
        sp.GetRequiredService<ILogger<MasterRegistrar>>());
});
builder.Services.AddHostedService<AgentHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

Log.Information("OneDevice Master Client {Version} – setup page {Url}", AgentRuntime.AgentVersion, url);
try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
