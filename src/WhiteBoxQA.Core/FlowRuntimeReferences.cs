using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace WhiteBoxQA.Core;

internal static class FlowRuntimeReferences
{
    // The loaded assemblies own the metadata for the lifetime of these references.
    private static readonly Assembly[] Assemblies =
    [
        typeof(object).Assembly, Assembly.Load("System.Runtime"),
        typeof(Console).Assembly, Assembly.Load("System.Collections"),
        typeof(Enumerable).Assembly
    ];

    public static IReadOnlyList<MetadataReference> All { get; } = Create();

    private static unsafe MetadataReference[] Create()
    {
        var references = new List<MetadataReference>();
        foreach (var assembly in Assemblies)
        {
            if (assembly.TryGetRawMetadata(out var pointer, out var length))
            {
                var module = ModuleMetadata.CreateFromMetadata((nint)pointer, length);
                references.Add(AssemblyMetadata.Create(module).GetReference(display: assembly.GetName().Name));
            }
        }

        return references.ToArray();
    }
}
