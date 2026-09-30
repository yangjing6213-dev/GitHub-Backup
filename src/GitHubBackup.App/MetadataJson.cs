using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHubBackup.App;

internal static class MetadataJson
{
    internal const int MaxFileBytes = 1024 * 1024;
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new RunStatusJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter<BackupMode>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<NetworkMode>(JsonNamingPolicy.CamelCase));
        return options;
    }

    internal static async Task<JsonDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken,
        long maxFileBytes = long.MaxValue)
    {
        using var handle = NativeFileSystem.Open(path);
        NativeFileSystem.Inspect(handle, path, directory: false);
        if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), System.Security.Principal.WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
            throw new UnauthorizedAccessException("ACL_PRIVATE_BOUNDARY_REQUIRED");
        await using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > maxFileBytes) throw new JsonException("Metadata file exceeds the size limit.");
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = false }, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class RunStatusJsonConverter : JsonConverter<RunStatus>
{
    public override RunStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "PASS" => RunStatus.Pass,
            "PARTIAL" => RunStatus.Partial,
            "FAIL" => RunStatus.Fail,
            "CANCELLED" => RunStatus.Cancelled,
            _ => throw new JsonException("Unknown status.")
        } : throw new JsonException("Status must be a string.");

    public override void Write(Utf8JsonWriter writer, RunStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            RunStatus.Pass => "PASS",
            RunStatus.Partial => "PARTIAL",
            RunStatus.Fail => "FAIL",
            RunStatus.Cancelled => "CANCELLED",
            _ => throw new JsonException("Unknown status.")
        });
}
