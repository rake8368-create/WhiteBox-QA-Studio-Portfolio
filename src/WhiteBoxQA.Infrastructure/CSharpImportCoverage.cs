namespace WhiteBoxQA.Infrastructure;

// Keep omission details bounded independently of the number of entries in the input.
internal sealed class CSharpImportCoverage(StringComparer comparer)
{
    public OmittedPaths Projects { get; } = new(comparer);
    public OmittedPaths Sources { get; } = new(comparer);

    internal sealed class OmittedPaths(StringComparer comparer)
    {
        private readonly HashSet<string> paths = new(comparer);
        private int characters;

        public IEnumerable<string> Paths => paths;
        public bool HasUntrackedPaths { get; set; }

        public void Add(string path)
        {
            if (paths.Count == 2_000 || path.Length > 1_000_000 - characters)
            {
                HasUntrackedPaths = true;
                return;
            }

            if (paths.Add(path))
                characters += path.Length;
        }
    }
}
