using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class JavaAnalyzerTests
{
    [Theory]
    [InlineData("package sample; public record Pair(int first, int second) {}")]
    [InlineData("class Sample { void run() { try { work(); } catch (Exception e) { recover(e); } } }")]
    [InlineData("class Sample { void run() { try { work(); } catch (Exception e) { throw e; } } }")]
    [InlineData("class Sample { void run() { try (var resource = open()) { work(); } finally { close(); } } }")]
    [InlineData("class Sample { void run() { try { work(); } catch (Exception e) { int value = 1; } } }")]
    [InlineData("// comment-only compilation unit")]
    public void ParsesValidSourceAndDoesNotProduceFlow(string code)
    {
        var result = Analyze(code);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Issues);
        Assert.Null(result.ControlFlow);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" /* rationale */ // ignored\n")]
    [InlineData("; { ; { /* empty */ } }")]
    public void ReportsOnlyInertCatchBlocks(string body)
    {
        var result = Analyze(Wrap($"try {{ work(); }}\ncatch (Exception ignored) {{ {body} }}"));
        var finding = Assert.Single(result.Findings);
        Assert.Equal(("JAVA-002", Severity.Low, "Sample.java", 2, 1),
            (finding.RuleId, finding.Severity, finding.FileName, finding.Line, finding.Column));
        Assert.StartsWith("catch (Exception", finding.Evidence);
        Assert.NotNull(finding.Caution);
    }

    [Theory]
    [InlineData("Throwable")]
    [InlineData("java.lang.Throwable")]
    [InlineData("java./* comment */lang.Throwable")]
    [InlineData("final @Annotated Throwable")]
    [InlineData("IOException | Throwable")]
    public void ReportsExactThrowableTypeOncePerCatch(string type)
    {
        var result = Analyze(Wrap($"try {{ work(); }} catch ({type} failure) {{ recover(failure); }}"));
        var finding = Assert.Single(result.Findings);
        Assert.Equal("JAVA-003", finding.RuleId);
        Assert.Equal(Severity.Moderate, finding.Severity);
        Assert.Contains("이름 기준", finding.Caution);
    }

    [Theory]
    [InlineData("IOException | SQLException")]
    [InlineData("custom.Throwable")]
    [InlineData("OtherThrowable")]
    [InlineData("@Throwable Exception")]
    public void OtherTypesAndAnnotationsDoNotMatchThrowable(string type) =>
        Assert.Empty(Analyze(Wrap($"try {{ work(); }} catch ({type} Throwable) {{ recover(Throwable); }}")).Findings);

    [Fact]
    public void NestedHandlersAreInspectedIndependently()
    {
        var result = Analyze(Wrap("""
            try { work(); }
            catch (Exception outer) {
                try { handle(outer); }
                catch (Throwable inner) { /* no recovery */ }
            }
            """));
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, finding => Assert.Equal(4, finding.Line));
        Assert.Equal(new[] { "JAVA-002", "JAVA-003" }, result.Findings.Select(finding => finding.RuleId));
    }

    [Fact]
    public void MissingTokenAndRecoveryErrorSuppressOtherRulesInTheirOwnFiles()
    {
        var result = new AnalysisOrchestrator().Analyze(new("Recovery",
            [
                new("Error.java", Wrap("try { work(}; } catch (Throwable ignored) {}")),
                new("Missing.java", "class Sample { int value = 1 }"),
                new("Good.java", Wrap("try { work(); } catch (Exception ignored) {}"))
            ], []));
        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.Empty(result.ControlFlow!);
        var error = Assert.Single(result.Findings, finding => finding.FileName == "Error.java");
        Assert.Equal("JAVA-001", error.RuleId);
        Assert.Equal(Severity.High, error.Severity);
        Assert.Contains("해석하지 못했습니다", error.Description);
        var missing = Assert.Single(result.Findings, finding => finding.FileName == "Missing.java");
        Assert.Equal("JAVA-001", missing.RuleId);
        Assert.Contains("토큰(;)", missing.Description);
        Assert.Equal("JAVA-002", Assert.Single(result.Findings, finding => finding.FileName == "Good.java").RuleId);
    }

    [Fact]
    public void MalformedSourceReportsDistinctPositions()
    {
        var result = Analyze("class Broken { void first() { run(}; } void second() { run(}; } }");
        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding => Assert.Equal("JAVA-001", finding.RuleId));
        Assert.Equal(result.Findings.Count, result.Findings.Select(finding => (finding.Line, finding.Column)).Distinct().Count());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void MultibyteTextAndTabsUseOneBasedUtf16SourceCoordinates(string newLine)
    {
        const string sourceLine = "\t\tString text = \"한😀\"; try { work(); } catch (Throwable e) {}";
        var code = string.Join(newLine, "class Sample {", "\tvoid check() {", sourceLine, "\t}", "}");
        var result = Analyze(code);
        var empty = Assert.Single(result.Findings, finding => finding.RuleId == "JAVA-002");
        var broad = Assert.Single(result.Findings, finding => finding.RuleId == "JAVA-003");
        Assert.Equal((3, sourceLine.IndexOf("catch", StringComparison.Ordinal) + 1), (empty.Line, empty.Column));
        Assert.Equal((3, sourceLine.IndexOf("Throwable", StringComparison.Ordinal) + 1), (broad.Line, broad.Column));
        Assert.Contains("한😀", empty.Evidence);
    }

    [Fact]
    public void MissingTokenAfterMultibyteTextKeepsItsLocation()
    {
        const string sourceLine = "\tString text = \"한😀\"";
        var result = Analyze($"class Sample {{\r\n{sourceLine}\r\n}}");
        var missing = Assert.Single(result.Findings);
        Assert.Equal(("JAVA-001", 2, sourceLine.Length + 1), (missing.RuleId, missing.Line, missing.Column));
        Assert.Contains("한😀", missing.Evidence);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void NestedRecoveryReportsDistinctErrorAndMissingLocations(string newline)
    {
        const string errorLine = "\t\tString text = \"한😀\"; var + 😀 + 😀 = ;";
        const string missingLine = "\t\tint value = 0";
        var code = string.Join(newline, "class Broken {", "\tvoid run() {", errorLine, missingLine, "\t}", "}");

        var result = Analyze(code);
        var errors = result.Findings.ToArray();
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.All(errors, finding => Assert.Equal("JAVA-001", finding.RuleId));
        Assert.Equal(new[]
        {
            (3, errorLine.IndexOf('+') + 1),
            (3, errorLine.IndexOf("😀 +", StringComparison.Ordinal) + 1),
            (3, errorLine.LastIndexOf("😀", StringComparison.Ordinal) + 1),
            (4, missingLine.Length + 1)
        }, errors.Select(finding => (finding.Line, finding.Column)));
        Assert.All(errors[..3], finding =>
        {
            Assert.Contains("해석하지 못했습니다", finding.Description);
            Assert.Contains("한😀", finding.Evidence);
        });
        Assert.Contains("토큰(;)", errors[3].Description);
    }

    [Theory]
    [InlineData(";", true)]
    [InlineData("{{}}", true)]
    [InlineData("/* { recover(e); } */", true)]
    [InlineData("{{ recover(e); }}", false)]
    [InlineData("{ throw e; }", false)]
    [InlineData("{ int declared; }", false)]
    [InlineData("{ class Local {} }", false)]
    [InlineData("if (ready) {}", false)]
    [InlineData("skip: {}", false)]
    public void EmptyCatchOnlyIgnoresCommentsEmptyStatementsAndPlainBlocks(string body, bool isEmpty)
    {
        var result = Analyze(Wrap($"try {{ work(); }} catch (Exception e) {{ {body} }}"));
        Assert.Empty(result.Issues);
        Assert.Equal(isEmpty ? new[] { "JAVA-002" } : [], result.Findings.Select(finding => finding.RuleId));
    }

    [Theory]
    [InlineData("Throwable", false)]
    [InlineData("Exception | /* why */ Throwable", false)]
    [InlineData("final @A(Throwable.class) Throwable", false)]
    [InlineData("java.lang.@A Throwable", true)]
    [InlineData("Throwable | java./* why */lang.@A(Throwable.class) Throwable", true)]
    public void ThrowableSpellingDeterminesEvidenceAndCaution(string type, bool qualified)
    {
        var catchLine = $"catch ({type} failure) {{ recover(failure); }}";
        var result = Analyze(Wrap($"try {{ work(); }}\n{catchLine}"));
        var finding = Assert.Single(result.Findings);
        var column = (qualified ? catchLine.IndexOf("java.", StringComparison.Ordinal)
            : catchLine.LastIndexOf("Throwable", StringComparison.Ordinal)) + 1;
        Assert.Equal(("JAVA-003", 2, column), (finding.RuleId, finding.Line, finding.Column));
        Assert.Contains(qualified ? "한정 이름 java.lang.Throwable" : "단순 이름 Throwable", finding.Description);
        Assert.Contains("표준 타입으로 해석되면", finding.Description);
        Assert.Contains(qualified ? "실제 타입 바인딩" : "같은 이름의 사용자 타입", finding.Caution);
        Assert.Contains(type, finding.Evidence);
    }

    [Theory]
    [InlineData("class Throwable extends Exception {}")]
    [InlineData("import custom.Throwable;")]
    public void SimpleThrowableRemainsAnAdvisoryWhenItCouldBeAUserType(string declaration)
    {
        var code = declaration + "\n" + Wrap("try { work(); } catch (Throwable failure) { recover(failure); }");
        var finding = Assert.Single(Analyze(code).Findings);
        Assert.Equal("JAVA-003", finding.RuleId);
        Assert.Contains("같은 이름의 사용자 타입", finding.Caution);
        Assert.Contains("표준 타입으로 해석되면", finding.Description);
    }

    [Theory]
    [InlineData("Exception")]
    [InlineData("custom./* namespace */@A(Throwable.class) Throwable")]
    [InlineData("Outer<T>.java.lang.Throwable")]
    [InlineData("outer.java.lang.Throwable")]
    public void OtherQualifiedNamesAndAnnotationArgumentsDoNotTriggerThrowable(string type)
    {
        var result = Analyze(Wrap($"try {{ work(); }} catch ({type} failure) {{ recover(failure); }}"));
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedParseDoesNotBlockLaterJavaCSharpOrPythonFiles(bool throws)
    {
        var java = new JavaAnalyzer(null, (parser, code) => code switch
        {
            "fail" when throws => throw new InvalidOperationException(),
            "fail" => null,
            _ => parser.Parse(code)
        });
        var analyzer = new AnalysisOrchestrator([java, new CSharpAnalyzer(), new PythonAnalyzer()]);
        var result = analyzer.Analyze(new("mixed",
            [
                new("Before.java", "class Before { int missing = 0 }"),
                new("Failed.java", "fail"),
                new("After.java", Wrap("try { work(); } catch (Exception e) {}")),
                new("Check.cs", "class Check { int M(bool b) => b ? 1 : 0; }"),
                new("check.py", "def route(a, b):\n    if a and b: return 1\n    return 0\n")
            ], []));

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(4, result.AnalyzedFileCount);
        Assert.Equal(new[] { ("Before.java", "JAVA-001"), ("After.java", "JAVA-002") },
            result.Findings.Select(finding => (finding.FileName, finding.RuleId)));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("Failed.java", issue.FileName);
        Assert.Contains(throws ? "InvalidOperationException" : "구문 트리를 생성하지 못했습니다", issue.Reason);
        Assert.Equal(new[] { (SourceLanguage.CSharp, 2), (SourceLanguage.Python, 3) },
            result.ControlFlow!.Select(method => (method.Language, method.Complexity!.Value)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SyntaxLimitOnlyReportsOmissionWhenMoreDiagnosticsExist(int extra)
    {
        var max = JavaAnalyzer.MaxSyntaxFindingsPerFile;
        var result = Analyze(MissingSemicolons(max + extra));
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal(Enumerable.Range(2, max), result.Findings.Select(finding => finding.Line));
        if (extra == 0)
            Assert.Empty(result.Issues);
        else
            Assert.Contains("추가 진단을 생략", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void BatchSyntaxLimitKeepsNonSyntaxRulesAvailable()
    {
        var perFile = JavaAnalyzer.MaxSyntaxFindingsPerFile;
        var perBatch = JavaAnalyzer.MaxSyntaxFindingsPerBatch;
        var files = perBatch / perFile;
        var code = MissingSemicolons(perFile);
        var documents = Enumerable.Range(1, files).Select(index => new SourceDocument($"Broken{index}.java", code))
            .Append(new("Omitted.java", MissingSemicolons(1)))
            .Append(new("Good.java", Wrap("try { work(); } catch (Exception e) {}")))
            .ToArray();

        var result = new AnalysisOrchestrator().Analyze(new("syntax limit", documents, []));
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(files + 2, result.AnalyzedFileCount);
        Assert.Equal(perBatch, result.Findings.Count(finding => finding.RuleId == "JAVA-001"));
        Assert.Equal("Omitted.java", Assert.Single(result.Issues).FileName);
        Assert.Equal("Good.java", Assert.Single(result.Findings, finding => finding.RuleId == "JAVA-002").FileName);
    }

    [Fact]
    public void ParserInitializationFailureLeavesOtherLanguagesAvailable()
    {
        var java = new JavaAnalyzer(() => throw new DllNotFoundException());
        var analyzer = new AnalysisOrchestrator([new CSharpAnalyzer(), java]);
        var source = new SourceDocument("Unavailable.java", "class Sample {}");
        var input = new SourceInput("mixed", [source, new("Check.cs", "class Check { int M() => 1; }")], []);
        var mixed = analyzer.Analyze(input);
        Assert.Equal(AnalysisStatus.Partial, mixed.Status);
        Assert.Equal(1, mixed.AnalyzedFileCount);
        Assert.Contains("DllNotFoundException", Assert.Single(mixed.Issues).Reason);
        Assert.Empty(mixed.Findings);
        Assert.Equal(SourceLanguage.CSharp, Assert.Single(mixed.ControlFlow!).Language);
        var failed = analyzer.Analyze(input with { Documents = [source] });
        Assert.Equal(AnalysisStatus.Failed, failed.Status);
        Assert.Equal(0, failed.AnalyzedFileCount);
        Assert.Empty(failed.ControlFlow!);
    }

    private static string Wrap(string body) => $"class Sample {{ void check() {{ {body} }} }}";

    private static string MissingSemicolons(int count) =>
        "class Broken {\n" + string.Concat(Enumerable.Repeat("    int value = 0\n", count)) + "}";

    private static LanguageAnalysis Analyze(string code) => new JavaAnalyzer().Analyze([new("Sample.java", code)]);
}
