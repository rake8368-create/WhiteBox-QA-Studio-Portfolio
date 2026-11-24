using TreeSitter;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class CppAnalyzerTests
{
    private const string EmptyCatch = "void run() { try { work(); } catch (...) {} }\n";
    private const string BrokenStatement = "int broken = ;\n";

    [Theory]
    [InlineData("main.cpp")]
    [InlineData("main.CPP")]
    [InlineData("main.cc")]
    [InlineData("main.CC")]
    [InlineData("main.cxx")]
    [InlineData("main.CXX")]
    [InlineData("main.hpp")]
    [InlineData("main.HPP")]
    [InlineData("main.hh")]
    [InlineData("main.HH")]
    [InlineData("main.hxx")]
    [InlineData("main.HXX")]
    public void ParsesComplexCppUsingEachSupportedExtension(string fileName)
    {
        const string code = """
            #include <utility>
            #define VALUE 2
            #define SUM(a, b) ((a) + (b))
            namespace audit {
                template<class T>
                class Box {
                    T value;
                public:
                    explicit Box(T initial) : value{initial} {}
                    ~Box() = default;
                    T get() const { return value; }
                };
                int work(int* pointer, int& reference) {
                    auto sum = [&reference](int item) { return item + reference; };
                    const char* text = R"note(한글 "😀")note";
            #if MODE == 1
                    int mode = 1;
            #elif MODE == 2
                    int mode = 2;
            #else
                    int mode = 0;
            #endif
                    try { return SUM(sum(*pointer), VALUE) + mode; }
                    catch (const int& error) { return error; }
                }
            }
            """;
        var result = new AnalysisOrchestrator().Analyze(new("fixture", [new(fileName, code)], []));

        Assert.True(LanguageIdentification.HasSourceReader(LanguageIdentification.Identify(fileName)));
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Issues);
        Assert.Empty(result.ControlFlow!);
        Assert.Equal(new(SourceLanguage.Cpp, 1, 0) { AnalyzedFileCount = 1 }, Assert.Single(result.LanguageInventory!));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReportsErrorAndMissingTokenAtOriginalUtf16Positions(string newline)
    {
        const string prefix = "\tconst char* text = \"한😀\"; ";
        var code = $"void run() {{{newline}{prefix}int broken = ;{newline}\twork(1;{newline}}}";

        var result = Analyze(code);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Collection(result.Findings,
            error =>
            {
                Assert.Equal((2, prefix.Length + "int broken ".Length + 1), (error.Line, error.Column));
                Assert.Contains("한😀", error.Evidence);
                Assert.Contains("해석하지 못했습니다", error.Description);
            },
            missing =>
            {
                Assert.Equal((3, "\twork(1".Length + 1), (missing.Line, missing.Column));
                Assert.Contains("필요한 토큰())", missing.Description);
            });
        Assert.All(result.Findings, finding =>
        {
            Assert.Equal(("CPP-001", Severity.High), (finding.RuleId, finding.Severity));
            Assert.Contains("Tree-sitter의 구문 복구 결과", finding.Caution);
            Assert.NotEmpty(finding.Recommendation);
        });
    }

    [Theory]
    [InlineData("void incomplete() {\r\n\treturn;", 2, 9)]
    [InlineData("void incomplete() {\n\treturn; // 한😀", 2, 16)]
    [InlineData("void incomplete() {\n\treturn;\n", 2, 9)]
    public void ReportsMissingBraceAtTheParserInsertionPointNearEof(string code, int line, int column)
    {
        var finding = Assert.Single(Analyze(code).Findings);

        Assert.Equal(("CPP-001", line, column), (finding.RuleId, finding.Line, finding.Column));
        Assert.Contains("필요한 토큰(})", finding.Description);
        Assert.NotEmpty(finding.Evidence);
    }

    [Fact]
    public void DeduplicatesNestedSyntaxNodesAtTheSamePositionDeterministically()
    {
        const string code = "void f() { @; @; }";
        using var language = TreeSitterSupport.LoadLanguage("Cpp");
        using var parser = new Parser(language);
        using var tree = parser.Parse(code)!;
        var starts = TreeSitterSupport.Descendants(tree.RootNode)
            .Where(node => node.IsError || node.IsMissing).Select(node => node.StartIndex).ToArray();
        Assert.Equal(new[] { 11, 11, 14, 14 }, starts);

        var findings = Analyze(code).Findings;

        Assert.Equal(starts.Distinct().Order().Select(index => (1, index + 1)),
            findings.Select(finding => (finding.Line, finding.Column)));
        Assert.Equal(findings, Analyze(code).Findings);
    }

    [Theory]
    [InlineData("#if 0\nint broken = ;\n#endif\n", 2, 12)]
    [InlineData("#define OPEN {\nvoid f() OPEN\n    return;\n}\n", 2, 9)]
    public void ExplainsSyntaxRecoveryInInactiveBranchesAndMacroDependentCode(string code, int line, int column)
    {
        var result = Analyze(code);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Contains(result.Findings, finding => (finding.Line, finding.Column) == (line, column));
        Assert.All(result.Findings, finding =>
        {
            Assert.Equal(("CPP-001", Severity.High), (finding.RuleId, finding.Severity));
            Assert.Contains("비활성 전처리 분기", finding.Caution);
            Assert.Contains("매크로 확장", finding.Caution);
            Assert.Contains("지원하지 않는 문법", finding.Caution);
        });
    }

    [Theory]
    [InlineData("...", "")]
    [InlineData("const Error& error", "/* note */ // ignored\n")]
    [InlineData("Error", ";;;")]
    [InlineData("...", "{ /* note */ { ; } } {}")]
    [InlineData("...", "// #if 0\n /* #define X */ ;")]
    public void ReportsCatchWithOnlyEmptyStatementsBlocksOrComments(string parameters, string body)
    {
        var code = $"void run() {{ try {{ work(); }} catch ({parameters}) {{ {body} }} }}";
        var result = Analyze(code);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(("CPP-002", Severity.Low, 1, code.IndexOf("catch", StringComparison.Ordinal) + 1),
            (finding.RuleId, finding.Severity, finding.Line, finding.Column));
        Assert.Contains("의도적으로", finding.Caution);
    }

    [Theory]
    [InlineData("int ignored;")]
    [InlineData("work();")]
    [InlineData("work /* note */ ();")]
    [InlineData("{ work(); }")]
    [InlineData("return;")]
    [InlineData("throw error;")]
    [InlineData("throw;")]
    [InlineData("if (ready) ;")]
    [InlineData("(void)error;")]
    [InlineData("const char* text = \"#if 0\";")]
    public void AcceptsCatchWithDeclarationOrExecutableStatement(string body)
    {
        var result = Analyze($"void run() {{ try {{ work(); }} catch (const Error& error) {{ {body} }} }}");

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void EvaluatesNestedCatchesIndependently()
    {
        var result = Analyze("""
            void run() {
                try {
                    try {} catch (...) {}
                } catch (const Error& error) {
                    try { work(); } catch (...) { { ; } }
                } catch (...) {}
            }
            """);

        Assert.Empty(result.Issues);
        Assert.Equal(new[] { 3, 5, 6 }, result.Findings.Select(finding => finding.Line));
        Assert.All(result.Findings, finding => Assert.Equal("CPP-002", finding.RuleId));
    }

    [Fact]
    public void SkipsCatchesInEveryConditionalBranchAndCatchesWithDirectivesInTheirBodies()
    {
        var result = Analyze("""
            #if FEATURE
            void a() { try {} catch (...) {} }
            #elif OTHER
            void b() { try {} catch (...) {} }
            #else
            void c() { try {} catch (...) { work(); } }
            #endif
            void run() {
            #ifndef GUARD
                try {} catch (...) {}
            #endif
                try {} catch (...) {
                    {
            #define HANDLED 1
                    }
                    work();
                }
                try {} catch (...) {
            #pragma message("skipped")
                }
                try {} catch (...) {
            #if 0
                    work();
            #endif
                }
                try {} catch (...) {}
            }
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(("CPP-002", 26), (finding.RuleId, finding.Line));
        var issue = Assert.Single(result.Issues);
        Assert.Contains("catch 7개", issue.Reason);
        Assert.Contains("전처리 지시문", issue.Reason);
        Assert.Contains("활성 분기 선택과 매크로 확장", issue.Reason);
    }

    [Fact]
    public void PreprocessorOmissionsKeepIndependentNestedCatchesAvailable()
    {
        var result = Analyze("""
            void run() {
                try {} catch (...) {
                    try {} catch (...) {}
            #if FEATURE
                    try {} catch (...) {}
            #endif
                }
            }
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(("CPP-002", 3), (finding.RuleId, finding.Line));
        Assert.Contains("catch 2개", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void ClassMemberCatchUsesItsEnclosingConditionalBranch()
    {
        var result = Analyze("""
            struct Handler {
            #ifdef FEATURE
            #if NESTED
                void nested() { try {} catch (...) {} }
            #endif
            #else
                void alternative() { try {} catch (...) {} }
            #endif
                void ordinary() { try {} catch (...) {} }
            };
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(("CPP-002", 9), (finding.RuleId, finding.Line));
        Assert.Contains("catch 2개", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void SkipsCatchRuleOnlyForFileWithSyntaxErrors()
    {
        var result = new CppAnalyzer().Analyze([new("broken.cpp", BrokenStatement + EmptyCatch),
            new("healthy.hpp", EmptyCatch)]);

        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Equal(new[] { ("broken.cpp", "CPP-001"), ("healthy.hpp", "CPP-002") },
            result.Findings.Select(finding => (finding.FileName, finding.RuleId)));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("broken.cpp", issue.FileName);
        Assert.Contains("CPP-002 검토를 생략", issue.Reason);
    }

    [Fact]
    public void IsolatesNativeInitializationParseAndNullTreeFailuresAndDisposesResources()
    {
        using var language = TreeSitterSupport.LoadLanguage("Cpp");
        var parsers = new List<Parser>();
        var parsedWith = new List<Parser>();
        var trees = new List<Tree>();
        var attempts = 0;
        var analyzer = new CppAnalyzer(() =>
        {
            if (attempts++ == 0)
                throw new DllNotFoundException();
            var parser = new Parser(language);
            parsers.Add(parser);
            return parser;
        }, (parser, code) =>
        {
            parsedWith.Add(parser);
            if (code == "native failure")
                throw new EntryPointNotFoundException();
            if (code == "parse failure")
                throw new InvalidOperationException();
            if (code == "null tree")
                return null;
            var tree = parser.Parse(code)!;
            trees.Add(tree);
            return tree;
        });
        Assert.Equal(0, attempts);

        var result = analyzer.Analyze([new("load.cpp", ""), new("native.cpp", "native failure"),
            new("parse.cpp", "parse failure"), new("null.cpp", "null tree"), new("good.cpp", EmptyCatch)]);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal("good.cpp", Assert.Single(result.Findings).FileName);
        Assert.Equal(5, attempts);
        Assert.Equal(4, parsedWith.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(parsers, parsedWith);
        Assert.All(parsers, parser => Assert.Throws<ObjectDisposedException>(() => parser.Parse("")));
        Assert.Throws<ObjectDisposedException>(() => Assert.Single(trees).RootNode);
        Assert.Collection(result.Issues,
            issue => Assert.Contains("DllNotFoundException", issue.Reason),
            issue => Assert.Contains("EntryPointNotFoundException", issue.Reason),
            issue => Assert.Contains("InvalidOperationException", issue.Reason),
            issue => Assert.Contains("구문 트리를 생성하지 못했습니다", issue.Reason));
    }

    [Fact]
    public void EnforcesDirectUtf16LimitsBeforeParserCreation()
    {
        var atLimit = new string(' ', CppAnalyzer.MaxInputCharactersPerFile - 2) + "😀";
        using var language = TreeSitterSupport.LoadLanguage("Cpp");
        var created = 0;
        var analyzer = new CppAnalyzer(() =>
        {
            created++;
            return new Parser(language);
        }, (parser, code) =>
        {
            Assert.Same(atLimit, code);
            return parser.Parse("");
        });
        var documents = new List<SourceDocument> { new("over-file.cpp", atLimit + "x") };
        documents.AddRange(Enumerable.Range(0, 16).Select(index => new SourceDocument($"{index}.cpp", atLimit)));
        documents.Add(new("over-batch.cpp", " "));

        var result = analyzer.Analyze(documents);

        Assert.Equal(2 * 1024 * 1024, atLimit.Length);
        Assert.Equal(32 * 1024 * 1024, atLimit.Length * created);
        Assert.Equal(16, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Collection(result.Issues,
            issue =>
            {
                Assert.Equal("over-file.cpp", issue.FileName);
                Assert.Contains("파일당", issue.Reason);
                Assert.Contains("UTF-16", issue.Reason);
                Assert.Contains("하드 상한", issue.Reason);
            },
            issue =>
            {
                Assert.Equal("over-batch.cpp", issue.FileName);
                Assert.Contains("전체", issue.Reason);
            });
    }

    [Theory]
    [InlineData("CPP-001", 100, 2_000, BrokenStatement, EmptyCatch)]
    [InlineData("CPP-002", 500, 5_000, EmptyCatch, BrokenStatement)]
    public void EnforcesBothDiagnosticLimitsWithoutConsumingTheOtherRuleBudget(
        string rule, int perFile, int perBatch, string sample, string otherSample)
    {
        var atLimit = string.Concat(Enumerable.Repeat(sample, perFile));
        var documents = new List<SourceDocument> { new("file-over.cpp", atLimit + sample) };
        documents.AddRange(Enumerable.Range(1, perBatch / perFile - 1)
            .Select(index => new SourceDocument($"{index}.cpp", atLimit)));
        documents.AddRange([new("no-diagnostic.cpp", "int ok;"), new("batch-over.cpp", sample),
            new("other.cpp", otherSample)]);

        var result = new CppAnalyzer().Analyze(documents);

        Assert.Equal(documents.Count, result.AnalyzedFileCount);
        Assert.Equal(perBatch, result.Findings.Count(finding => finding.RuleId == rule));
        Assert.Equal(perFile, result.Findings.Count(finding => finding.FileName == "file-over.cpp"));
        Assert.Equal("other.cpp", Assert.Single(result.Findings, finding => finding.RuleId != rule).FileName);
        Assert.Equal(new[] { "file-over.cpp", "batch-over.cpp" }, result.Issues
            .Where(issue => issue.Reason.Contains("추가 진단을 생략")).Select(issue => issue.FileName));
    }

    private static LanguageAnalysis Analyze(string code) => new CppAnalyzer().Analyze([new("sample.cpp", code)]);
}
