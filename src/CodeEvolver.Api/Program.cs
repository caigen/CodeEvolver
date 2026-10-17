using CodeEvolver.Api;
using Microsoft.VisualBasic.FileIO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
var allowedOrigins = new[] { "http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:5278", "http://127.0.0.1:5278" };
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();
builder.Services.AddEvolutionCore(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Use(async (context, next) =>
{
    // CORS alone does not prevent cross-origin form POSTs from launching coding jobs.
    var origin = context.Request.Headers.Origin.ToString();
    if ((context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address)) ||
        (origin.Length > 0 && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)))
    {
        await Results.Problem("This control plane only accepts trusted loopback clients.", statusCode: 403).ExecuteAsync(context);
        return;
    }
    try { await next(context); }
    catch (StoreConflictException exception) { await Results.Problem(exception.Message, statusCode: 409).ExecuteAsync(context); }
    catch (MySqlException) { await Results.Problem("MySQL is unavailable; the command was not acknowledged. Refresh before retrying.", statusCode: 503).ExecuteAsync(context); }
});
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", async (IEvolutionStore store, IConfiguration configuration, CancellationToken token) =>
{
    await store.ServiceAsync(null, token);
    return Results.Ok(new { status = "healthy", storage = "mysql", role = "control-plane", agent = configuration["Agent:Provider"] ?? "copilot" });
});
app.MapGet("/api/service", async (IEvolutionStore store, IConfiguration configuration, CancellationToken token) =>
{
    var state = await store.ServiceAsync(null, token);
    return Results.Ok(new ServiceResponse(state.DispatchPaused, state.Workers.Any(w => w.LastSeenAt > DateTimeOffset.UtcNow.AddSeconds(-30)),
        state.Workers, configuration.GetValue("Agent:Copilot:PublishChanges", false), state.UpdatedAt));
});
app.MapPost("/api/service/control", async (ServiceControlRequest request, IEvolutionStore store, CancellationToken token) =>
{
    var state = await store.ServiceAsync(state =>
    {
        state.DispatchPaused = request.Paused;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.Commands.Add(new ServiceCommand(request.Paused ? "pause-dispatch" : "resume-dispatch", state.UpdatedAt));
    }, token);
    return Results.Ok(new { state.DispatchPaused, state.UpdatedAt });
});
app.MapGet("/api/repository", (IWebHostEnvironment environment) =>
{
    var repositoryPath = RepositoryDiscovery.FindRoot(environment.ContentRootPath);
    return repositoryPath is null
        ? Results.NotFound(new { error = "The API is not running inside a Git repository." })
        : Results.Ok(new { repositoryPath });
});
app.MapPost("/api/repository/select", async (CancellationToken cancellationToken) =>
{
    try
    {
        var repositoryPath = await RepositoryDiscovery.SelectDirectoryAsync(cancellationToken);
        return repositoryPath is null ? Results.NoContent() : Results.Ok(new { repositoryPath });
    }
    catch (PlatformNotSupportedException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status501NotImplemented);
    }
});
app.MapPost("/api/data-analysis", async (HttpRequest request, DataAnalyzer analyzer, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload a CSV or JSON file." });
    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    var repositoryPath = form["repositoryPath"].ToString();
    if (file is null) return Results.BadRequest(new { error = "Select a CSV or JSON file." });
    try
    {
        var run = await analyzer.StartAsync(file, repositoryPath, cancellationToken);
        return Results.Accepted($"/api/data-analysis/{run.Id}", run);
    }
    catch (Exception exception) when (exception is InvalidOperationException or DirectoryNotFoundException or JsonException or MalformedLineException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery();
app.MapGet("/api/data-analysis/{id:guid}", async (Guid id, DataAnalyzer analyzer, CancellationToken token) =>
{
    var run = await analyzer.GetAsync(id, token);
    return run is null ? Results.NotFound() : Results.Ok(run);
});
app.MapGet("/api/data-analysis", async (IEvolutionStore store, CancellationToken token) =>
    Results.Ok((await store.ListAsync(token)).Where(e => e.Kind == "analysis").Take(50).Select(DataAnalyzer.ToResponse)));
app.MapGet("/api/evolutions", async (bool? summaries, int? offset, IEvolutionStore store, CancellationToken cancellationToken) =>
{
    var items = (await store.ListAsync(cancellationToken)).Where(e => e.Kind == "evolution").Skip(Math.Max(0, offset ?? 0)).Take(50).ToArray();
    if (summaries == true)
        return Results.Ok(items.Select(e => new { e.Id, e.RepositoryPath, e.Direction, e.Scope, e.TargetBranch, e.Status, e.Cycle, e.MaxCycles,
            e.CycleDelaySeconds, e.PublishChanges, e.UpdatedAt, e.StartedAt, e.CompletedAt, events = Array.Empty<EvolutionEvent>(), workItems = Array.Empty<WorkItem>() }));
    return Results.Ok((object)items);
});
app.MapGet("/api/evolutions/{id:guid}/events", async (Guid id, int? offset, int? limit, IEvolutionStore store, CancellationToken token) =>
{
    var evolution = await store.GetAsync(id, token);
    return evolution is null ? Results.NotFound() : Results.Ok(new { total = evolution.Events.Count,
        items = evolution.Events.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 100, 1, 500)) });
});
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
    return Results.Ok(await coordinator.UpdateAsync(id, request, cancellationToken));
});
app.MapDelete("/api/evolutions/{id:guid}", async (Guid id, IEvolutionStore store, EvolutionCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var existing = await store.GetAsync(id, cancellationToken);
    if (existing is null) return Results.NotFound();
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

foreach (var command in new[] { "pause", "resume", "approve", "retry" })
{
    app.MapPost($"/api/evolutions/{{id:guid}}/{command}", async (Guid id, EvolutionCoordinator coordinator, CancellationToken token) =>
    {
        var evolution = await (command switch
        {
            "pause" => coordinator.PauseAsync(id, token),
            "resume" => coordinator.ResumeAsync(id, token),
            "approve" => coordinator.ApproveAsync(id, token),
            _ => coordinator.RetryAsync(id, token)
        });
        return evolution is null ? Results.NotFound() : Results.Accepted($"/api/evolutions/{id}", evolution);
    });
}

app.Run();

public partial class Program;
