using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Infrastructure;

public sealed record HistoryLoadResult(IReadOnlyList<AnalysisResult> Entries, int SkippedCount);

public sealed class JsonAnalysisHistory(string directory)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.HangulSyllables),
        IgnoreReadOnlyProperties = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters =
        {
            new JsonStringEnumConverter<Severity>(allowIntegerValues: false),
            new JsonStringEnumConverter<MethodFlowStatus>(allowIntegerValues: false),
            new JsonStringEnumConverter<SourceLanguage>(allowIntegerValues: false),
            new JsonStringEnumConverter<SourceKind>(allowIntegerValues: false),
            new JsonStringEnumConverter<RethrowAssignment>(allowIntegerValues: false)
        }
    };

    public void Save(AnalysisResult result)
    {
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(directory, id + ".tmp");

        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, new StoredAnalysis(SchemaVersion, result), Options);

            File.Move(staging, Path.Combine(directory, id + ".json"));
        }
        finally
        {
            File.Delete(staging);
        }
    }

    public HistoryLoadResult Read()
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.json");
        }
        catch (DirectoryNotFoundException)
        {
            return new([], 0);
        }

        var entries = new List<AnalysisResult>(paths.Length);
        var skipped = 0;
        foreach (var path in paths)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var entry = JsonSerializer.Deserialize<StoredAnalysis>(stream, Options);
                if (entry is { Version: SchemaVersion } && IsValid(entry.Result))
                    entries.Add(entry.Result);
                else
                    skipped++;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                skipped++;
            }
        }

        return new(entries.OrderByDescending(result => result.AnalyzedAt).ToArray(), skipped);
    }

    private static bool IsValid(AnalysisResult result) =>
        result.AnalyzedFileCount >= 0
        && result.Findings.All(finding => finding is not null && finding.Line > 0 && finding.Column > 0)
        && result.Issues.All(issue => issue is not null)
        && (result.LanguageInventory?.All(item => item is not null && item.Language != SourceLanguage.Unknown
            && item.FileCount > 0 && item.MetadataOnlyCount >= 0 && item.MetadataOnlyCount <= item.FileCount
            && (item.AnalyzedFileCount is null || item.AnalyzedFileCount >= 0 && item.AnalyzedFileCount <= item.FileCount)) ?? true)
        && (result.ControlFlow?.All(method => method is not null
            && method.StartLine > 0 && method.EndLine >= method.StartLine
            && (method.Status == MethodFlowStatus.Unavailable ? method.Complexity is null : method.Complexity is > 0)
            && method.Blocks.All(block => block is not null)
            && method.Decisions.All(decision => decision is not null && decision.Line > 0)) ?? true);

    private sealed record StoredAnalysis(int Version, AnalysisResult Result);
}
