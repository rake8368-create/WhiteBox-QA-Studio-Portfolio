namespace WhiteBoxQA.Infrastructure;

internal static class SourceImportLimits
{
    public const int MaxFiles = 2_000;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxTotalBytes = 32 * 1024 * 1024;

    public const string FileSizeLimit = "개별 파일의 2 MiB 제한을 넘어 건너뛰었습니다.";
    public const string TotalSizeLimit = "전체 소스의 32 MiB 제한을 넘어 건너뛰었습니다.";

    public static string FileCountLimit(int skipped) =>
        $"최대 {MaxFiles:N0}개 제한을 넘어 소스 파일 {skipped:N0}개를 건너뛰었습니다.";
}
