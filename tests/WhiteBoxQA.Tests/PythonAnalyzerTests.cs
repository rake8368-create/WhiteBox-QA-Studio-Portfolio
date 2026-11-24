using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class PythonAnalyzerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("한글 = '😀'\nassert 한글")]
    [InlineData("if (\n    True\n): assert True\n")]
    [InlineData("x = 1; \\\n    y = 2\n")]
    [InlineData("if True: \\\n    pass\n")]
    [InlineData("async def fetch():\n    return await client.read()\n")]
    [InlineData("from private_package import something\n@something\ndef f():\n    return missing_name()\n")]
    [InlineData("match value:\n    case {'id': key}:\n        print(key)\n    case _:\n        pass\n")]
    [InlineData("type Pair[T] = tuple[T, T]\ndef first[T](pair: Pair[T]) -> T: return pair[0]\n")]
    [InlineData("if True:\n\tif True:\n\t\tpass\n\tsecond()\n")]
    [InlineData("# header\n\fif True:\n    x = 'a\u2028b'\n")]
    public void ValidSourceIncludingAsyncAndStubsProducesNoFinding(string code)
    {
        var result = Analyze(code);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Findings);
        Assert.All(result.ControlFlow!, method =>
        {
            Assert.Equal(MethodFlowStatus.Unavailable, method.Status);
            Assert.Contains(".pyi", method.Limitation);
            Assert.Null(method.Complexity);
        });
    }

    [Theory]
    [InlineData("x = (\n", 1)]
    [InlineData("x = 'broken\n", 1)]
    [InlineData("if x print(x)\n", 1)]
    [InlineData("def f():\nprint('missing indentation')\n", 1)]
    [InlineData("def f():\n    pass\n  misplaced()\n", 3)]
    [InlineData("if True:\n    pass\n        misplaced()\n", 3)]
    [InlineData("  unexpected()\n", 1)]
    [InlineData("try: work()\nexcept:\n    # comment alone\n", 2)]
    [InlineData("try: work()\n", 1)]
    [InlineData("try: work()\nexcept*: recover()\n", 2)]
    [InlineData("try: work()\nexcept: recover()\nexcept ValueError: handle()\n", 2)]
    [InlineData("try: work()\nexcept ValueError: recover()\nexcept* TypeError: handle()\n", 3)]
    public void SyntaxDiagnosticsAreDedicatedFindingsWithEvidence(string code, int line)
    {
        var result = Analyze(code);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("PY-001", finding.RuleId);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal("sample.pyi", finding.FileName);
        Assert.Equal(line, finding.Line);
        Assert.True(finding.Column > 0);
        Assert.NotEmpty(finding.Evidence);
        Assert.NotEmpty(finding.Recommendation);
    }

    [Fact]
    public void ReportsMissingTokenEvenWithoutAnErrorNode()
    {
        var result = Analyze("def f(x):\n    return f'foo{}'\n");
        var finding = Assert.Single(result.Findings);
        Assert.Equal(("PY-001", 2, 18), (finding.RuleId, finding.Line, finding.Column));
        Assert.Contains("identifier", finding.Description);
    }

    [Fact]
    public void SyntaxErrorsSuppressHandlerRulesForThatFile()
    {
        var result = Analyze("try: work()\nexcept: pass\nvalue = (");
        Assert.Equal("PY-001", Assert.Single(result.Findings).RuleId);
    }

    [Fact]
    public void LocationsUseUtf16ColumnsAndUniversalNewlines()
    {
        const string prefix = "\t메모 = '한글😀'; ";
        var result = Analyze("try: work()\r\nexcept Exception as error:\r" + prefix + "raise error\n");
        var finding = Assert.Single(result.Findings);
        Assert.Equal("PY-004", finding.RuleId);
        Assert.Equal(3, finding.Line);
        Assert.Equal(prefix.Length + 1, finding.Column);
        Assert.Contains("한글😀", finding.Evidence);
    }

    [Theory]
    [InlineData("except Exception: pass", "PY-002")]
    [InlineData("except Exception:\n    # intentional\n    pass", "PY-002")]
    [InlineData("except Exception: ...", "PY-002")]
    [InlineData("except Exception: (...); 'intentional'", "PY-002")]
    [InlineData("except Exception: f'{recover()}'", "")]
    [InlineData("except Exception: pass; recover()", "")]
    [InlineData("except Exception: recover()", "")]
    [InlineData("except BaseException: recover()", "")]
    [InlineData("except (ValueError, TypeError): recover()", "")]
    [InlineData("except: recover()", "PY-003")]
    [InlineData("except: pass", "PY-002,PY-003")]
    [InlineData("except* Exception: recover()", "")]
    public void EmptyAndBareExceptAreIndependentRules(string handler, string expected)
    {
        var result = Analyze("try: work()\n" + handler + "\n");
        Assert.Equal(expected, string.Join(',', result.Findings.Select(finding => finding.RuleId)));
    }

    [Theory]
    [InlineData("    raise error", true)]
    [InlineData("    raise (error)", true)]
    [InlineData("    log(error)\n    raise error", true)]
    [InlineData("    other = error\n    raise error", true)]
    [InlineData("    raise", false)]
    [InlineData("    raise other", false)]
    [InlineData("    raise error from None", false)]
    [InlineData("    raise error.with_traceback(trace)", false)]
    [InlineData("    error = ValueError()\n    raise error", false)]
    [InlineData("    if changed:\n        error = ValueError()\n    raise error", false)]
    [InlineData("    ｅrror = ValueError()\n    raise error", false)]
    [InlineData("    def later():\n        raise error", false)]
    [InlineData("    async def later():\n        raise error", false)]
    [InlineData("    def later(arg=(error := ValueError())):\n        pass\n    raise error", false)]
    [InlineData("    try: work()\n    except Other as other: raise error", false)]
    [InlineData("    try: work()\n    except Other as error: handle()\n    raise error", false)]
    [InlineData("    try: raise error\n    finally: cleanup()", false)]
    [InlineData("    record.error = replacement\n    raise error", true)]
    [InlineData("    data[error] = replacement\n    raise error", true)]
    [InlineData("    from error import tool\n    raise error", true)]
    [InlineData("    import error.module as another\n    raise error", true)]
    [InlineData("    from module import tool as error\n    raise error", false)]
    [InlineData("    import error.module\n    raise error", false)]
    [InlineData("    error, other = replacement\n    raise error", false)]
    [InlineData("    with context() as error: setup()\n    raise error", false)]
    [InlineData("    del error\n    raise error", false)]
    [InlineData("    del data[error]\n    raise error", true)]
    [InlineData("    def later():\n        error = RuntimeError()\n    raise error", true)]
    [InlineData("    def later():\n        nonlocal error\n        error = RuntimeError()\n    later()\n    raise error", false)]
    [InlineData("    try: work()\n    except Other as other: handle()\n    raise error", true)]
    [InlineData("    if flag:\n        raise error from cause\n    else:\n        raise other", false)]
    [InlineData("    if (error := changed()):\n        raise error\n    else:\n        raise error", false)]
    [InlineData("    if (False):\n        raise error\n    elif True:\n        handle()\n    else:\n        raise error", false)]
    public void RethrowRequiresAnUnchangedAliasInTheSameHandlerScope(string body, bool expected)
    {
        var result = Analyze("try: work()\nexcept Exception as error:\n" + body + "\n");
        if (expected)
        {
            var finding = Assert.Single(result.Findings);
            Assert.Equal("PY-004", finding.RuleId);
            Assert.Null(finding.ExceptionAssignment);
        }
        else
            Assert.Empty(result.Findings);
    }

    [Fact]
    public void AWriteInOneIfBranchDoesNotMaskTheRethrowInItsSibling()
    {
        var result = Analyze("""
            try: work()
            except Exception as error:
                if first:
                    error = replacement
                    raise error
                elif second:
                    raise error
                else:
                    raise error from cause
                raise error
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(("PY-004", 7, 9), (finding.RuleId, finding.Line, finding.Column));
    }

    [Fact]
    public void NestedHandlerCanReportItsOwnAlias()
    {
        var result = Analyze("""
            async def run():
                try:
                    await fetch()
                except RuntimeError as outer:
                    try:
                        await retry()
                    except ValueError as inner:
                        raise inner
            """);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(("PY-004", 8, 13), (finding.RuleId, finding.Line, finding.Column));
    }

    [Fact]
    public void NativeInitializationFailureProducesIssuesAndKeepsCSharpResults()
    {
        var python = new PythonAnalyzer(() => throw new DllNotFoundException());
        var analyzer = new AnalysisOrchestrator([new CSharpAnalyzer(), python]);

        var result = analyzer.Analyze(new("mixed",
            [new("Clean.cs", "class Clean {}"), new("bad.py", "x = (")], []));

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("bad.py", issue.FileName);
        Assert.Contains("Python 파서를 초기화", issue.Reason);
        Assert.Contains("DllNotFoundException", issue.Reason);
        Assert.Null(issue.UnsupportedLanguage);
        Assert.Equal(0, Assert.Single(result.LanguageInventory!, item => item.Language == SourceLanguage.Python).AnalyzedFileCount);
    }

    private static LanguageAnalysis Analyze(string code) =>
        new PythonAnalyzer().Analyze([new("sample.pyi", code)]);
}
