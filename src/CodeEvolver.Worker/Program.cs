using CodeEvolver.Api;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(options => options.ServiceName = "CodeEvolver.Worker");
builder.Services.AddEvolutionCore(builder.Configuration);
builder.Services.AddHostedService<EventMonitor>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
using var host = builder.Build();
var importIndex = Array.IndexOf(args, "--import-json");
if (importIndex >= 0)
{
    if (importIndex + 1 >= args.Length) throw new ArgumentException("--import-json requires an export file path.");
    var store = host.Services.GetRequiredService<MySqlEvolutionStore>();
    await store.InitializeAsync(CancellationToken.None);
    var count = await LegacyImport.ImportAsync(args[importIndex + 1], store, CancellationToken.None);
    Console.WriteLine($"Imported {count} evolutions. Existing IDs were left unchanged; Worker was not started.");
    return;
}
await host.RunAsync();