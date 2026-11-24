namespace WhiteBoxQA.Infrastructure;

internal sealed class CSharpImportPaths(string? root, bool gitSnapshot)
{
    public StringComparer Comparer { get; } = gitSnapshot || !OperatingSystem.IsWindows()
        ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public string? Normalize(string path)
    {
        // ls-tree supplies canonical, case-sensitive names, even on Windows.
        if (gitSnapshot)
            return path;
        try { return root is null ? Path.GetFullPath(path) : Path.GetFullPath(path, root); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    public string? Directory(string path)
    {
        if (!gitSnapshot)
            return Path.GetDirectoryName(path);
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[..slash] : path.Length == 0 ? null : "";
    }

    public string Relative(string directory, string path) => gitSnapshot
        ? directory.Length == 0 ? path : path[(directory.Length + 1)..]
        : Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');

    public string? Resolve(string directory, string value)
    {
        if (!gitSnapshot)
        {
            var path = Normalize(Path.Combine(directory,
                value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            return path is not null && Contains(root ?? directory, path) ? path : null;
        }

        var parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in value.Replace('\\', '/').Split('/'))
        {
            switch (part)
            {
                case "":
                case ".":
                    break;
                case "..":
                    if (parts.Count == 0)
                        return null;
                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }
        return string.Join('/', parts);
    }

    private static bool Contains(string boundary, string path)
    {
        var relative = Path.GetRelativePath(boundary, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
