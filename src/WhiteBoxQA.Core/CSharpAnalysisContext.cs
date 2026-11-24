using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace WhiteBoxQA.Core;

// Callers supply project settings explicitly. Analysis does not open projects or run builds.
public sealed record CSharpAnalysisContext
{
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = [];
    public IReadOnlyList<MetadataReference> AdditionalReferences { get; init; } = [];
    public LanguageVersion? LanguageVersion { get; init; }
}

public sealed record CSharpProjectContext(string ProjectPath, CSharpAnalysisContext Settings)
{
    internal CSharpProjectNode? Node { get; init; }

    public string LanguageLabel => Settings.LanguageVersion is { } version
        ? $"C# {version.ToDisplayString()}" : AnalysisScope.Language;
}
