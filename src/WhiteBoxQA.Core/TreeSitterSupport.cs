using System.ComponentModel;
using System.Runtime.InteropServices;
using TreeSitter;

namespace WhiteBoxQA.Core;

internal static class TreeSitterSupport
{
    internal static Language LoadLanguage(string name)
    {
        // Load via .NET first so the binding's native loader can also find the
        // modules already loaded from a single-file bundle's extraction directory.
        var grammarName = name.ToLowerInvariant();
        var assembly = typeof(Parser).Assembly;
        const DllImportSearchPath search = DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories;
        var runtime = NativeLibrary.Load("tree-sitter", assembly, search);
        var grammar = IntPtr.Zero;
        try
        {
            grammar = NativeLibrary.Load($"tree-sitter-{grammarName}", assembly, search);
            NativeLibrary.GetExport(runtime, "ts_current_free");
            NativeLibrary.GetExport(grammar, $"tree_sitter_{grammarName}");
            return new Language(name);
        }
        catch
        {
            if (grammar != IntPtr.Zero)
                NativeLibrary.Free(grammar);
            NativeLibrary.Free(runtime);
            throw;
        }
    }

    internal static bool IsNativeFailure(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or Win32Exception or PlatformNotSupportedException or TypeInitializationException;

    internal static string FailureName(Exception exception) =>
        exception is TypeInitializationException { InnerException: { } inner } ? inner.GetType().Name : exception.GetType().Name;

    internal static Finding CreateFinding(string fileName, string code, Node node, string id, Severity severity,
        string title, string description, string recommendation, string? caution = null)
    {
        var position = node.StartPosition;
        var index = node.StartIndex;
        // Evidence only uses a small window, even on very long source lines.
        var evidenceStart = Math.Max(0, index - 40);
        if (evidenceStart < index)
            evidenceStart = Math.Max(evidenceStart, code.LastIndexOf('\n', index - 1, index - evidenceStart) + 1);
        var evidenceEnd = Math.Min(code.Length, evidenceStart + 160);
        var lineEnd = code.IndexOf('\n', index, evidenceEnd - index);
        if (lineEnd >= 0)
            evidenceEnd = lineEnd;
        if (evidenceStart > 0 && evidenceStart < code.Length && char.IsLowSurrogate(code[evidenceStart]))
            evidenceStart--;
        if (evidenceEnd < code.Length && evidenceEnd > 0 && char.IsLowSurrogate(code[evidenceEnd]))
            evidenceEnd++;
        var evidence = code[evidenceStart..evidenceEnd].Trim();
        if (evidence.Length == 0)
            evidence = node.IsMissing ? $"누락된 토큰 · {node.Type}" : "줄 끝 또는 파일 끝";

        return new(id, severity, title, fileName, position.Row + 1, position.Column + 1, description,
            evidence, recommendation, caution);
    }

    internal static IEnumerable<Node> Descendants(Node root, Func<Node, bool>? descend = null)
    {
        using var cursor = root.Walk();
        while (true)
        {
            var node = cursor.CurrentNode;
            yield return node;
            if ((descend is null || descend(node)) && cursor.GotoFirstChild())
                continue;
            while (!cursor.GotoNextSibling())
                if (!cursor.GotoParent())
                    yield break;
        }
    }

    internal static IEnumerable<Node> NamedChildren(Node root)
    {
        using var cursor = root.Walk();
        if (!cursor.GotoFirstChild())
            yield break;
        do
        {
            var node = cursor.CurrentNode;
            if (node.IsNamed)
                yield return node;
        } while (cursor.GotoNextSibling());
    }

    internal static int LineStart(string text, int index) => index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
}
