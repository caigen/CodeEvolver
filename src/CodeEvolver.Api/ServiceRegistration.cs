namespace CodeEvolver.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddEvolutionCore(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration["Storage:Provider"] is { } provider && !provider.Equals("mysql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production hosts require Storage:Provider=mysql. JSON is only available to tests/imports.");
        services.AddSingleton<MySqlEvolutionStore>();
        services.AddSingleton<IEvolutionStore>(sp => sp.GetRequiredService<MySqlEvolutionStore>());
        services.AddSingleton<EvolutionCoordinator>();
        services.AddSingleton<DataAnalyzer>();
        services.AddSingleton<CopilotAgentRunner>();
        services.AddSingleton<IAgentRunner>(sp => configuration["Agent:Provider"] == "local"
            ? new LocalAgentRunner() : sp.GetRequiredService<CopilotAgentRunner>());
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }
}

public sealed class DatabaseInitializer(MySqlEvolutionStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}