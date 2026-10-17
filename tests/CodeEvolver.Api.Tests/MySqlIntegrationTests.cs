using CodeEvolver.Api;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace CodeEvolver.Api.Tests;

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEEVOLVER_TEST_MYSQL")))
            Skip = "Set CODEEVOLVER_TEST_MYSQL to an isolated local MySQL test server (never production).";
    }
}

public sealed class MySqlIntegrationTests
{
    [MySqlFact]
    public async Task IndependentConnectionsPersistClaimsCommandsAndCrashRecovery()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CODEEVOLVER_TEST_MYSQL")!);
        Assert.Contains(builder.Server, new[] { "localhost", "127.0.0.1", "::1" });
        Assert.True(builder.Database.StartsWith("code_evolver_test", StringComparison.Ordinal), "Only explicitly named test databases are allowed.");
        var database = "code_evolver_test_" + Guid.NewGuid().ToString("N");
        builder.Database = "";
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4";
        await command.ExecuteNonQueryAsync();
        try
        {
            builder.Database = database;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:CodeEvolver"] = builder.ConnectionString }).Build();
            var first = new MySqlEvolutionStore(configuration);
            var second = new MySqlEvolutionStore(configuration);
            await Task.WhenAll(first.InitializeAsync(default), second.InitializeAsync(default));
            var repository = Path.Combine(Path.GetTempPath(), "ce-mysql-" + Guid.NewGuid().ToString("N"));
            var evolution = await DurableWorkflowTests.CreateStartedAsync(first, repository, cycles: 2);
            var claims = await Task.WhenAll(first.ClaimNextEventAsync(default), second.ClaimNextEventAsync(default));
            var claim = claims.Single(c => c.HasValue)!.Value;
            Assert.Single(claims, c => c.HasValue);
            await second.MutateAsync(evolution.Id, e => e.Events.Single(x => x.Id == claim.Event.Id).LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1), default);
            Assert.Null(await first.ClaimNextEventAsync(default));
            await second.MutateAsync(evolution.Id, e => e.Events.Single(x => x.Id == claim.Event.Id).NotBefore = DateTimeOffset.UtcNow.AddSeconds(-1), default);
            var reclaimed = (await first.ClaimNextEventAsync(default))!.Value;
            await Assert.ThrowsAsync<LeaseLostException>(() => second.UpdateClaimAsync(evolution.Id, claim.Event.Id, claim.Event.LeaseOwner!, (_, e) => e.Logs.Add("old"), default));
            await Task.WhenAll((Task)new EvolutionCoordinator(second).PauseAsync(evolution.Id, default),
                first.UpdateClaimAsync(evolution.Id, reclaimed.Event.Id, reclaimed.Event.LeaseOwner!, (_, e) => e.Logs.Add("persisted progress"), default));
            await DurableWorkflowTests.Monitor(first).ProcessAsync(reclaimed.Evolution, reclaimed.Event, default);
            Assert.Equal(EvolutionStatus.Paused, (await second.GetAsync(evolution.Id, default))!.Status);
            await new EvolutionCoordinator(second).ResumeAsync(evolution.Id, default);
            await DurableWorkflowTests.Drain(first);
            var saved = (await new MySqlEvolutionStore(configuration).GetAsync(evolution.Id, default))!;
            Assert.Equal(EvolutionStatus.Completed, saved.Status);
            Assert.Equal(2, saved.WorkItems.Count);
            Assert.Contains("persisted progress", saved.Events.Single(e => e.Id == claim.Event.Id).Logs);
            await first.ServiceAsync(s => s.DispatchPaused = true, default);
            Assert.True((await second.ServiceAsync(null, default)).DispatchPaused);
            await second.DeleteAsync(evolution.Id, default);
            Assert.Null(await first.GetAsync(evolution.Id, default));
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            command.CommandText = $"DROP DATABASE `{database}`";
            await command.ExecuteNonQueryAsync();
        }
    }
}