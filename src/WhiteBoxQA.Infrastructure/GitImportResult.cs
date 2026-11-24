using WhiteBoxQA.Core;

namespace WhiteBoxQA.Infrastructure;

public enum GitImportStatus { Completed, Cancelled, TimedOut, Failed, DiskLimitExceeded }

public enum GitImportStage { Validating, Cloning, Listing, Reading, Preparing, Completed }

public sealed record GitImportProgress(GitImportStage Stage, int Processed = 0, int Total = 0, long? TemporaryBytes = null)
{
    public string Message => Stage switch
    {
        GitImportStage.Validating => "Git 주소를 확인하는 중입니다…",
        GitImportStage.Cloning when TemporaryBytes is long used =>
            $"Git 복제 중 · 임시 파일 {used / (1024d * 1024):N1} MiB",
        GitImportStage.Cloning => "Git 저장소에 연결하고 복제하는 중입니다…",
        GitImportStage.Listing => "소스 언어와 파일 목록을 확인하는 중입니다…",
        GitImportStage.Reading => $"소스 파일 {Processed:N0} / {Total:N0}개를 처리했습니다.",
        GitImportStage.Preparing => "분석 입력을 준비하는 중입니다…",
        _ => "Git 저장소 가져오기를 완료했습니다."
    };
}

public sealed record GitImportResult(GitImportStatus Status, SourceInput? Input, string Message);
