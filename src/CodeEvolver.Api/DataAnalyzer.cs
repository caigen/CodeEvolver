using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace CodeEvolver.Api;

public sealed record DataAnalysisResult(string Direction, IReadOnlyList<string> KeyPoints);
public enum DataAnalysisStatus { Running, Completed, Failed }

public sealed class DataAnalysisRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FileName { get; init; }
    public DataAnalysisStatus Status { get; set; } = DataAnalysisStatus.Running;
    public string? Direction { get; set; }
    public IReadOnlyList<string> KeyPoints { get; set; } = [];
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<EvolutionEvent> Events { get; init; } = [];
}

public sealed class DataAnalyzer(CopilotAgentRunner agentRunner)
{
    private const int MaxFileBytes = 5 * 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, DataAnalysisRun> runs = new();

    public async Task<DataAnalysisRun> StartAsync(IFormFile file, string repositoryPath, CancellationToken cancellationToken)
    {
        if (file.Length == 0) throw new InvalidOperationException("Select a non-empty CSV or JSON file.");
        if (file.Length > MaxFileBytes) throw new InvalidOperationException("Data files must be 5 MB or smaller.");
        if (!Directory.Exists(repositoryPath)) throw new DirectoryNotFoundException("Select an existing cloned repository first.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        await using var stream = file.OpenReadStream();
        var profile = extension switch
        {
            ".csv" => ProfileCsv(stream),
            ".json" => await ProfileJsonAsync(stream, cancellationToken),
            _ => throw new InvalidOperationException("Only .csv and .json files are supported.")
        };
        var run = new DataAnalysisRun
        {
            FileName = Path.GetFileName(file.FileName),
            Events = CreateEvents()
        };
        runs[run.Id] = run;
        _ = ProcessAsync(run, repositoryPath, profile);
        return run;
    }

    public DataAnalysisRun? Get(Guid id) => runs.GetValueOrDefault(id);

    private async Task ProcessAsync(DataAnalysisRun run, string repositoryPath, string profile)
    {
        try
        {
            var purpose = await RunAgentAsync(run, 0, repositoryPath, $$"""
                You are the Data Purpose Agent. Infer the likely business and operational purpose of this dataset from its bounded profile. Focus on decisions, workflows, and outcomes the data supports. Do not inspect or modify repository files. Return a concise purpose assessment.

                File: {{run.FileName}}
                Profile:
                {{profile}}
                """);
            var insights = await RunAgentAsync(run, 1, repositoryPath, $$"""
                You are the Data Insight Agent. Analyze the bounded profile for data quality issues, meaningful distributions, anomalies, trends, and automation opportunities. Ground every claim in the profile and do not inspect or modify repository files. Return concise prioritized insights.

                File: {{run.FileName}}
                Profile:
                {{profile}}
                """);
            var direction = await RunAgentAsync(run, 2, repositoryPath, $$"""
                You are the Evolution Direction Agent. Propose one concrete software evolution direction for the selected repository based on the data purpose and insights below. Focus on a useful product, workflow, validation, monitoring, or automation improvement. Do not inspect or modify repository files. Return one concise actionable direction and supporting rationale.

                Data purpose:
                {{purpose}}

                Data insights:
                {{insights}}
                """);
            var summary = await RunAgentAsync(run, 3, repositoryPath, $$"""
                You are the Summary Agent. Consolidate the team findings into one evolution recommendation. Do not add unsupported claims. Return only JSON with this shape: {"direction":"one concise actionable direction","keyPoints":["point 1","point 2","point 3"]}. Include 3 to 5 key points covering data purpose, strongest insights, and why the direction matters.

                Purpose Agent:
                {{purpose}}

                Insight Agent:
                {{insights}}

                Evolution Direction Agent:
                {{direction}}
                """);
            var result = ParseResult(summary);
            run.Direction = result.Direction;
            run.KeyPoints = result.KeyPoints;
            run.Status = DataAnalysisStatus.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception exception)
        {
            var activeEvent = run.Events.FirstOrDefault(item => item.Status == EvolutionEventStatus.Processing);
            if (activeEvent is not null)
            {
                activeEvent.Status = EvolutionEventStatus.Failed;
                activeEvent.Detail = exception.Message;
                activeEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Failed: {exception.Message}");
                activeEvent.CompletedAt = DateTimeOffset.UtcNow;
            }
            foreach (var pendingEvent in run.Events.Where(item => item.Status == EvolutionEventStatus.Pending))
            {
                pendingEvent.Status = EvolutionEventStatus.Cancelled;
                pendingEvent.CompletedAt = DateTimeOffset.UtcNow;
            }
            run.Status = DataAnalysisStatus.Failed;
            run.Error = exception.Message;
            run.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<string> RunAgentAsync(DataAnalysisRun run, int eventIndex, string repositoryPath, string prompt)
    {
        var currentEvent = run.Events[eventIndex];
        currentEvent.Status = EvolutionEventStatus.Processing;
        currentEvent.StartedAt = DateTimeOffset.UtcNow;
        currentEvent.Prompt = prompt;
        currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Dispatching {currentEvent.Type}.");
        var result = await agentRunner.RunPromptAsync(repositoryPath, prompt, (message, _) =>
        {
            currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} {message}");
            return Task.CompletedTask;
        }, CancellationToken.None);
        currentEvent.Status = EvolutionEventStatus.Completed;
        currentEvent.Detail = result;
        currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Completed {currentEvent.Type}.");
        currentEvent.CompletedAt = DateTimeOffset.UtcNow;
        return result;
    }

    internal static List<EvolutionEvent> CreateEvents() =>
    [
        new() { Type = "data-purpose.started" },
        new() { Type = "data-insight.started" },
        new() { Type = "evolution-direction.started" },
        new() { Type = "analysis-summary.started" }
    ];

    internal static DataAnalysisResult ParseResult(string response)
    {
        try
        {
            var start = response.IndexOf('{');
            var end = response.LastIndexOf('}');
            if (start < 0 || end <= start) throw new JsonException();
            using var document = JsonDocument.Parse(response[start..(end + 1)]);
            var direction = document.RootElement.GetProperty("direction").GetString()?.Trim();
            var keyPoints = document.RootElement.GetProperty("keyPoints").EnumerateArray()
                .Select(item => item.GetString()?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(5)
                .Cast<string>()
                .ToArray();
            if (string.IsNullOrWhiteSpace(direction) || keyPoints.Length == 0) throw new JsonException();
            return new DataAnalysisResult(direction, keyPoints);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The Data Analyzer Agent returned an invalid recommendation.");
        }
    }

    private static string ProfileCsv(Stream stream)
    {
        using var parser = new TextFieldParser(stream, Encoding.UTF8, true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true
        };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidOperationException("The CSV file has no header row.");
        if (headers.Length == 0 || headers.Length > 100) throw new InvalidOperationException("CSV files must contain 1 to 100 columns.");
        var values = headers.Select(_ => new List<string>()).ToArray();
        var rowCount = 0;
        while (!parser.EndOfData && rowCount < 1000)
        {
            var row = parser.ReadFields() ?? [];
            for (var index = 0; index < headers.Length; index++) values[index].Add(index < row.Length ? row[index] : "");
            rowCount++;
        }
        return BuildProfile("CSV", rowCount, headers, values);
    }

    private static async Task<string> ProfileJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var rows = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray().Take(1000).ToArray(),
            JsonValueKind.Object => [document.RootElement],
            _ => throw new InvalidOperationException("JSON data must be an object or an array of objects.")
        };
        if (rows.Any(row => row.ValueKind != JsonValueKind.Object))
            throw new InvalidOperationException("JSON arrays must contain objects.");
        var headers = rows.SelectMany(row => row.EnumerateObject().Select(property => property.Name))
            .Distinct(StringComparer.Ordinal).Take(101).ToArray();
        if (headers.Length == 0 || headers.Length > 100) throw new InvalidOperationException("JSON data must contain 1 to 100 properties.");
        var values = headers.Select(header => rows.Select(row =>
            row.TryGetProperty(header, out var value) ? JsonValueForProfile(value) : "").ToList()).ToArray();
        return BuildProfile("JSON", rows.Length, headers, values);
    }

    private static string JsonValueForProfile(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Array => "[array]",
        JsonValueKind.Object => "{object}",
        _ => ""
    };

    private static string BuildProfile(string format, int rowCount, string[] headers, List<string>[] columns)
    {
        var lines = new List<string> { $"Format: {format}", $"Rows sampled: {rowCount}", $"Columns: {headers.Length}" };
        for (var index = 0; index < headers.Length; index++)
        {
            var nonEmpty = columns[index].Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var numeric = nonEmpty.Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : (double?)null).ToArray();
            var numericValues = numeric.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            var type = nonEmpty.Length > 0 && numericValues.Length == nonEmpty.Length ? "number" : "text/mixed";
            var stats = numericValues.Length == 0 ? "" : $", min={numericValues.Min():G5}, max={numericValues.Max():G5}, avg={numericValues.Average():G5}";
            lines.Add($"- {headers[index]}: {type}, populated={nonEmpty.Length}, missing={rowCount - nonEmpty.Length}, distinct={nonEmpty.Distinct().Take(101).Count()}{stats}");
        }
        return string.Join('\n', lines);
    }
}