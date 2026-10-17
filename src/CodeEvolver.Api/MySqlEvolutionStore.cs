using System.Data;
using System.Text.Json;
using MySqlConnector;

namespace CodeEvolver.Api;

/// <summary>Short InnoDB transactions; the mutex fences API commands and all workers.</summary>
public sealed class MySqlEvolutionStore(IConfiguration configuration) : TransactionalEvolutionStore
{
    private string ConnectionString => configuration.GetConnectionString("CodeEvolver")
        ?? throw new InvalidOperationException("Configure ConnectionStrings__CodeEvolver with a MySQL database connection. No fallback store is enabled.");

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        // Bootstrap is idempotent. The database itself must already exist.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ce_schema (version INT NOT NULL PRIMARY KEY) ENGINE=InnoDB;
            CREATE TABLE IF NOT EXISTS ce_service (
                id INT NOT NULL PRIMARY KEY, payload JSON NOT NULL
            ) ENGINE=InnoDB;
            CREATE TABLE IF NOT EXISTS ce_evolutions (
                id CHAR(36) NOT NULL PRIMARY KEY,
                version BIGINT NOT NULL,
                status VARCHAR(32) NOT NULL,
                updated_at DATETIME(6) NOT NULL,
                payload JSON NOT NULL,
                INDEX ix_ce_status (status, updated_at)
            ) ENGINE=InnoDB;
            INSERT IGNORE INTO ce_service(id, payload) VALUES (1, '{}');
            INSERT IGNORE INTO ce_schema(version) VALUES (1);
            """;
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT MAX(version) FROM ce_schema";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 1)
            throw new InvalidOperationException("Unsupported Code Evolver database schema version.");
    }

    protected override async Task<T> TransactionAsync<T>(Func<StoreState, T> action, bool write, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM ce_service WHERE id=1" + (write ? " FOR UPDATE" : "");
        var serviceJson = (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Database is not initialized.");
        var state = new StoreState { Service = JsonSerializer.Deserialize<ServiceState>(serviceJson, JsonOptions)! };
        var original = new Dictionary<Guid, long>();
        command.CommandText = "SELECT payload FROM ce_evolutions";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var evolution = JsonSerializer.Deserialize<Evolution>(reader.GetString(0), JsonOptions)!;
                state.Evolutions.Add(evolution);
                original.Add(evolution.Id, evolution.Version);
            }
        }
        var result = action(state);
        if (write)
        {
            foreach (var evolution in state.Evolutions)
            {
                if (original.TryGetValue(evolution.Id, out var version) && version == evolution.Version) continue;
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO ce_evolutions(id, version, status, updated_at, payload)
                    VALUES (@id, @version, @status, @updated, @payload)
                    ON DUPLICATE KEY UPDATE version=@version, status=@status, updated_at=@updated, payload=@payload
                    """;
                command.Parameters.AddWithValue("@id", evolution.Id.ToString());
                command.Parameters.AddWithValue("@version", evolution.Version);
                command.Parameters.AddWithValue("@status", evolution.Status.ToString());
                command.Parameters.AddWithValue("@updated", evolution.UpdatedAt.UtcDateTime);
                command.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(evolution, JsonOptions));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var removed in original.Keys.Except(state.Evolutions.Select(e => e.Id)))
            {
                command.Parameters.Clear();
                command.CommandText = "DELETE FROM ce_evolutions WHERE id=@id";
                command.Parameters.AddWithValue("@id", removed.ToString());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            command.Parameters.Clear();
            command.CommandText = "UPDATE ce_service SET payload=@payload WHERE id=1";
            command.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(state.Service, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}