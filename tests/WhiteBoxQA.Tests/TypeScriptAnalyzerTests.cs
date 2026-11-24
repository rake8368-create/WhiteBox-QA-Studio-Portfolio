using TreeSitter;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class TypeScriptAnalyzerTests
{
    private const string EmptyCatch = "try {} catch {}\n";
    private const string Broken = "const broken = ;\n";

    [Theory]
    [InlineData("types.ts", "interface Box<T> { value: T } export const n = <number>value;")]
    [InlineData("module.MTS", "import type { Model } from './model.js'; export type Item = Model;")]
    [InlineData("module.cTs", "import value = require('./value.cjs'); export = value;")]
    [InlineData("View.TsX", "const id = <T,>(x: T) => x; export const View = () => <><span>{id(1)}</span></>;")]
    public void SelectsGrammarByExtension(string file, string code)
    {
        var result = new AnalysisOrchestrator().Analyze(new("fixture", [new(file, code)], []));

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Issues);
        Assert.Empty(result.ControlFlow!);
        Assert.Equal(new LanguageFileCount(SourceLanguage.TypeScript, 1, 0) { AnalyzedFileCount = 1 },
            Assert.Single(result.LanguageInventory!));
    }

    [Fact]
    public void DialectsDifferAndSyntaxIssuesOnlyReflectParserRecovery()
    {
        var result = Analyze(
            new("jsx-in-ts.ts", "const view = <><div /></>;"),
            new("assertion.tsx", "const n = <number>value;"),
            new("missing-close.tsx", "const view = <div><span /></div;"),
            new("mismatch.tsx", "const view = <div><span /></section>;"));

        Assert.Equal(4, result.AnalyzedFileCount);
        Assert.Equal(new[] { "jsx-in-ts.ts", "assertion.tsx", "missing-close.tsx" },
            result.Issues.Select(issue => issue.FileName));
        Assert.All(result.Findings, finding =>
        {
            Assert.Equal(("TS-001", Severity.High), (finding.RuleId, finding.Severity));
            Assert.Contains("TSX 태그 이름 불일치", finding.Caution!);
        });
        var missing = Assert.Single(result.Findings, finding => finding.FileName == "missing-close.tsx");
        Assert.Equal((1, 32), (missing.Line, missing.Column));
        Assert.Contains(">", missing.Description);
    }

    [Theory]
    [InlineData("ts", "\n")]
    [InlineData("tsx", "\r\n")]
    public void ReportsOriginalUtf16CoordinatesAndDeduplicatesByStart(string extension, string newline)
    {
        var documents = new SourceDocument[]
        {
            new($"unicode.{extension}", "const ok = 1;" + newline + "\tconst label = '한😀'; const broken = ;" + newline),
            new($"eof.{extension}", "function incomplete(): void {" + newline + "\treturn;"),
            new($"dedupe.{extension}", @"function f() { \; \; }")
        };
        var result = Analyze(documents);

        Assert.Equal(new[] { (2, 36), (2, 9), (1, 16), (1, 19) },
            result.Findings.Select(finding => (finding.Line, finding.Column)));
        Assert.Contains("한😀", result.Findings[0].Evidence);
        Assert.Contains("}", result.Findings[1].Description);
        Assert.Equal(result.Findings, Analyze(documents).Findings);
    }

    [Theory]
    [InlineData("ts")]
    [InlineData("tsx")]
    public void FindsEmptyCatchesIndependentlyAndSkipsSyntaxErrorFiles(string extension)
    {
        var result = Analyze(new($"broken.{extension}", Broken + EmptyCatch), new($"valid.{extension}", """
            function run() {
                try {} catch {}
                try {} catch (error) { /* ignored */ ; { { ; } } }
                try {} catch (error) { const saved: unknown = error; }
                try {} catch { recover(); }
                try {} catch { return; }
                try {} catch { throw new Error(); }
                try {} catch { try {} catch { ;; } }
            }
            """));

        Assert.Equal(new[] { ("TS-001", 1), ("TS-002", 2), ("TS-002", 3), ("TS-002", 8) },
            result.Findings.Select(finding => (finding.RuleId, finding.Line)));
        Assert.All(result.Findings.Skip(1), finding => Assert.Equal(Severity.Low, finding.Severity));
        Assert.Contains("TS-002 검토를 생략", Assert.Single(result.Issues).Reason);
    }

    [Theory]
    [InlineData("TypeScript", "a.ts", "b.tsx", "const view = <span />;")]
    [InlineData("Tsx", "a.tsx", "b.ts", "const n = <number>value;")]
    public void IsolatesGrammarLoadParseAndNullTreeFailures(string badGrammar, string bad, string good, string code)
    {
        using var other = TreeSitterSupport.LoadLanguage(badGrammar == "Tsx" ? "TypeScript" : "Tsx");
        var parsers = new List<Parser>();
        Tree? successfulTree = null;
        var analyzer = new TypeScriptAnalyzer(grammar =>
        {
            if (grammar == badGrammar)
                throw new DllNotFoundException();
            var parser = new Parser(other);
            parsers.Add(parser);
            return parser;
        }, (parser, text) => text switch
        {
            "failed" => throw new InvalidOperationException(),
            "null" => null,
            _ => successfulTree = parser.Parse(text)
        });
        string extension = Path.GetExtension(good);
        var result = new AnalysisOrchestrator([analyzer, new CSharpAnalyzer()]).Analyze(new("mixed",
            [new(bad, code), new("fail" + extension, "failed"), new("null" + extension, "null"),
                new(good, code + " " + EmptyCatch), new("existing.cs", "class Existing {}")], []));

        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Equal(good, Assert.Single(result.Findings).FileName);
        Assert.Equal(3, result.Issues.Count);
        Assert.Contains("DllNotFoundException", result.Issues[0].Reason);
        Assert.Contains("InvalidOperationException", result.Issues[1].Reason);
        Assert.Contains("구문 트리", result.Issues[2].Reason);
        Assert.Equal(3, parsers.Count);
        Assert.All(parsers, parser => Assert.Throws<ObjectDisposedException>(() => parser.Parse("")));
        Assert.NotNull(successfulTree);
        Assert.Throws<ObjectDisposedException>(() => successfulTree.RootNode);
    }

    [Fact]
    public void EnforcesFileAndBatchInputLimitsBeforeParsingAcrossDialects()
    {
        var atLimit = new string(' ', TypeScriptAnalyzer.MaxInputCharactersPerFile - 2) + "😀";
        using var language = TreeSitterSupport.LoadLanguage("TypeScript");
        var analyzer = new TypeScriptAnalyzer(_ => new Parser(language), (parser, code) =>
        {
            Assert.Same(atLimit, code);
            return parser.Parse("");
        });
        var documents = Enumerable.Range(0, 16)
            .Select(index => new SourceDocument(index % 2 == 0 ? $"{index}.ts" : $"{index}.tsx", atLimit))
            .Prepend(new("too-large.ts", atLimit + " ")).Append(new("too-many.tsx", " ")).ToArray();

        var result = analyzer.Analyze(documents);

        Assert.Equal(16, result.AnalyzedFileCount);
        Assert.Equal(new[] { "too-large.ts", "too-many.tsx" }, result.Issues.Select(issue => issue.FileName));
        Assert.Contains("파일당", result.Issues[0].Reason);
        Assert.Contains("전체", result.Issues[1].Reason);
    }

    [Theory]
    [InlineData("TS-001", 100, 2_000, Broken, EmptyCatch)]
    [InlineData("TS-002", 500, 5_000, EmptyCatch, Broken)]
    public void EnforcesIndependentDiagnosticBudgetsAcrossDialects(string rule, int perFile, int batch, string sample, string other)
    {
        var exact = string.Concat(Enumerable.Repeat(sample, perFile));
        var files = Enumerable.Range(0, batch / perFile)
            .Select(index => new SourceDocument(index % 2 == 0 ? $"{index}.ts" : $"{index}.tsx", index == 0 ? exact + sample : exact))
            .Append(new("extra.tsx", sample)).Append(new("other.tsx", other)).ToArray();

        var result = Analyze(files);

        Assert.Equal(files.Length, result.AnalyzedFileCount);
        Assert.Equal(batch, result.Findings.Count(finding => finding.RuleId == rule));
        Assert.Equal(perFile, result.Findings.Count(finding => finding.FileName == "0.ts"));
        Assert.Equal("other.tsx", Assert.Single(result.Findings, finding => finding.RuleId != rule).FileName);
        Assert.Equal(new[] { "0.ts", "extra.tsx" }, result.Issues
            .Where(issue => issue.Reason.Contains("추가 진단", StringComparison.Ordinal)).Select(issue => issue.FileName));
    }

    private static LanguageAnalysis Analyze(params SourceDocument[] files) => new TypeScriptAnalyzer().Analyze(files);
}
