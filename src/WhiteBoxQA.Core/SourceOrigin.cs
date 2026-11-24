using System.Text.Json.Serialization;

namespace WhiteBoxQA.Core;

public enum SourceKind { DirectInput, LocalFiles, LocalFolder, Git }

public sealed record SourceOrigin
{
    public SourceOrigin(SourceKind kind, string? remoteUrl = null, string? commitSha = null,
        string? branch = null, DateTimeOffset? importedAt = null)
    {
        if (kind == SourceKind.Git)
        {
            if (!TryGetPublicGitUrl(remoteUrl, out var publicUrl))
                throw new ArgumentException("인증 정보가 없는 HTTPS 주소가 필요합니다.", nameof(remoteUrl));
            if (commitSha is not null && !IsGitObjectId(commitSha))
                throw new ArgumentException("Git 커밋 SHA 형식이 올바르지 않습니다.", nameof(commitSha));

            RemoteUrl = publicUrl;
            CommitSha = commitSha;
            Branch = branch;
        }

        Kind = kind;
        ImportedAt = importedAt;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public SourceKind Kind { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RemoteUrl { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CommitSha { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Branch { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? ImportedAt { get; }
    public bool HasLocalEdits { get; init; }

    public string KindLabel => Kind switch
    {
        SourceKind.DirectInput => "직접 입력",
        SourceKind.LocalFiles => "로컬 파일",
        SourceKind.LocalFolder => "로컬 폴더",
        SourceKind.Git => "Git 저장소",
        _ => "기록 없음"
    };

    public static bool IsGitObjectId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    public static bool TryGetPublicGitUrl(string? value, out string url)
    {
        url = "";
        const string scheme = "https://";
        if (value is null || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || value.Any(char.IsControl) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        var authority = value.AsSpan(scheme.Length);
        var pathStart = authority.IndexOf('/');
        if ((pathStart < 0 ? authority : authority[..pathStart]).Contains('@'))
            return false;

        url = uri.AbsoluteUri;
        return true;
    }
}
