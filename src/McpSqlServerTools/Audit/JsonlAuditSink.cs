using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpSqlServerTools.Audit;

/// <summary>
/// Appends one JSON object per line. A semaphore serialises writes so two tool calls
/// finishing at the same moment cannot interleave their lines into a broken one.
/// </summary>
public sealed class JsonlAuditSink : IAuditSink, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private JsonlAuditSink(TextWriter writer, bool ownsWriter)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
    }

    public static JsonlAuditSink ForPath(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new JsonlAuditSink(new StreamWriter(stream) { AutoFlush = true }, ownsWriter: true);
    }

    // MCP_AUDIT_PATH unset falls back here rather than disabling audit, so the feature is
    // always on and a missing config value cannot silently turn it off.
    public static JsonlAuditSink ForStandardError() => new(Console.Error, ownsWriter: false);

    public async Task WriteAsync(AuditRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions);

        await _gate.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(line);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsWriter) _writer.Dispose();
    }
}
