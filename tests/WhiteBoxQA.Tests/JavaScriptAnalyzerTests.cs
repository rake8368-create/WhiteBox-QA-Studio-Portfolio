using TreeSitter;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class JavaScriptAnalyzerTests
{
    private const string EmptyCatch = "try { work(); } catch {}";
    private const string BrokenStatement = "const broken = ;\n";

    [Theory]
    [InlineData("main.js", "const pick = (x) => x?.value ?? 0; class C { #n = 1; }")]
    [InlineData("main.MJS", "import { work } from './dep.js'; export const status = await work();")]
    [InlineData("main.cjs", "#!/usr/bin/env node\nconst dep = require('./dep.cjs'); module.exports = dep;")]
    [InlineData("view.JSX", "export const View = ({value}) => <><span>{value}</span><input /></>;")]
    public void ParsesSupportedSources(string fileName, string code)
    {
        var result = new AnalysisOrchestrator().Analyze(new("fixture", [new(fileName, code)], []));

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Empty(result.Findings);
        Assert.Empty(result.ControlFlow!);
        Assert.Equal(new(SourceLanguage.JavaScript, 1, 0) { AnalyzedFileCount = 1 },
            Assert.Single(result.LanguageInventory!));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReportsErrorAndMissingTokenAtOriginalUtf16Positions(string newline)
    {
        const string prefix = "\tconst 한글 = '😀'; ";
        var code = $"const ok = 1;{newline}{prefix}const broken = ;{newline}{prefix}f(1;";

        var result = Analyze(code);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Collection(result.Findings,
            error => Assert.Equal((2, prefix.Length + "const broken ".Length + 1), (error.Line, error.Column)),
            missing =>
            {
                Assert.Equal((3, prefix.Length + "f(1".Length + 1), (missing.Line, missing.Column));
                Assert.Contains("누락", missing.Description);
            });
        Assert.All(result.Findings, finding =>
        {
            Assert.Equal(("JS-001", Severity.High), (finding.RuleId, finding.Severity));
            Assert.Contains("한글", finding.Evidence);
            Assert.Contains("😀", finding.Evidence);
            Assert.Contains("지원하지 않는 문법", finding.Caution);
        });
    }

    [Fact]
    public void ReportsMissingTokenAtEndOfSource()
    {
        const string code = "function f() {\n\treturn 1;";

        var finding = Assert.Single(Analyze(code).Findings);

        Assert.Equal(("JS-001", 2, "\treturn 1;".Length + 1), (finding.RuleId, finding.Line, finding.Column));
        Assert.Contains("필요한 토큰(})", finding.Description);
    }

    [Fact]
    public void DeduplicatesNestedErrorNodesAtTheSamePositionDeterministically()
    {
        const string code = "?; }";
        using var language = TreeSitterSupport.LoadLanguage("JavaScript");
        using var parser = new Parser(language);
        using var tree = parser.Parse(code)!;
        Assert.Equal(new[] { 0, 0, 3 }, TreeSitterSupport.Descendants(tree.RootNode)
            .Where(node => node.IsError || node.IsMissing).Select(node => node.StartIndex));

        var findings = Analyze(code).Findings;

        Assert.Equal(new[] { (1, 1), (1, 4) }, findings.Select(finding => (finding.Line, finding.Column)));
        Assert.Equal(findings, Analyze(code).Findings);
    }

    [Fact]
    public void ReportsInvalidJsxAsHighSeverity()
    {
        var result = Analyze("export const View = () => <div>", "view.jsx");

        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding => Assert.Equal(("JS-001", Severity.High), (finding.RuleId, finding.Severity)));
    }

    [Theory]
    [InlineData("try { work(); } catch {}")]
    [InlineData("try { work(); } catch (error) { /* note */ // ignored\n }")]
    [InlineData("try { work(); } catch { <!-- legacy comment\n }")]
    [InlineData("try { work(); } catch (error) { ;;; }")]
    [InlineData("try { work(); } catch { { /* note */ { ; } } {} }")]
    [InlineData("try { work(); } catch {} finally { cleanup(); }")]
    [InlineData("async function run() { try { await work(); } catch {} }")]
    public void ReportsCatchWithoutExecutableContent(string code)
    {
        var result = Analyze(code);

        Assert.Empty(result.Issues);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(("JS-002", Severity.Low, 1, code.IndexOf("catch", StringComparison.Ordinal) + 1),
            (finding.RuleId, finding.Severity, finding.Line, finding.Column));
        Assert.Contains("의도적으로", finding.Caution);
    }

    [Theory]
    [InlineData("console.warn(error);")]
    [InlineData("{ recover(error); }")]
    [InlineData("const message = error.message;")]
    [InlineData("return;")]
    [InlineData("throw error;")]
    [InlineData("await recover(error);")]
    public void AcceptsCatchContainingExecutableContent(string body)
    {
        var result = Analyze($"async function run() {{ try {{ await work(); }} catch (error) {{ {body} }} finally {{}} }}");

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void EvaluatesNestedCatchesIndependentlyOfEachOtherAndFinally()
    {
        var result = Analyze("""
            try {
                try { work(); } catch {}
            } catch (outer) {
                try { recover(outer); } catch (inner) { { ; } }
            } finally {
                try { cleanup(); } catch {}
            }
            """);

        Assert.Empty(result.Issues);
        Assert.Equal(new[] { 2, 4, 6 }, result.Findings.Select(finding => finding.Line));
        Assert.All(result.Findings, finding => Assert.Equal("JS-002", finding.RuleId));
    }

    [Fact]
    public void SkipsCatchRuleOnlyForFileWithSyntaxErrors()
    {
        var result = new AnalysisOrchestrator().Analyze(new("mixed",
            [new("broken.js", BrokenStatement + EmptyCatch), new("valid.cjs", EmptyCatch), new("other.cs", "class C {}")], []));

        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(new[] { ("broken.js", "JS-001"), ("valid.cjs", "JS-002") },
            result.Findings.Select(finding => (finding.FileName, finding.RuleId)));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("broken.js", issue.FileName);
        Assert.Contains("JS-002 검토를 생략", issue.Reason);
    }

    [Fact]
    public void RecoversFromInitializationFailureParseFailureAndNullTree()
    {
        using var language = TreeSitterSupport.LoadLanguage("JavaScript");
        var created = new List<Parser>();
        var seen = new List<Parser>();
        var attempts = 0;
        var analyzer = new JavaScriptAnalyzer(() =>
        {
            if (attempts++ == 0)
                throw new DllNotFoundException();
            var parser = new Parser(language);
            created.Add(parser);
            return parser;
        }, (parser, code) =>
        {
            seen.Add(parser);
            return code switch
            {
                "throw" => throw new InvalidOperationException(),
                "null" => null,
                _ => parser.Parse(code)
            };
        });
        var orchestrator = new AnalysisOrchestrator([analyzer, new CSharpAnalyzer()]);

        var result = orchestrator.Analyze(new("mixed", [new("load.js", ""), new("throw.js", "throw"),
            new("null.js", "null"), new("ok.jsx", EmptyCatch), new("other.cs", "class C {}")], []));

        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal("ok.jsx", Assert.Single(result.Findings).FileName);
        Assert.Equal(4, attempts);
        Assert.Equal(3, seen.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(created.Zip(seen), pair => Assert.Same(pair.First, pair.Second));
        Assert.All(created, parser => Assert.Throws<ObjectDisposedException>(() => parser.Parse("")));
        Assert.Collection(result.Issues,
            issue => Assert.Contains("DllNotFoundException", issue.Reason),
            issue => Assert.Contains("InvalidOperationException", issue.Reason),
            issue => Assert.Contains("구문 트리를 생성하지 못했습니다", issue.Reason));
        Assert.Contains(new LanguageFileCount(SourceLanguage.JavaScript, 4, 0) { AnalyzedFileCount = 1 },
            result.LanguageInventory!);
    }

    [Fact]
    public void EnforcesDirectUtf16LimitsBeforeParserCreation()
    {
        const int fileLimit = JavaScriptAnalyzer.MaxInputCharactersPerFile;
        const int batchLimit = JavaScriptAnalyzer.MaxInputCharactersPerBatch;
        var atLimit = new string(' ', fileLimit - 2) + "😀";
        using var language = TreeSitterSupport.LoadLanguage("JavaScript");
        var created = 0;
        var analyzer = new JavaScriptAnalyzer(() =>
        {
            created++;
            return new Parser(language);
        }, (parser, code) =>
        {
            Assert.Same(atLimit, code);
            return parser.Parse("");
        });
        var documents = new List<SourceDocument> { new("oversized.js", atLimit + "x") };
        documents.AddRange(Enumerable.Range(0, batchLimit / fileLimit).Select(index => new SourceDocument($"{index}.js", atLimit)));
        documents.Add(new("over-total.js", " "));

        var result = analyzer.Analyze(documents);

        Assert.Equal(16, created);
        Assert.Equal(16, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Collection(result.Issues,
            issue =>
            {
                Assert.Equal("oversized.js", issue.FileName);
                Assert.Contains("파일당", issue.Reason);
                Assert.Contains("UTF-16", issue.Reason);
            },
            issue =>
            {
                Assert.Equal("over-total.js", issue.FileName);
                Assert.Contains("전체", issue.Reason);
            });
    }

    [Fact]
    public void CapsSyntaxDiagnosticsPerFileAndBatchWithoutUsingCatchBudget()
    {
        var atLimit = string.Concat(Enumerable.Repeat(BrokenStatement, JavaScriptAnalyzer.MaxSyntaxFindingsPerFile));
        var documents = DocumentsAtDiagnosticLimit(atLimit, BrokenStatement, JavaScriptAnalyzer.MaxSyntaxFindingsPerBatch,
            JavaScriptAnalyzer.MaxSyntaxFindingsPerFile, "syntax").Append(new("healthy.js", EmptyCatch)).ToArray();

        var result = new JavaScriptAnalyzer().Analyze(documents);

        Assert.Equal(documents.Length, result.AnalyzedFileCount);
        Assert.Equal(2_000, result.Findings.Count(finding => finding.RuleId == "JS-001"));
        Assert.Equal(100, result.Findings.Count(finding => finding.FileName == "syntax-0.js"));
        Assert.Equal("healthy.js", Assert.Single(result.Findings, finding => finding.RuleId == "JS-002").FileName);
        Assert.Equal(new[] { "syntax-0.js", "syntax-over.js" }, result.Issues
            .Where(issue => issue.Reason.Contains("추가 진단을 생략")).Select(issue => issue.FileName));
    }

    [Fact]
    public void CapsCatchDiagnosticsPerFileAndBatchWithoutUsingSyntaxBudget()
    {
        var repeated = EmptyCatch + "\n";
        var atLimit = string.Concat(Enumerable.Repeat(repeated, JavaScriptAnalyzer.MaxRuleFindingsPerFile));
        var documents = DocumentsAtDiagnosticLimit(atLimit, repeated, JavaScriptAnalyzer.MaxRuleFindingsPerBatch,
            JavaScriptAnalyzer.MaxRuleFindingsPerFile, "catch").Append(new("broken.js", BrokenStatement)).ToArray();

        var result = new JavaScriptAnalyzer().Analyze(documents);

        Assert.Equal(documents.Length, result.AnalyzedFileCount);
        Assert.Equal(5_000, result.Findings.Count(finding => finding.RuleId == "JS-002"));
        Assert.Equal(500, result.Findings.Count(finding => finding.FileName == "catch-0.js"));
        Assert.Equal("broken.js", Assert.Single(result.Findings, finding => finding.RuleId == "JS-001").FileName);
        Assert.Equal(new[] { "catch-0.js", "catch-over.js" }, result.Issues
            .Where(issue => issue.Reason.Contains("추가 진단을 생략")).Select(issue => issue.FileName));
    }

    private static IEnumerable<SourceDocument> DocumentsAtDiagnosticLimit(string atLimit, string one, int total, int perFile, string prefix)
    {
        yield return new($"{prefix}-0.js", atLimit + one);
        for (var index = 1; index < total / perFile; index++)
            yield return new($"{prefix}-{index}.js", atLimit);
        yield return new($"{prefix}-over.js", one);
    }

    private static LanguageAnalysis Analyze(string code, string fileName = "sample.js") =>
        new JavaScriptAnalyzer().Analyze([new(fileName, code)]);
}
