using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WhiteBoxQA.Core;

public sealed class CSharpAnalyzer : ILanguageAnalyzer
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private readonly CSharpAnalysisContext context;

    public CSharpAnalyzer() : this(new()) { }

    public CSharpAnalyzer(CSharpAnalysisContext context) => this.context = context;

    public SourceLanguage Language => SourceLanguage.CSharp;
    public AnalysisCapabilities Capabilities => AnalysisCapabilities.StaticRules | AnalysisCapabilities.ControlFlow;

    public AnalysisResult Analyze(SourceInput input) => new AnalysisOrchestrator([this]).Analyze(input);

    LanguageAnalysis ILanguageAnalyzer.Analyze(IReadOnlyList<SourceDocument> documents)
    {
        var findings = new List<Finding>();
        var flow = new List<MethodFlow>();
        var issues = new List<AnalysisIssue>();
        var analyzed = 0;

        foreach (var compilation in PrepareCompilations(documents, issues))
            analyzed += AnalyzeProject(compilation, findings, flow, issues);

        return new(analyzed, findings, issues) { ControlFlow = flow };
    }

    internal IReadOnlyList<CSharpCompilation> PrepareCompilations(IReadOnlyList<SourceDocument> documents,
        List<AnalysisIssue> issues)
    {
        var groups = documents.GroupBy(document => document.CSharpProject)
            .OrderBy(group => group.Key?.Node?.DependencyOrder ?? -1).ToArray();
        var targets = groups.SelectMany(group => group.Key?.Node?.Dependencies ?? []).ToHashSet();
        var prepared = new Dictionary<CSharpProjectNode, PreparedReference>();
        var compilations = new List<CSharpCompilation>(groups.Length);
        using var validationDeadline = new CancellationTokenSource();
        if (targets.Count > 0)
            validationDeadline.CancelAfter(TimeSpan.FromSeconds(20));

        foreach (var group in groups)
        {
            var node = group.Key?.Node;
            var issueCount = issues.Count;
            var trees = ParseDocuments(group, group.Key?.Settings, issues);
            if (trees.Count == 0)
                continue;
            var incomplete = node?.HasLimitations == true || issues.Count != issueCount;
            var references = new HashSet<CompilationReference>();
            foreach (var dependency in node?.Dependencies ?? [])
            {
                if (!prepared.TryGetValue(dependency, out var target))
                {
                    incomplete = true;
                    issues.Add(new(node!.DisplayPath,
                        $"ProjectReference 연결 보류 · {dependency.DisplayPath} · 확보한 컴파일 대상 소스 없음"));
                    continue;
                }

                references.Add(target.Reference);
                // A consumer can use C's types directly, or in B's public signatures, when A -> B -> C.
                references.UnionWith(target.Dependencies);
                incomplete |= target.Incomplete;
                var status = target.Incomplete ? "부분 연결 · 대상 또는 종속 프로젝트의 분석 문맥이 불완전합니다."
                    : "분석 문맥에 연결됨";
                issues.Add(new(node!.DisplayPath, $"ProjectReference {status} · {dependency.DisplayPath}")
                    { IsInformational = !target.Incomplete });
            }

            var settings = group.Key?.Settings;
            var metadata = FlowRuntimeReferences.All.Concat(context.AdditionalReferences)
                .Concat(settings?.AdditionalReferences ?? []).Concat(references).Distinct();
            var compilation = CSharpCompilation.Create($"WhiteBoxQA.Source.{compilations.Count}", trees, metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            compilations.Add(compilation);
            if (node is null || !targets.Contains(node))
                continue;

            var limitation = GetReferenceLimitation(compilation, validationDeadline.Token);
            if (limitation is not null)
            {
                incomplete = true;
                issues.Add(new(node.DisplayPath, limitation));
            }
            prepared.Add(node, new(compilation.ToMetadataReference(), references, incomplete));
        }

        return compilations;
    }

    private static string? GetReferenceLimitation(CSharpCompilation compilation, CancellationToken cancellationToken)
    {
        try
        {
            var error = compilation.GetDiagnostics(cancellationToken)
                .FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            return error is null ? null : $"참조 대상 프로젝트의 C# 구문, 형식 또는 종속성을 완전히 확인할 수 없습니다 ({error.Id}).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "확인 시간 한도로 참조 대상 프로젝트의 C# 진단을 완료하지 못했습니다.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            return $"참조 대상 프로젝트의 C# 진단을 완료하지 못했습니다. 오류 유형 {exception.GetType().Name}";
        }
    }

    private sealed record PreparedReference(CompilationReference Reference, IReadOnlySet<CompilationReference> Dependencies,
        bool Incomplete);

    private List<SyntaxTree> ParseDocuments(IEnumerable<SourceDocument> documents, CSharpAnalysisContext? project,
        List<AnalysisIssue> issues)
    {
        var parseOptions = new CSharpParseOptions(project?.LanguageVersion ?? context.LanguageVersion ?? LanguageVersion.CSharp14,
            preprocessorSymbols: context.PreprocessorSymbols.Concat(project?.PreprocessorSymbols ?? []).Distinct(StringComparer.Ordinal));
        var trees = new List<SyntaxTree>();
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.Code))
            {
                issues.Add(new(document.FileName, "분석할 C# 코드가 비어 있습니다."));
                continue;
            }

            try
            {
                trees.Add(CSharpSyntaxTree.ParseText(document.Code, parseOptions, document.FileName));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            {
                issues.Add(new(document.FileName, $"Roslyn이 파일을 분석하지 못했습니다. 오류 유형 {exception.GetType().Name}"));
            }
        }

        return trees;
    }

    private static int AnalyzeProject(CSharpCompilation compilation, List<Finding> findings, List<MethodFlow> flow,
        List<AnalysisIssue> issues)
    {
        var analyzed = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            try
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);
                AddSyntaxFindings(tree, root, findings);
                analyzed++;
                AddRethrowFindings(model, root, findings);
                flow.AddRange(CSharpControlFlow.Analyze(model, root));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            {
                issues.Add(new(tree.FilePath, $"Roslyn 분석 일부를 완료하지 못했습니다. 오류 유형 {exception.GetType().Name}"));
            }
        }

        return analyzed;
    }

    private static void AddSyntaxFindings(SyntaxTree tree, SyntaxNode root, List<Finding> findings)
    {
        foreach (var diagnostic in tree.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error))
        {
            findings.Add(CreateFinding(
                "WB-003", Severity.High, $"C# 구문 오류 ({diagnostic.Id})", diagnostic.Location,
                diagnostic.GetMessage(Korean), $"Roslyn C# 구문 진단 {diagnostic.Id}",
                "표시된 위치와 앞뒤 줄의 괄호, 세미콜론, 선언을 확인한 뒤 다시 분석하세요."));
        }

        foreach (var clause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (!clause.ContainsDiagnostics && IsNoOp(clause.Block))
            {
                findings.Add(CreateFinding(
                    "WB-001", Severity.Moderate, "아무 처리도 하지 않는 catch", clause.CatchKeyword.GetLocation(),
                    "예외가 발생해도 catch 내부에서 실행되는 처리가 없어 실패 원인을 놓칠 수 있습니다.",
                    "Roslyn이 확인한 catch 블록에는 주석, 빈 문장 또는 빈 중첩 블록 외에 처리 코드가 없습니다.",
                    "예외 정보를 기록하거나 복구를 시도하세요. 호출 측에서 처리해야 한다면 throw;를 사용하세요.",
                    "예외를 의도적으로 무시하거나 catch 필터에서 별도 처리를 하는 경우에는 결함이 아닐 수 있습니다. 필터와 처리 의도를 확인하세요."));
            }
        }
    }

    private static bool IsNoOp(StatementSyntax statement) => statement switch
    {
        EmptyStatementSyntax => true,
        BlockSyntax block => block.Statements.All(IsNoOp),
        _ => false
    };

    private static void AddRethrowFindings(SemanticModel model, SyntaxNode root, List<Finding> findings)
    {
        var paths = new RethrowFlow(model);
        foreach (var statement in root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            var expression = statement.Expression;
            while (expression is ParenthesizedExpressionSyntax parentheses)
                expression = parentheses.Expression;

            if (expression is not IdentifierNameSyntax identifier || statement.ContainsDiagnostics)
                continue;

            var boundary = statement.Ancestors().FirstOrDefault(node =>
                node is CatchClauseSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);

            if (boundary is not CatchClauseSyntax { Declaration: { } declaration } clause)
                continue;

            var caught = model.GetDeclaredSymbol(declaration);
            var thrown = model.GetSymbolInfo(identifier).Symbol;
            if (caught is not ILocalSymbol variable || !SymbolEqualityComparer.Default.Equals(variable, thrown))
                continue;

            var assignment = paths.Analyze(clause, identifier, variable);
            var caution = assignment switch
            {
                RethrowAssignment.AllPaths => "이 throw에 도달하는 CFG의 모든 경로에 변수에 쓰는 코드가 있습니다. 경로별 확인 결과 대입이 선행합니다. 같은 예외 객체를 넣었는지는 따로 확인하세요.",
                RethrowAssignment.SomePaths => "이 throw에 도달하는 일부 경로에만 변수에 쓰는 코드가 있습니다. 어느 경로에서 원래 예외가 유지되는지 경로별 확인이 필요합니다.",
                RethrowAssignment.None => "이 throw 이전에 도달 가능한 직접 쓰기는 탐지되지 않았습니다. 뒤에 있거나 이 지점에 도달하지 않는 쓰기는 제외했습니다.",
                _ => "이 throw에 도달하는 재대입 여부를 확정할 수 없습니다. 도달 가능 여부, 예외 처리 전이, 지역 함수 호출 또는 ref를 통한 변경을 확인하세요."
            };

            findings.Add(CreateFinding(
                "WB-002", Severity.Low, "catch 예외 변수를 다시 던짐", statement.ThrowKeyword.GetLocation(),
                "throw ex;는 변수에 들어 있는 객체를 이 줄에서 던져 원래 스택 추적 일부를 잃을 수 있습니다. throw;는 현재 catch가 잡은 원래 예외와 그 스택 추적을 유지합니다.",
                "던진 식과 catch 선언이 같은 Roslyn 심볼을 가리킵니다. 필터 진입부터 이 throw까지 제어 흐름과 변수 쓰기를 확인합니다.",
                assignment == RethrowAssignment.None
                    ? "원래 잡힌 예외를 그대로 다시 던지는 의도라면 throw;를 사용하세요."
                    : "현재 변수의 예외와 원래 잡힌 예외 중 어느 쪽을 던질지 먼저 확인하세요. 원래 예외를 유지할 때만 throw;를 검토하세요. 교체한 예외가 필요하면 그 의도를 코드에 드러내세요.",
                caution) with { ExceptionAssignment = assignment });
        }
    }

    private static Finding CreateFinding(
        string ruleId, Severity severity, string title, Location location,
        string description, string evidence, string recommendation, string? caution = null)
    {
        var span = location.GetLineSpan();
        return new(ruleId, severity, title, span.Path, span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1, description, evidence, recommendation, caution);
    }
}
