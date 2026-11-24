using System.Windows;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--python-parser-smoke-test"])
        {
            var result = new AnalysisOrchestrator().Analyze(new("Python parser smoke test",
                [
                    new("handlers.py", """
                        try: work()
                        except Exception: pass
                        try: work()
                        except: recover()
                        try: work()
                        except Exception as error: raise error

                        def classify(value, enabled):
                            if value and enabled:
                                return 1
                            return 0

                        def skip():
                            if True or check():
                                return 1
                            return 0

                        async def fetch():
                            return await load()
                        """),
                    new("invalid.pyi", "data = (\n")
                ], []));
            var expected = new[] { "PY-001", "PY-002", "PY-003", "PY-004" };
            var actual = result.Findings.Select(finding => finding.RuleId).Order(StringComparer.Ordinal);
            Shutdown(result.Status == AnalysisStatus.Succeeded && result.AnalyzedFileCount == 2
                && expected.SequenceEqual(actual) && result.ControlFlow is { Count: 3 } methods
                && methods.Any(method => method is { MethodName: "classify", Complexity: 3, Language: SourceLanguage.Python }
                    && method.Decisions is [var left, var right] && left is { Condition: "value", IncludedInComplexity: true }
                    && right is { Condition: "enabled", IncludedInComplexity: true }
                    && left.TrueTarget == right.Block && left.FalseTarget == right.FalseTarget)
                && methods.Any(method => method is { MethodName: "skip", Complexity: 1 }
                    && method.Decisions is [{ IsConstant: true, FalseTarget: null }, { IsReachable: false }])
                && methods.Any(method => method is { MethodName: "fetch", Status: MethodFlowStatus.Unavailable, Complexity: null })
                ? 0 : 1);
            return;
        }

        if (e.Args is ["--java-parser-smoke-test"])
        {
            var result = new AnalysisOrchestrator().Analyze(new("Java parser smoke test",
                [
                    new("Check.java", """
                        class Check {
                            void run() {
                                try { work(); }
                                catch (Exception ignored) { /* intentionally empty */ }
                                try { work(); }
                                catch (java.lang.Throwable error) { handle(error); }
                            }
                        }
                        """),
                    new("Broken.java", "class Broken { int field = 1 }"),
                    new("Check.cs", "class Check { int Evaluate(bool ready) => ready ? 1 : 0; }")
                ], []));
            var expected = new[] { "JAVA-001", "JAVA-002", "JAVA-003" };
            var actual = result.Findings.Select(finding => finding.RuleId).Order(StringComparer.Ordinal);
            Shutdown(result.Status == AnalysisStatus.Succeeded && result.AnalyzedFileCount == 3
                && expected.SequenceEqual(actual)
                && result.ControlFlow is [{ Language: SourceLanguage.CSharp, Complexity: 2, Status: MethodFlowStatus.Analyzed }]
                ? 0 : 1);
            return;
        }

        if (e.Args is ["--javascript-parser-smoke-test"])
        {
            var result = new AnalysisOrchestrator().Analyze(new("JavaScript parser smoke test",
                [
                    new("broken.js", "const broken = ;\ntry { work(); } catch {}"),
                    new("module.mjs", "export async function run() { try { await work(); } catch { /* empty */ } }"),
                    new("module.cjs", "try { require('./dep.cjs'); } catch (error) { recover(error); }"),
                    new("View.jsx", "export const View = () => <><input /><span>{value}</span></>;")
                ], []));
            Shutdown(result.Status == AnalysisStatus.Partial && result.AnalyzedFileCount == 4
                && result.LanguageInventory is [{ Language: SourceLanguage.JavaScript, FileCount: 4, AnalyzedFileCount: 4 }]
                && result.Findings is
                [
                    { FileName: "broken.js", RuleId: "JS-001", Severity: Severity.High, Line: 1 },
                    { FileName: "module.mjs", RuleId: "JS-002", Severity: Severity.Low, Line: 1 }
                ]
                && result.Issues is [{ FileName: "broken.js" }]
                && result.ControlFlow is { Count: 0 }
                ? 0 : 1);
            return;
        }

        if (e.Args is ["--cpp-parser-smoke-test"])
        {
            var analyzer = new AnalysisOrchestrator();
            var result = analyzer.Analyze(new("C++ parser smoke test",
                [
                    new("valid.cpp", "template<class T> T pick(T left, T right) { return left < right ? left : right; }"),
                    new("broken.cc", "int broken = ;\nvoid run() { try {} catch (...) {} }"),
                    new("empty.hpp", "void run() { try { work(); } catch (const Error& error) { { ; } } }"),
                    new("handled.cxx", "void run() { try { work(); } catch (...) { recover(); } }"),
                    new("guarded.hxx", "#if 0\nvoid run() { try {} catch (...) {} }\n#endif\n")
                ], []));
            Shutdown(result.Status == AnalysisStatus.Partial && result.AnalyzedFileCount == 5 && result.HasCppSources
                && analyzer.GetCapabilities(SourceLanguage.Cpp) == AnalysisCapabilities.StaticRules
                && result.LanguageInventory is [{ Language: SourceLanguage.Cpp, FileCount: 5, AnalyzedFileCount: 5 }]
                && result.Findings is
                [
                    { FileName: "broken.cc", RuleId: "CPP-001", Severity: Severity.High, Line: 1 },
                    { FileName: "empty.hpp", RuleId: "CPP-002", Severity: Severity.Low, Line: 1 }
                ]
                && result.Issues is [{ FileName: "broken.cc", Reason: var syntax }, { FileName: "guarded.hxx", Reason: var guard }]
                && syntax.Contains("구문 오류", StringComparison.Ordinal) && guard.Contains("조건부 전처리", StringComparison.Ordinal)
                && result.ControlFlow is { Count: 0 }
                ? 0 : 1);
            return;
        }

        if (e.Args is ["--typescript-parser-smoke-test"])
        {
            var analyzer = new AnalysisOrchestrator();
            var typeScript = analyzer.Analyze(new("TypeScript parser smoke test",
                [
                    new("valid.mts", "export interface Box<T> { value: T }; export const id = <T>(value: T) => value;"),
                    new("broken.ts", "const broken: number = ;"),
                    new("handler.cts", "try {} catch {} try {} catch (error) { recover(error); }")
                ], []));
            var tsx = analyzer.Analyze(new("TSX parser smoke test",
                [
                    new("valid.tsx", "const id = <T,>(x: T) => x; export const View = () => <><span>{id(1)}</span></>;"),
                    new("broken.tsx", "const view = <div><span /></div;"),
                    new("handler.tsx", "try {} catch (error) { /* empty */ ; { ; } } try {} catch { recover(); }")
                ], []));
            Shutdown(analyzer.GetCapabilities(SourceLanguage.TypeScript) == AnalysisCapabilities.StaticRules
                && IsTypeScriptSmokeResult(typeScript, "broken.ts", "handler.cts")
                && IsTypeScriptSmokeResult(tsx, "broken.tsx", "handler.tsx") ? 0 : 1);
            return;
        }

        base.OnStartup(e);
    }

    private static bool IsTypeScriptSmokeResult(AnalysisResult result, string broken, string handler) =>
        result.Status == AnalysisStatus.Partial && result.AnalyzedFileCount == 3 && result.HasTypeScriptSources
        && result.LanguageInventory is [{ Language: SourceLanguage.TypeScript, FileCount: 3, AnalyzedFileCount: 3 }]
        && result.Findings is
        [
            { FileName: var syntaxFile, RuleId: "TS-001", Severity: Severity.High, Line: 1 },
            { FileName: var catchFile, RuleId: "TS-002", Severity: Severity.Low, Line: 1 }
        ]
        && syntaxFile == broken && catchFile == handler
        && result.Issues is [{ FileName: var issueFile }] && issueFile == broken
        && result.ControlFlow is { Count: 0 };
}
