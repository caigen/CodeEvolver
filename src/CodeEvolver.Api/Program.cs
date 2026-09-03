using CodeEvolver.Api;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));
builder.Services.AddSingleton<IEvolutionStore>(services =>
    builder.Configuration["Storage:Provider"]?.Equals("cassandra", StringComparison.OrdinalIgnoreCase) == true
        ? ActivatorUtilities.CreateInstance<CassandraEvolutionStore>(services)
        : ActivatorUtilities.CreateInstance<JsonEvolutionStore>(services));
builder.Services.AddSingleton<EvolutionCoordinator>();
builder.Services.AddSingleton<IAgentRunner>(services =>
    builder.Configuration["Agent:Provider"]?.Equals("copilot", StringComparison.OrdinalIgnoreCase) == true
        ? ActivatorUtilities.CreateInstance<CopilotAgentRunner>(services)
        : new LocalAgentRunner());
builder.Services.AddHostedService<EventMonitor>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

app.MapGet("/api/health", (IConfiguration configuration) => Results.Ok(new
{
    status = "healthy",
    storage = configuration["Storage:Provider"] ?? "json",
    agent = configuration["Agent:Provider"] ?? "local"
}));
app.MapGet("/api/repository", (IWebHostEnvironment environment) =>
{
    var repositoryPath = RepositoryDiscovery.FindRoot(environment.ContentRootPath);
    return repositoryPath is null
        ? Results.NotFound(new { error = "The API is not running inside a Git repository." })
        : Results.Ok(new { repositoryPath });
});
app.MapGet("/api/evolutions", async (IEvolutionStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListAsync(cancellationToken)));
app.MapGet("/api/evolutions/{id:guid}", async (Guid id, IEvolutionStore store, CancellationToken cancellationToken) =>
{
    var evolution = await store.GetAsync(id, cancellationToken);
    return evolution is null ? Results.NotFound() : Results.Ok(evolution);
});
app.MapPost("/api/evolutions", async (CreateEvolutionRequest request, EvolutionCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var evolution = await coordinator.CreateAsync(request, cancellationToken);
    return Results.Created($"/api/evolutions/{evolution.Id}", evolution);
});
app.MapPut("/api/evolutions/{id:guid}", async (Guid id, CreateEvolutionRequest request, IEvolutionStore store, EvolutionCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var errors = request.Validate();
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var existing = await store.GetAsync(id, cancellationToken);
    if (existing is null) return Results.NotFound();
    if (existing.Status is EvolutionStatus.Running or EvolutionStatus.StopRequested) return Results.Conflict(new { error = "Wait for the evolution to stop before editing it." });
    return Results.Ok(await coordinator.UpdateAsync(id, request, cancellationToken));
});
app.MapDelete("/api/evolutions/{id:guid}", async (Guid id, IEvolutionStore store, EvolutionCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var existing = await store.GetAsync(id, cancellationToken);
    if (existing is null) return Results.NotFound();
    if (existing.Status is EvolutionStatus.Running or EvolutionStatus.StopRequested) return Results.Conflict(new { error = "Wait for the evolution to stop before deleting it." });
    await coordinator.DeleteAsync(id, cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/evolutions/{id:guid}/start", async (Guid id, EvolutionCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var evolution = await coordinator.StartAsync(id, cancellationToken);
    return evolution is null ? Results.NotFound() : Results.Accepted($"/api/evolutions/{id}", evolution);
});
app.MapPost("/api/evolutions/{id:guid}/stop", async (Guid id, EvolutionCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var evolution = await coordinator.StopAsync(id, cancellationToken);
    return evolution is null ? Results.NotFound() : Results.Ok(evolution);
});

app.Run();

public partial class Program;
