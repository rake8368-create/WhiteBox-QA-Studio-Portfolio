using System.Text;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Infrastructure;

internal static class CSharpProjectGraph
{
    public static void Prepare(Dictionary<CSharpProjectNode, List<CSharpProjectNode>> edges,
        Action<string, string> addIssue, CancellationToken token)
    {
        // Tarjan SCC. The importer limits recursion to 128 project nodes.
        var index = new Dictionary<CSharpProjectNode, int>();
        var lowLink = new Dictionary<CSharpProjectNode, int>();
        var stack = new Stack<CSharpProjectNode>();
        var onStack = new HashSet<CSharpProjectNode>();
        var cycleEvidence = new Dictionary<CSharpProjectNode, string>();

        void FindComponent(CSharpProjectNode node)
        {
            token.ThrowIfCancellationRequested();
            lowLink[node] = index[node] = index.Count;
            stack.Push(node);
            onStack.Add(node);
            foreach (var dependency in edges[node].OrderBy(item => item.DisplayPath, StringComparer.Ordinal))
            {
                if (!index.ContainsKey(dependency))
                {
                    FindComponent(dependency);
                    lowLink[node] = Math.Min(lowLink[node], lowLink[dependency]);
                }
                else if (onStack.Contains(dependency))
                    lowLink[node] = Math.Min(lowLink[node], index[dependency]);
            }

            if (lowLink[node] != index[node])
                return;

            var component = new List<CSharpProjectNode>();
            CSharpProjectNode member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (member != node);

            if (component.Count > 1 || edges[node].Contains(node))
            {
                var evidence = DescribeCycle(component, edges);
                foreach (var cyclicNode in component)
                    cycleEvidence.Add(cyclicNode, evidence);
            }
        }

        foreach (var node in edges.Keys.OrderBy(item => item.DisplayPath, StringComparer.Ordinal))
            if (!index.ContainsKey(node))
                FindComponent(node);

        foreach (var (node, dependencies) in edges.OrderBy(item => item.Key.DisplayPath, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var usable = new List<CSharpProjectNode>();
            foreach (var dependency in dependencies.OrderBy(item => item.DisplayPath, StringComparer.Ordinal))
            {
                if (!cycleEvidence.TryGetValue(dependency, out var cycle))
                {
                    usable.Add(dependency);
                    continue;
                }

                node.HasLimitations = true;
                var reason = node == dependency ? "자기 참조" : "대상 프로젝트의 순환 참조";
                addIssue(node.DisplayPath, $"ProjectReference 연결 보류 · {dependency.DisplayPath} · {reason} · 순환 경로 예시: {cycle}");
            }
            node.Dependencies = usable.ToArray();
        }

        var visited = new HashSet<CSharpProjectNode>();
        var order = 0;
        void Order(CSharpProjectNode node)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(node))
                return;
            foreach (var dependency in node.Dependencies)
                Order(dependency);
            node.DependencyOrder = order++;
        }

        foreach (var node in edges.Keys.OrderBy(item => item.DisplayPath, StringComparer.Ordinal))
            Order(node);
    }
    // Show an actual path around one cycle in the SCC, not an arbitrary ordering
    // of component names. Bound the text because it is saved in JSON and Excel.
    private static string DescribeCycle(IReadOnlyList<CSharpProjectNode> component,
        IReadOnlyDictionary<CSharpProjectNode, List<CSharpProjectNode>> edges)
    {
        var members = component.ToHashSet();
        var start = component.OrderBy(item => item.DisplayPath, StringComparer.Ordinal).First();
        if (edges[start].Contains(start))
            return SummarizePath([start, start]);

        var predecessor = new Dictionary<CSharpProjectNode, CSharpProjectNode>();
        var queue = new Queue<CSharpProjectNode>();
        foreach (var neighbor in edges[start].Where(members.Contains)
            .OrderBy(item => item.DisplayPath, StringComparer.Ordinal))
            if (predecessor.TryAdd(neighbor, start))
                queue.Enqueue(neighbor);

        while (queue.TryDequeue(out var current))
        {
            foreach (var next in edges[current].Where(members.Contains)
                .OrderBy(item => item.DisplayPath, StringComparer.Ordinal))
            {
                if (next == start)
                {
                    var reversed = new List<CSharpProjectNode>();
                    for (var cursor = current; cursor != start; cursor = predecessor[cursor])
                        reversed.Add(cursor);
                    reversed.Reverse();
                    var cycle = new List<CSharpProjectNode> { start };
                    cycle.AddRange(reversed);
                    cycle.Add(start);
                    return SummarizePath(cycle);
                }

                if (predecessor.TryAdd(next, current))
                    queue.Enqueue(next);
            }
        }

        // Defensive fallback: every nontrivial SCC must contain a cycle.
        return $"순환 요소 {component.Count:N0}개 프로젝트 (경로 확인 불가)";
    }

    private static string SummarizePath(IReadOnlyList<CSharpProjectNode> nodes)
    {
        const int maxCharacters = 260;
        const int maxNodes = 12;
        var text = new StringBuilder();
        for (var index = 0; index < nodes.Count; index++)
        {
            if (index >= maxNodes)
            {
                text.Append(" → … (경로 일부)");
                break;
            }

            var separator = index == 0 ? "" : " → ";
            var label = nodes[index].DisplayPath;
            if (text.Length + separator.Length + label.Length > maxCharacters)
            {
                text.Append(separator);
                var remaining = maxCharacters - text.Length;
                if (remaining > 0)
                    text.Append(label.AsSpan(0, Math.Min(remaining, label.Length)));
                text.Append("… (경로 일부)");
                break;
            }

            text.Append(separator).Append(label);
        }

        return text.ToString();
    }

}
