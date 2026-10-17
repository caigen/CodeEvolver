using System.Net;
using System.Net.Http.Json;
using CodeEvolver.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeEvolver.Api.Tests;

public sealed class ControlApiTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ce-api-tests-");
    private readonly WebApplicationFactory<Program> factory;

    public ControlApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEvolutionStore>();
            foreach (var registration in services.Where(s => s.ImplementationType == typeof(DatabaseInitializer)).ToArray()) services.Remove(registration);
            services.AddSingleton<IEvolutionStore>(new JsonEvolutionStore(new DurableWorkflowTests.TestEnvironment(directory.FullName)));
        }));
    }

    [Fact]
    public async Task ApiPersistsCommandsButNeverDispatchesAgents()
    {
        using var client = factory.CreateClient();
        var request = new CreateEvolutionRequest(RepositoryDiscovery.FindRoot(AppContext.BaseDirectory)!, "Test API", ".", "main", 30);
        var response = await client.PostAsJsonAsync("/api/evolutions", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/evolutions/{id}/start", null)).StatusCode);
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), hosted => hosted is EventMonitor);
        var store = factory.Services.GetRequiredService<IEvolutionStore>();
        Assert.Contains((await store.GetAsync(id, default))!.Events, e => e.Status == EvolutionEventStatus.Pending);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/evolutions/{id}/pause", null)).StatusCode);
        Assert.Equal(EvolutionStatus.Paused, (await store.GetAsync(id, default))!.Status);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/evolutions/{id}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/evolutions/{id}", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/service/control", new { paused = true })).StatusCode);
        Assert.True((await store.ServiceAsync(null, default)).DispatchPaused);
    }

    [Fact]
    public async Task InvalidInputsAndCrossOriginCommandsAreRejected()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/evolutions", new CreateEvolutionRequest("not-a-repo", "x", "../outside", "main", 51));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/service/control", new { paused = true })).StatusCode);
    }

    public void Dispose() { factory.Dispose(); directory.Delete(true); }
}