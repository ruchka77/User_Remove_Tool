using System.Text.Json;
using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.Workflow;

/// <summary>Reads and writes the temporary JSON file of the active offboarding.</summary>
public sealed class RecordStore(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(Path.GetTempPath(), "UserRemoveTool");

    public string Save(OffboardingRecord record)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"offboard-{record.CorrelationId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, RecordJsonContext.Readable.OffboardingRecord));
        return path;
    }

    public OffboardingRecord Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), RecordJsonContext.Readable.OffboardingRecord)
        ?? throw new JsonException("empty record");

    public void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes files left behind by a crashed session.</summary>
    public void DeleteStale(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (var file in Directory.EnumerateFiles(_directory, "offboard-*.json"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > olderThan)
                    Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
