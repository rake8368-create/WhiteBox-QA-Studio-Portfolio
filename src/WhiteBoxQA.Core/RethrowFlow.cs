using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace WhiteBoxQA.Core;

internal sealed class RethrowFlow(SemanticModel model)
{
    [Flags]
    private enum State { Unreached = 0, Original = 1, Assigned = 2, Unknown = 4 }

    private readonly Dictionary<SyntaxNode, ControlFlowGraph?> graphs = [];
    private readonly Dictionary<CatchClauseSyntax, IReadOnlyDictionary<int, State>> catches = [];

    public RethrowAssignment Analyze(CatchClauseSyntax clause, IdentifierNameSyntax thrown, ILocalSymbol variable)
    {
        if (!catches.TryGetValue(clause, out var paths))
        {
            paths = AnalyzeCatch(clause, variable);
            catches.Add(clause, paths);
        }

        return paths.GetValueOrDefault(thrown.SpanStart) switch
        {
            State.Original => RethrowAssignment.None,
            State.Assigned => RethrowAssignment.AllPaths,
            State.Original | State.Assigned => RethrowAssignment.SomePaths,
            _ => RethrowAssignment.Unknown
        };
    }

    private Dictionary<int, State> AnalyzeCatch(CatchClauseSyntax clause, ILocalSymbol variable)
    {
        if (clause.ContainsDiagnostics || GetGraph(clause) is not { } graph)
            return [];

        var region = FindRegion(graph.Root, variable);
        var body = model.AnalyzeDataFlow(clause.Block);
        var filter = clause.Filter is { FilterExpression: { } expression } ? model.AnalyzeDataFlow(expression) : null;
        if (region is null || body?.Succeeded != true || (clause.Filter is not null && filter?.Succeeded != true))
            return [];

        bool CapturedWrite(DataFlowAnalysis data) =>
            data.CapturedInside.Contains(variable, SymbolEqualityComparer.Default)
            && data.WrittenInside.Contains(variable, SymbolEqualityComparer.Default);
        var capturedWrite = CapturedWrite(body) || (filter is not null && CapturedWrite(filter));

        var blocks = graph.Blocks;
        var entry = new State[blocks.Length];
        var exit = new State[blocks.Length];
        var pending = new Queue<int>();
        var regionWrites = new Dictionary<ControlFlowRegion, bool>();
        void Merge(int ordinal, State state)
        {
            if (ordinal < region.FirstBlockOrdinal || ordinal > region.LastBlockOrdinal || !blocks[ordinal].IsReachable)
                return;
            var joined = entry[ordinal] | state;
            if (joined == entry[ordinal])
                return;
            entry[ordinal] = joined;
            pending.Enqueue(ordinal);
        }

        bool MayChangeInRegion(ControlFlowRegion candidate)
        {
            if (!regionWrites.TryGetValue(candidate, out var change))
            {
                change = blocks.Skip(candidate.FirstBlockOrdinal)
                    .Take(candidate.LastBlockOrdinal - candidate.FirstBlockOrdinal + 1)
                    .Any(block => block.IsReachable && Transfer(block, State.Original, variable, capturedWrite) != State.Original);
                regionWrites.Add(candidate, change);
            }
            return change;
        }

        Merge(region.FirstBlockOrdinal, State.Original);
        while (pending.TryDequeue(out var ordinal))
        {
            var block = blocks[ordinal];
            var before = entry[ordinal];
            var after = Transfer(block, before, variable, capturedWrite);
            exit[ordinal] = after;

            // Roslyn does not expose ordinary predecessor edges for exception dispatch.
            for (var nested = block.EnclosingRegion; nested != region; nested = nested.EnclosingRegion!)
            {
                if (nested is { Kind: ControlFlowRegionKind.Try, EnclosingRegion.Kind: ControlFlowRegionKind.TryAndCatch })
                {
                    var state = MayChangeInRegion(nested) ? Uncertain(before) : before;
                    foreach (var handler in nested.EnclosingRegion.NestedRegions.Skip(1))
                        Merge(handler.FirstBlockOrdinal, state);
                }
            }

            foreach (var branch in Successors(block, after, variable))
            {
                var state = after;
                foreach (var finallyRegion in branch.FinallyRegions)
                {
                    Merge(finallyRegion.FirstBlockOrdinal, Uncertain(state));
                    if (MayChangeInRegion(finallyRegion))
                        state = Uncertain(state);
                }
                if (branch.Destination is { } destination)
                    Merge(destination.Ordinal, state);
            }
        }

        var paths = new Dictionary<int, State>();
        for (var ordinal = region.FirstBlockOrdinal; ordinal <= region.LastBlockOrdinal; ordinal++)
        {
            var block = blocks[ordinal];
            if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Throw
                && block.BranchValue is { } thrown)
            {
                foreach (var reference in thrown.DescendantsAndSelf().OfType<ILocalReferenceOperation>())
                    if (IsVariable(reference, variable))
                        paths[reference.Syntax.SpanStart] = exit[ordinal];
            }
        }
        return paths;
    }

    private ControlFlowGraph? GetGraph(SyntaxNode node)
    {
        var owner = node.Ancestors().FirstOrDefault(ancestor =>
            ancestor is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or CompilationUnitSyntax
                or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        if (owner is null)
            return null;
        if (graphs.TryGetValue(owner, out var cached))
            return cached;
        try
        {
            cached = owner switch
            {
                LocalFunctionStatementSyntax local when model.GetDeclaredSymbol(local) is IMethodSymbol symbol =>
                    GetGraph(local)?.GetLocalFunctionControlFlowGraph(symbol),
                AnonymousFunctionExpressionSyntax => null,
                _ => ControlFlowGraph.Create(owner, model)
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            cached = null;
        }
        graphs.Add(owner, cached);
        return cached;
    }

    private static ControlFlowRegion? FindRegion(ControlFlowRegion region, ILocalSymbol variable)
    {
        if (region.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler
            && region.Locals.Contains(variable, SymbolEqualityComparer.Default))
            return region;
        foreach (var nested in region.NestedRegions)
            if (FindRegion(nested, variable) is { } match)
                return match;
        return null;
    }

    private IEnumerable<ControlFlowBranch> Successors(BasicBlock block, State state, ILocalSymbol variable)
    {
        bool? value = block.BranchValue?.ConstantValue is { HasValue: true, Value: bool constant } ? constant : null;
        // The original caught exception is non-null, including on the lowered null test for ??=.
        if (state == State.Original && block.BranchValue is { Kind: OperationKind.IsNull } nullTest
            && nullTest.ChildOperations.Any(operand => TargetsVariable(operand, variable)))
            value = false;
        bool? takeConditional = value is { } condition && block.ConditionKind != ControlFlowConditionKind.None
            ? condition == (block.ConditionKind == ControlFlowConditionKind.WhenTrue) : null;
        if (takeConditional != true && block.FallThroughSuccessor is { } next)
            yield return next;
        if (takeConditional != false && block.ConditionalSuccessor is { } conditional)
            yield return conditional;
    }

    private State Transfer(BasicBlock block, State state, ILocalSymbol variable, bool capturedWrite)
    {
        foreach (var operation in block.Operations)
            state = Transfer(operation, state, variable, capturedWrite);
        return block.BranchValue is { } branch ? Transfer(branch, state, variable, capturedWrite) : state;
    }

    private State Transfer(IOperation operation, State state, ILocalSymbol variable, bool capturedWrite)
    {
        if (operation is ILocalFunctionOperation or IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation)
            return state;
        foreach (var child in operation.ChildOperations)
            state = Transfer(child, state, variable, capturedWrite);

        return operation switch
        {
            ISimpleAssignmentOperation { IsImplicit: true, Syntax: CatchDeclarationSyntax } => state,
            ISimpleAssignmentOperation { IsRef: true } alias when alias.Value.DescendantsAndSelf()
                .OfType<ILocalReferenceOperation>().Any(reference => IsVariable(reference, variable)) =>
                Uncertain(state),
            IAssignmentOperation assignment when TargetsVariable(assignment.Target, variable) =>
                assignment.Value.DescendantsAndSelf().Any(child => child is IInvalidOperation)
                    ? Uncertain(state) : State.Assigned,
            IIncrementOrDecrementOperation change when TargetsVariable(change.Target, variable) => State.Assigned,
            IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument
                when TargetsVariable(argument.Value, variable) => Uncertain(state),
            IInvalidOperation or IDynamicInvocationOperation when operation.Descendants().OfType<ILocalReferenceOperation>()
                .Any(reference => IsVariable(reference, variable)) => Uncertain(state),
            IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction or MethodKind.DelegateInvoke }
                when capturedWrite => Uncertain(state),
            IArgumentOperation { Value.Type.TypeKind: TypeKind.Delegate } when capturedWrite => Uncertain(state),
            IAssignmentOperation { Value.Type.TypeKind: TypeKind.Delegate,
                Target: IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation }
                when capturedWrite => Uncertain(state),
            IEventAssignmentOperation or IDynamicInvocationOperation when capturedWrite =>
                Uncertain(state),
            _ => state
        };
    }

    private static State Uncertain(State state) =>
        (state & State.Assigned) | ((state & (State.Original | State.Unknown)) != 0 ? State.Unknown : State.Unreached);

    private bool TargetsVariable(IOperation target, ILocalSymbol variable) => target switch
    {
        ILocalReferenceOperation reference => IsVariable(reference, variable),
        ITupleOperation tuple => tuple.Elements.Any(element => TargetsVariable(element, variable)),
        IDeclarationExpressionOperation declaration => TargetsVariable(declaration.Expression, variable),
        IFlowCaptureReferenceOperation { Syntax: IdentifierNameSyntax identifier } =>
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, variable),
        _ => false
    };

    private static bool IsVariable(ILocalReferenceOperation reference, ILocalSymbol variable) =>
        SymbolEqualityComparer.Default.Equals(reference.Local, variable);
}
