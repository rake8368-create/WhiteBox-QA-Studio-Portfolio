# 결함 발견과 회귀 검증

원본 개발 기록에서 확인한 동작을 기대 결과와 비교했습니다. 재현한 동작과 실패 주입·경계 조건으로 추가 보호한 항목을 각 사례에 표시했습니다.

## 1. 제외 폴더의 빠른 반환이 취소를 누락

| 항목 | 근거 |
| --- | --- |
| 재현 조건 | Day 07 최종 리뷰에서 로컬 가져오기로 `node_modules` 폴더를 직접 선택하고 이미 취소된 토큰을 전달했습니다. 폴더 이름은 대소문자를 섞어도 제외 대상입니다. |
| 기대 결과 | 읽기보다 취소가 우선하며 `OperationCanceledException`으로 반환됩니다. |
| 실제 결과 | 제외 폴더 처리의 빠른 반환이 먼저 실행돼 취소가 반영되지 않았습니다. |
| 원인 | 가져오기 진입점에서 취소를 검사하기 전에 제외 경로를 판정했습니다. |
| 수정 | 로컬 폴더 읽기 시작 시 취소 토큰을 확인하고 이후에 제외 폴더를 판정합니다. [구현](../src/WhiteBoxQA.Infrastructure/SourceImporter.cs#L55) |
| 회귀 검증 | 사전 취소한 토큰과 대소문자를 섞은 폴더에서 예외를 단언합니다. 일반 자동 탐색의 제외, 개별 파일 열기, Git 발견·읽기 예산도 각각 확인합니다. [취소 재현](../tests/WhiteBoxQA.Tests/JavaScriptIntegrationTests.cs#L52) · [로컬 가져오기](../tests/WhiteBoxQA.Tests/JavaScriptIntegrationTests.cs#L15) · [Git 예산](../tests/WhiteBoxQA.Tests/GitSourceImporterTests.cs#L206) |

## 2. Windows 단일 EXE 스모크가 추출물을 재사용

| 항목 | 근거 |
| --- | --- |
| 재현 조건 | Day 07 이전 게시 스크립트가 Windows x64 단일 EXE의 Python·Java 스모크를 한 호출 안에서 순서대로 실행할 때입니다. 공개 문서 검토 중 해당 스크립트에 시험용 EXE 복사 단계를 제공하고 격리 폴더에서 .NET 호스트 추적을 확인했습니다. |
| 기대 결과 | 각 언어 스모크가 빈 추출 위치에서 네이티브 DLL을 직접 풀고 시작해야 합니다. |
| 실제 결과 | 두 번째 스모크가 첫 번째에서 추출한 번들을 재사용했습니다. 재현 실행은 성공했으며 관찰한 결함은 두 번째 스모크가 새 추출을 검증할 수 없다는 점입니다. |
| 원인 | 두 진입점에 게시 스테이징 내부의 같은 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`을 전달했습니다. |
| 수정 | 게시 스테이징과 별개인 고유 임시 폴더를 만들고 언어별 하위 추출 경로를 사용합니다. CI도 언어별로 별도 추출 경로를 사용합니다. [게시 코드](../scripts/publish-win-x64.ps1#L124) · [CI 스모크](../.github/workflows/dotnet-ci.yml) |
| 회귀 검증 | 다섯 진입점의 독립 경로와 임시 정리를 검사합니다. 실패 주입은 호출자 환경, 기존 EXE, 외부 추출 파일 보존을 추가로 검증합니다. [게시 회귀](../tests/PublishScripts.Tests.ps1#L78) |

## 3. Documents 경계와 Excel 저장 대화상자

| 항목 | 근거 |
| --- | --- |
| 재현 조건 | Day 06 실제 WPF에서 연결되지 않은 OneDrive 리디렉션 Documents를 사용했을 때 기본 Reports 폴더를 만들 수 없었습니다. Day 08은 별도 시험 프로세스의 `WHITEBOXQA_DOCUMENTS_DIR` 공백·상대 경로 사례와 이전 저장 폴더 복원까지 검토했습니다. |
| 기대 결과 | 공백은 Windows Documents 기본값을 사용합니다. 설정 경로는 절대 경로여야 하고, 기본 Reports 경로 오류가 있어도 대화상자를 열어 쓸 수 있는 이전 폴더 또는 사용자가 새 경로를 선택하도록 합니다. 저장은 사용자가 확정한 경우에만 진행합니다. |
| 실제 결과 | 리디렉션된 기본 폴더 준비가 실패했습니다. 상대 경로에는 작업 폴더 기준으로 해석되는 경계가 있었고, 최종 수정 후 별도 시험 프로세스에서 상대 경로를 입력하자 마지막 폴더를 제안했습니다. 저장 취소 후 기존 시험용 보고서와 이력이 보존됐습니다. |
| 원인 | 기본 Windows Documents 경로가 연결되지 않은 디렉터리를 가리킬 수 있고, 지정 경로가 완전한 경로인지 별도로 판정해야 합니다. |
| 수정 | 공백 설정은 기본 Documents로 연결하고 상대 경로에는 오류를 반환합니다. 기본 폴더를 사용할 수 없으면 접근 가능한 이전 저장 경로를 제안하며 이력 폴더는 독립적으로 유지합니다. [경로 결정 코드](../src/WhiteBoxQA.Infrastructure/ResultWorkspace.cs#L14) |
| 회귀 검증 | 공백·상대 경로, 실제 Windows ACL 접근 거부, 이전 위치 복원과 기존 보고서 보존을 확인합니다. ACL과 잠긴 Excel은 별도의 부정 테스트입니다. [공백](../tests/WhiteBoxQA.Tests/ResultWorkspaceTests.cs#L26) · [상대 경로](../tests/WhiteBoxQA.Tests/ResultWorkspaceTests.cs#L63) · [이전 위치](../tests/WhiteBoxQA.Tests/ResultWorkspaceTests.cs#L244) · [ACL·잠김](../tests/WhiteBoxQA.Tests/ResultWorkspaceTests.cs#L291) |

OneDrive 계정이나 동기화 동작은 검증 범위 밖입니다. 구버전 C++·TypeScript 식별 전용 이력은 기존 분석 0건을 유지하는 호환성 검증으로 관리합니다. [C++ 이력](../tests/WhiteBoxQA.Tests/CppIntegrationTests.cs#L133) · [TypeScript 이력](../tests/WhiteBoxQA.Tests/TypeScriptIntegrationTests.cs#L73)

[QA 전략](QA_TEST_STRATEGY.md) · [공개 검증 보고서](VERIFICATION.md)
