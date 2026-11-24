using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Infrastructure;

internal static class CSharpProjectReader
{
    internal const int MaxProjects = 128;
    internal const int MaxProjectBytes = 256 * 1024;
    private const int MaxProjectReferences = 256;
    private const int MaxProjectNotes = 32;
    private const int MaxReferences = 16;
    private const int MaxReferenceBytes = 8 * 1024 * 1024;
    private const int MaxTotalReferenceBytes = 32 * 1024 * 1024;

    internal sealed record ProjectSource(string FileName, string? Xml, bool ExceedsByteLimit = false);

    public static bool IsProject(string path) => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);

    public static ProjectSource ReadLocal(string path, string displayName, List<AnalysisIssue> issues)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException();
            return new(displayName, SourceImporter.DecodeSource(ReadLimited(path, MaxProjectBytes)));
        }
        catch (FileSizeLimitException)
        {
            issues.Add(new(displayName, "프로젝트 설정이 개별 256 KiB 제한을 넘어 건너뛰었습니다."));
            return new(displayName, null, ExceedsByteLimit: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            issues.Add(new(displayName, "프로젝트 설정을 읽을 수 없습니다. 링크, 인코딩, 권한 또는 256 KiB 제한을 확인하세요."));
            return new(displayName, null);
        }
    }

    public static void Attach(string? root, List<SourceDocument> documents, IEnumerable<ProjectSource> projectSources,
        Action<string, string> addIssue, bool useLocalReferences = true, string? singleFile = null,
        CancellationToken cancellationToken = default, CSharpImportCoverage? coverage = null, string? inputLabel = null)
    {
        var paths = new CSharpImportPaths(root, gitSnapshot: !useLocalReferences);
        var projects = new Dictionary<string, Project>(paths.Comparer);
        var projectsByDirectory = new Dictionary<string, List<Project>>(paths.Comparer);
        var explicitProjects = new Dictionary<string, List<Project>>(paths.Comparer);
        var referenceBudget = MaxTotalReferenceBytes;
        foreach (var source in projectSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.Normalize(source.FileName) is not { } path || paths.Directory(path) is not { } directory)
            {
                addIssue(source.FileName, "프로젝트 경로를 해석할 수 없습니다.");
                continue;
            }
            if (projects.ContainsKey(path))
                continue;
            var project = Parse(source, directory, root, paths, addIssue, useLocalReferences, ref referenceBudget);
            projects.Add(path, project);
            AddProject(projectsByDirectory, directory, project);
            foreach (var include in project.Includes)
                if (!project.Removes.Contains(include))
                    AddProject(explicitProjects, include, project);
        }

        if (projects.Count == 0)
            return;

        var omitted = coverage?.Projects.Paths.Select(paths.Normalize).OfType<string>()
            .ToHashSet(paths.Comparer) ?? new(paths.Comparer);
        var omittedDirectories = omitted.Select(paths.Directory).OfType<string>().ToHashSet(paths.Comparer);
        HashSet<Project> FindOwners(string path)
        {
            var owners = new HashSet<Project>();
            if (explicitProjects.TryGetValue(path, out var explicitOwners))
                owners.UnionWith(explicitOwners);
            // The nearest project directory is a boundary even when its default items are disabled.
            for (var folder = paths.Directory(path); folder is not null; folder = paths.Directory(folder))
            {
                if (omittedDirectories.Contains(folder))
                    break;
                if (!projectsByDirectory.TryGetValue(folder, out var nearest))
                    continue;
                if (!SourceImporter.IsExcludedRepositoryPath(paths.Relative(folder, path)))
                    owners.UnionWith(nearest.Where(project => project.DefaultItems && !project.Removes.Contains(path)));
                break;
            }
            return owners;
        }

        var ambiguous = 0;
        var available = new HashSet<string>(paths.Comparer);
        for (var index = 0; index < documents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents[index];
            if (LanguageIdentification.Identify(document.FileName) != SourceLanguage.CSharp)
                continue;
            if (paths.Normalize(singleFile ?? document.FileName) is not { } path)
                continue;

            available.Add(path);
            var owners = FindOwners(path);
            if (owners.Count == 1)
                documents[index] = document with { CSharpProject = owners.Single().Context };
            else if (owners.Count > 1)
            {
                ambiguous++;
                foreach (var owner in owners)
                    owner.Node.HasLimitations = true;
            }
        }

        if (ambiguous > 0)
            addIssue(inputLabel ?? root ?? "로컬 파일", $"프로젝트를 하나로 특정할 수 없는 C# 파일 {ambiguous:N0}개를 프로젝트 설정 없이 분석합니다.");

        if (root is not null || !useLocalReferences)
        {
            var incomplete = new HashSet<Project>();
            foreach (var (path, owners) in explicitProjects)
                if (!available.Contains(path))
                    incomplete.UnionWith(owners);
            if (coverage is not null)
            {
                foreach (var unavailable in coverage.Sources.Paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (paths.Normalize(unavailable) is { } path)
                        incomplete.UnionWith(FindOwners(path));
                }
                if (coverage.Sources.HasUntrackedPaths)
                    incomplete.UnionWith(projects.Values);
            }
            foreach (var project in incomplete)
            {
                project.Node.HasLimitations = true;
                addIssue(project.Node.DisplayPath, "프로젝트의 컴파일 대상 소스 일부를 확보하지 못했거나 가져오기 범위를 확인할 수 없습니다.");
            }
        }

        var edges = new Dictionary<CSharpProjectNode, List<CSharpProjectNode>>();
        foreach (var project in projects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependencies = new List<CSharpProjectNode>();
            edges.Add(project.Node, dependencies);
            foreach (var (path, include) in project.ReferencePaths)
            {
                var target = projects.GetValueOrDefault(path);
                if (target is { XmlAvailable: true })
                {
                    dependencies.Add(target.Node);
                    continue;
                }

                var reason = target is { ExceedsByteLimit: true } ? "프로젝트 가져오기 용량 한도로 생략됨"
                    : target is not null ? "대상 프로젝트 XML을 읽거나 해석하지 못함"
                    : omitted.Contains(path) ? "프로젝트 가져오기 개수 한도로 생략됨"
                    : coverage?.Projects.HasUntrackedPaths == true ? "대상 파일 누락 또는 가져오기에서 생략됨"
                    : "가져온 프로젝트 파일에서 대상을 찾을 수 없음";
                project.Node.HasLimitations = true;
                addIssue(project.Node.DisplayPath, $"ProjectReference 연결 보류 · {include} · {reason}");
            }
        }
        CSharpProjectGraph.Prepare(edges, addIssue, cancellationToken);
    }

    private static void AddProject(Dictionary<string, List<Project>> index, string path, Project project)
    {
        if (!index.TryGetValue(path, out var items))
            index.Add(path, items = []);
        items.Add(project);
    }

    private static Project Parse(ProjectSource source, string directory, string? root, CSharpImportPaths paths,
        Action<string, string> addIssue, bool useLocalReferences, ref int referenceBudget)
    {
        var node = new CSharpProjectNode(source.FileName);
        var includes = new HashSet<string>(paths.Comparer);
        var removes = new HashSet<string>(paths.Comparer);
        var projectReferences = new Dictionary<string, string>(paths.Comparer);
        var references = new List<MetadataReference>();
        var readReferences = new HashSet<string>(paths.Comparer);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        void Note(string reason)
        {
            node.HasLimitations = true;
            if (reported.Contains(reason))
                return;
            if (reported.Count < MaxProjectNotes && reported.Add(reason))
                addIssue(source.FileName, reason);
            else if (reported.Count == MaxProjectNotes &&
                reported.Add("추가 프로젝트 설정 안내를 표시 한도로 생략했습니다."))
                addIssue(source.FileName, "추가 프로젝트 설정 안내를 표시 한도로 생략했습니다.");
        }

        var symbols = Array.Empty<string>();
        LanguageVersion? version = null;
        var defaultItems = false;
        var xmlAvailable = false;
        if (source.Xml is { } xml)
        {
            try
            {
                using var text = new StringReader(xml);
                using var reader = XmlReader.Create(text, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxProjectBytes
                });
                var project = XDocument.Load(reader).Root;
                if (project?.Name.LocalName != "Project")
                    throw new XmlException();

                var ns = project.Name.Namespace;
                var sdk = (string?)project.Attribute("Sdk");
                var sdkDefaults = sdk == "Microsoft.NET.Sdk" ||
                    (sdk?.StartsWith("Microsoft.NET.Sdk.", StringComparison.Ordinal) ?? false);
                var enableItems = true;
                var enableCompile = true;

                foreach (var element in project.Elements())
                {
                    if (element.Name == ns + "PropertyGroup")
                    {
                        foreach (var property in element.Elements())
                        {
                            var name = property.Name.LocalName;
                            if (name is not ("DefineConstants" or "LangVersion" or "EnableDefaultItems"
                                or "EnableDefaultCompileItems" or "DefaultItemExcludes" or "DefaultExcludesInProjectFolder"
                                or "ImplicitUsings" or "TargetFramework" or "TargetFrameworks"))
                                continue;
                            var value = property.Value.Trim();
                            if (IsConditional(element, property) || IsDynamic(value) || property.HasElements)
                            {
                                Note("조건부 또는 동적 프로젝트 설정은 적용하지 않았습니다. 무조건부 고정 값만 분석에 사용합니다.");
                                continue;
                            }

                            switch (name)
                            {
                                case "DefineConstants":
                                    var names = Split(value);
                                    if (names.All(SyntaxFacts.IsValidIdentifier))
                                        symbols = names.Distinct(StringComparer.Ordinal).ToArray();
                                    else
                                        Note("DefineConstants에 해석할 수 없는 기호가 있습니다.");
                                    break;
                                case "LangVersion":
                                    if (value.Length > 0 && char.IsAsciiDigit(value[0])
                                        && LanguageVersionFacts.TryParse(value, out var lang) && lang <= LanguageVersion.CSharp14)
                                        version = lang;
                                    else
                                        Note("고정 C# 언어 버전을 해석할 수 없습니다. 적용 가능한 명시 값 또는 도구 기본값으로 분석합니다.");
                                    break;
                                case "EnableDefaultItems":
                                    if (bool.TryParse(value, out var items))
                                        enableItems = items;
                                    else
                                        Note("EnableDefaultItems 값을 해석할 수 없습니다.");
                                    break;
                                case "EnableDefaultCompileItems":
                                    if (bool.TryParse(value, out var compile))
                                        enableCompile = compile;
                                    else
                                        Note("EnableDefaultCompileItems 값을 해석할 수 없습니다.");
                                    break;
                                case "ImplicitUsings" when value is "enable" or "true":
                                    Note("ImplicitUsings에서 생성되는 using은 분석 문맥에 포함되지 않습니다.");
                                    break;
                                case "TargetFrameworks" when value.Contains(';'):
                                    Note("다중 대상 프레임워크의 빌드별 분석 문맥은 지원하지 않습니다.");
                                    break;
                                case "DefaultItemExcludes" or "DefaultExcludesInProjectFolder":
                                    Note("사용자 지정 기본 파일 제외 설정은 해석 범위 밖입니다.");
                                    break;
                            }
                        }
                    }
                    else if (element.Name == ns + "ItemGroup")
                    {
                        foreach (var item in element.Elements())
                        {
                            var kind = item.Name.LocalName;
                            if (kind is not ("Compile" or "ProjectReference" or "Reference"
                                or "PackageReference" or "FrameworkReference"))
                                continue;
                            if (item.Name == ns + "ProjectReference")
                            {
                                ReadProjectReference(project, element, item, directory, paths,
                                    root is not null || !useLocalReferences, projectReferences, Note);
                                continue;
                            }
                            if (IsConditional(element, item))
                            {
                                Note("조건부 파일 연결 또는 참조 선언은 적용하지 않았습니다.");
                                continue;
                            }

                            if (kind == "Compile")
                            {
                                if (item.Attribute("Exclude") is not null || item.Attribute("Update") is not null)
                                    Note("Compile Exclude 및 Update는 해석 범위 밖입니다.");
                                else
                                {
                                    ReadPaths(item, "Include", includes, removes, directory, paths, Note);
                                    ReadPaths(item, "Remove", removes, includes, directory, paths, Note);
                                }
                            }
                            else if (kind == "Reference" && useLocalReferences && item.Element(ns + "HintPath") is { } hint
                                && hint.Attribute("Condition") is null
                                && ResolveLiteral(paths, directory, hint.Value.Trim()) is { } referencePath
                                && Path.GetExtension(referencePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!readReferences.Add(referencePath))
                                    continue;
                                if (readReferences.Count > MaxReferences || !TryReadReference(root ?? directory, referencePath, references, ref referenceBudget))
                                    Note("로컬 HintPath 참조를 읽지 못했습니다. 범위, 파일 상태 또는 참조 용량 제한을 확인하세요.");
                            }
                            else
                            {
                                Note($"{kind} 참조를 확인하지 못했습니다. 프로젝트 평가, 패키지 복원 및 자동 참조 연결은 수행하지 않습니다.");
                            }
                        }
                    }
                    else if (element.Name.LocalName is "Import" or "ImportGroup" or "Choose" or "Sdk")
                    {
                        Note("가져온 설정과 조건 선택은 평가하지 않았습니다. 프로젝트 파일에 직접 선언된 설정만 확인했습니다.");
                    }
                }

                if (project.Descendants(ns + "ProjectReference").Any(item =>
                    item.Parent?.Name != ns + "ItemGroup" || item.Parent.Parent != project))
                    Note("중첩된 MSBuild 구문의 ProjectReference는 지원하지 않습니다.");
                if (project.Descendants(ns + "ProjectReference").Any(item =>
                    item.Attribute("Remove") is not null || item.Attribute("Update") is not null))
                {
                    projectReferences.Clear();
                    Note("ProjectReference Remove 또는 Update가 있어 이 프로젝트의 참조 연결을 보류했습니다.");
                }

                defaultItems = sdkDefaults && enableItems && enableCompile;
                xmlAvailable = true;
            }
            catch (XmlException)
            {
                Note("프로젝트 XML이 손상되었거나 지원하지 않는 설정 형식입니다.");
            }
        }

        var context = new CSharpAnalysisContext
        {
            PreprocessorSymbols = symbols,
            LanguageVersion = version,
            AdditionalReferences = references.ToArray()
        };
        return new(new(source.FileName, context) { Node = node }, node, defaultItems, xmlAvailable, source.ExceedsByteLimit,
            includes, removes, projectReferences);
    }

    private static void ReadProjectReference(XElement project, XElement group, XElement item, string directory,
        CSharpImportPaths paths, bool hasImportRoot, Dictionary<string, string> references, Action<string> note)
    {
        var include = ((string?)item.Attribute("Include"))?.Trim() ?? "";
        if (project.Attribute("Condition") is not null || IsConditional(group, item))
            note("조건부 ProjectReference는 지원하지 않습니다.");
        else if (item.HasElements || !string.IsNullOrWhiteSpace(item.Value) ||
            item.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && attribute.Name != "Include"))
            note("추가 MSBuild 속성이나 메타데이터가 있는 ProjectReference는 지원하지 않습니다.");
        else if (include.Contains(';') || !IsLiteral(include))
            note("동적, 절대, 와일드카드 및 목록 형식의 ProjectReference 경로는 지원하지 않습니다. 고정 상대 경로를 사용하세요.");
        else if (!IsProject(include))
            note("C# 프로젝트(.csproj)를 대상으로 하는 ProjectReference만 지원합니다.");
        else if (!hasImportRoot)
            note("파일 선택에는 프로젝트 참조 범위가 지정되지 않아 ProjectReference를 연결하지 않았습니다. 프로젝트가 포함된 폴더를 가져오세요.");
        else if (paths.Resolve(directory, include) is not { } path)
            note($"가져오기 루트 밖이거나 해석할 수 없는 ProjectReference 경로입니다 ({include}).");
        else if (references.ContainsKey(path))
            return;
        else if (references.Count == MaxProjectReferences)
            note($"프로젝트당 ProjectReference {MaxProjectReferences}개 한도로 나머지 참조를 생략했습니다.");
        else
            references.Add(path, include);
    }

    private static bool IsConditional(XElement group, XElement item) =>
        group.Attribute("Condition") is not null || item.Attribute("Condition") is not null;

    private static bool IsDynamic(string text) =>
        text.Contains("$(", StringComparison.Ordinal) || text.Contains("@(", StringComparison.Ordinal) ||
        text.Contains("%(", StringComparison.Ordinal);

    private static string[] Split(string value) => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void ReadPaths(XElement item, string attribute, HashSet<string> paths, HashSet<string> opposite,
        string directory, CSharpImportPaths importPaths, Action<string> note)
    {
        if ((string?)item.Attribute(attribute) is not { } list)
            return;
        foreach (var entry in Split(list))
        {
            if (ResolveLiteral(importPaths, directory, entry) is { } path)
            {
                paths.Add(path);
                opposite.Remove(path);
            }
            else
                note("동적 또는 범위 밖 Compile 경로와 와일드카드는 적용하지 않았습니다.");
        }
    }

    private static bool IsLiteral(string value) =>
        value.Length is > 0 and <= 4_096 && value[0] is not ('/' or '\\') && !IsDynamic(value)
        && value.IndexOfAny(['*', '?', ':', '%', '\0', '\r', '\n', '\t']) < 0;

    private static string? ResolveLiteral(CSharpImportPaths paths, string directory, string value) =>
        IsLiteral(value) ? paths.Resolve(directory, value) : null;

    private static byte[] ReadLimited(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit)
            throw new FileSizeLimitException();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool TryReadReference(string root, string path, List<MetadataReference> references, ref int budget)
    {
        if (budget == 0)
            return false;
        try
        {
            // Check components from the selected root down before opening the metadata file.
            var current = root;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return false;
            foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }

            var bytes = ImmutableArray.Create(ReadLimited(path, Math.Min(MaxReferenceBytes, budget)));
            budget -= bytes.Length;
            using var pe = new PEReader(bytes);
            if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly)
                return false;
            references.Add(MetadataReference.CreateFromImage(bytes, filePath: path));
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    private sealed class FileSizeLimitException : IOException;

    private sealed record Project(CSharpProjectContext Context, CSharpProjectNode Node, bool DefaultItems,
        bool XmlAvailable, bool ExceedsByteLimit, HashSet<string> Includes, HashSet<string> Removes,
        Dictionary<string, string> ReferencePaths);
}
