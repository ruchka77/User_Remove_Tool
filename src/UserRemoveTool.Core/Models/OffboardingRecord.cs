using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UserRemoveTool.Core.Models;

/// <summary>Data captured in step 2 and persisted to the temporary JSON file.</summary>
public sealed class OffboardingRecord
{
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public Guid ObjectGuid { get; set; }
    public string DisplayName { get; set; } = "";
    public string DistinguishedName { get; set; } = "";
    public string CurrentOu { get; set; } = "";
    [JsonPropertyName("mail")] public string Mail { get; set; } = "";
    [JsonPropertyName("homeDirectory")] public string HomeDirectory { get; set; } = "";
    [JsonPropertyName("samAccountName")] public string SamAccountName { get; set; } = "";
}

[JsonSerializable(typeof(OffboardingRecord))]
internal sealed partial class RecordJsonContext : JsonSerializerContext
{
    /// <summary>Keeps Hebrew readable in the file.</summary>
    public static RecordJsonContext Readable { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
