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
await builder.Build().RunAsync();