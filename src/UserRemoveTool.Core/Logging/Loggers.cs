using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UserRemoveTool.Core.Logging;

public interface IAppLogger
{
    void Info(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>Best-effort diagnostic log. Never breaks the application.</summary>
public sealed class FileLogger(string directory) : IAppLogger
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INFO ", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message.ReplaceLineEndings(" | ")}";
        if (exception is not null) line += Environment.NewLine + exception;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"app-{DateTime.Now:yyyy-MM-dd}-{Environment.MachineName}-{Environment.UserName}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public enum AuditOutcome { Attempt, Success, Failure, Skipped, Cancelled }

public sealed record AuditEntry(
    DateTimeOffset Timestamp, string CorrelationId, string Operator, string Machine,
    string Action, AuditOutcome Outcome, string TargetAccount, string TargetDn, string Detail);

public interface IAuditLogger
{
    /// <summary>Writes one entry. Throws when the entry cannot be stored, so no unaudited change is made.</summary>
    void Write(string correlationId, string action, AuditOutcome outcome, string account, string dn, string detail = "");
}

/// <summary>Append-only JSON Lines audit log, flushed to disk on every entry.</summary>
public sealed class AuditLogger(string directory) : IAuditLogger
{
    private readonly object _gate = new();
    private readonly string _operator = WindowsIdentity.GetCurrent().Name;

    public void Write(string correlationId, string action, AuditOutcome outcome, string account, string dn, string detail = "")
    {
        var entry = new AuditEntry(DateTimeOffset.Now, correlationId, _operator, Environment.MachineName, action, outcome, account, dn, detail);
        var line = JsonSerializer.Serialize(entry, AuditJsonContext.Readable.AuditEntry) + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"audit-{DateTime.Now:yyyy-MM}-{Environment.MachineName}-{Environment.UserName}.jsonl");
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(Encoding.UTF8.GetBytes(line));
                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UserFacingException($"לא ניתן לכתוב ליומן הביקורת ({directory}). הפעולה נעצרה.", ex);
            }
        }
    }
}

[JsonSerializable(typeof(AuditEntry))]
internal sealed partial class AuditJsonContext : JsonSerializerContext
{
    public static AuditJsonContext Readable { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter<AuditOutcome>() }
    });
}
