namespace WhiteBoxQA.Core;

// Populated once during import. A node is interned by its normalized input path.
internal sealed class CSharpProjectNode(string displayPath)
{
    public string DisplayPath { get; } = displayPath;
    public IReadOnlyList<CSharpProjectNode> Dependencies { get; set; } = [];
    public int DependencyOrder { get; set; }
    public bool HasLimitations { get; set; }
}
