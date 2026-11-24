# QA 테스트 전략

## 요구사항을 판정 가능한 결과로 바꾸기

WhiteBox QA Studio는 로컬 또는 공개 HTTPS Git의 소스에서 검토할 위치를 찾는 개인 프로젝트입니다. 언어 식별, 분석 가능 여부, 진단 ID·위험도·원본 위치, 분석 제한, 결과 저장을 각각 검증 대상으로 정했습니다. Codex와 구현과 테스트를 반복하면서 기대 결과와 완료 기준을 정하고, 테스트 단언·CI 결과·실제 화면을 대조했습니다.

## 설계 기법과 실행 증거

| 검증 기법 | 실제 사례와 판정 기준 | 테스트 소스 |
| --- | --- | --- |
| 동등 분할 | TypeScript의 `.ts/.mts/.cts`와 TSX의 `.tsx`를 문법 그룹으로 나눕니다. Java 빈 `catch`는 주석·빈 문장·빈 블록과 선언·호출·실행문을 나눠 결과 유무를 단언합니다. | [문법 선택](../tests/WhiteBoxQA.Tests/TypeScriptAnalyzerTests.cs#L16) · [Java catch 분할](../tests/WhiteBoxQA.Tests/JavaAnalyzerTests.cs#L172) |
| 경계값 분석 | UTF-8 파일이 가져오기 상한과 정확히 같을 때 읽고 1바이트 초과하면 생략합니다. 분석하지 않은 파일은 발견 수에 남아야 합니다. Java 구문 진단은 상한 초과분이 있을 때에만 생략 사유를 남깁니다. | [파일 크기 경계](../tests/WhiteBoxQA.Tests/JavaScriptIntegrationTests.cs#L63) · [진단 수 경계](../tests/WhiteBoxQA.Tests/JavaAnalyzerTests.cs#L259) |
| 결정 테이블 | TypeScript 파일에 구문 오류가 있는지와 `catch` 본문의 실행 여부를 조합해 TS-001, TS-002, 생략 안내를 구분합니다. | [TS·TSX 규칙 결정](../tests/WhiteBoxQA.Tests/TypeScriptAnalyzerTests.cs#L73) |
| 부정 테스트 | 문법 DLL 로드 실패, 파싱 예외, null 트리에서 해당 파일의 제한을 기록하고 다른 언어·문법은 계속 분석합니다. 입력에 인증 정보가 붙은 Git 주소는 거부합니다. | [실패 격리](../tests/WhiteBoxQA.Tests/TypeScriptAnalyzerTests.cs#L96) · [Git 주소 검사](../tests/WhiteBoxQA.Tests/InfrastructureTests.cs#L105) |
| 제어 흐름 기반 검토 | Python `and/or`에서 왼쪽·오른쪽 평가 순서와 참·거짓 간선을 단언합니다. `while`에서 재평가, `continue`, `break`, `else`의 연결도 확인합니다. | [Python 단락 평가](../tests/WhiteBoxQA.Tests/PythonShortCircuitTests.cs#L10) · [반복문 간선](../tests/WhiteBoxQA.Tests/PythonShortCircuitTests.cs#L41) |
| 회귀 테스트 | 제외 폴더를 직접 선택해도 사전 취소는 지켜야 합니다. 게시 실패 시 기존 EXE와 외부 추출 경로를 보존하고 보고서 경로 오류는 저장 대화상자 초기 위치에만 영향을 줘야 합니다. | [제외 폴더 취소](../tests/WhiteBoxQA.Tests/JavaScriptIntegrationTests.cs#L52) · [게시 회귀](../tests/PublishScripts.Tests.ps1#L78) · [상대 Documents 경로](../tests/WhiteBoxQA.Tests/ResultWorkspaceTests.cs#L63) |
| JSON 호환성 | v1에서 CFG 필드가 빠진 이력을 복원하고 임의의 복잡도를 채우지 않는지 확인합니다. 예전 TypeScript 식별 전용 레코드는 분석 0건을 보존합니다. | [기존 v1과 CFG](../tests/WhiteBoxQA.Tests/JsonAnalysisHistoryTests.cs#L126) · [기존 TypeScript 이력](../tests/WhiteBoxQA.Tests/TypeScriptIntegrationTests.cs#L73) |
| E2E와 배포 스모크 | 통합 자동화는 6개 언어의 폴더 읽기부터 JSON 왕복과 Excel 4시트까지 검사합니다. 별도 수동 시나리오에서는 게시 EXE로 검색·줄 이동·CFG·Excel 저장을 확인했습니다. CI는 게시 EXE 자체의 5개 스모크 진입점과 게시 스크립트를 실행합니다. | [혼합 자동화](../tests/WhiteBoxQA.Tests/TypeScriptIntegrationTests.cs#L13) · [수동 검증 기록](VERIFICATION.md#제품-화면-검증) · [Windows 워크플로](../.github/workflows/dotnet-ci.yml) |

### 결정 테이블 예시

아래 표는 같은 유형의 `.ts`와 `.tsx` 입력을 실행하는 기존 테스트의 판정을 정리한 것입니다. 정상 파일에는 선언, 호출, `return`, `throw`와 중첩 `catch`를 포함합니다. [테스트 단언](../tests/WhiteBoxQA.Tests/TypeScriptAnalyzerTests.cs#L73)

| 파일 구문 오류 | catch 본문 | 기대 결과 |
| --- | --- | --- |
| 없음 | 비어 있거나 주석·세미콜론·빈 블록만 있음 | TS-002 낮음 |
| 없음 | 선언·호출·반환·던지기 등 처리 있음 | 해당 catch에서 TS-002 미보고 |
| 있음 | 빈 catch를 포함 | TS-001 보고, 해당 파일 TS-002 생략 사유 기록. 같은 배치의 정상 파일은 계속 검사 |

## 품질 완료 게이트

1. Windows에서 Release `-warnaserror` 빌드의 경고·오류가 0이어야 합니다.
2. 기존 xUnit 보호 범위 486개 이상 전부 통과, 실패·건너뜀 0이어야 합니다. 로그와 TRX 카운터를 대조합니다.
3. Windows x64 자체 포함 단일 EXE가 게시되어야 합니다. Python·CFG, Java·C# CFG, JS·JSX, C++, TS·TSX 스모크와 게시 스크립트·프로그램 버전 검증이 통과해야 합니다.
4. 실제 제품 입력에서 진단 ID, 위험도, 원본 줄, 제한 및 Excel 결과를 대조한 E2E 근거를 보관합니다. 문서 변경은 제품 파일 일치와 링크·렌더링을 추가로 확인합니다.

[실행 방식과 실측 결과](VERIFICATION.md) · [실제 결함 및 예방 검증](DEFECT_CASE_STUDIES.md)

## 측정 범위와 한계

xUnit 통과 수는 제품 및 통합 테스트의 통과 수입니다. 제품 코드의 라인·분기 커버리지는 이번 검증에서 측정하지 않았습니다. 분석 대상 프로그램의 코드를 실행하거나 테스트 커버리지를 수집하는 기능도 지원 범위 밖입니다. CFG는 지원되는 C#·Python 구문에 한하며 Tree-sitter 진단은 구문 복구 결과입니다. C++ 전처리 평가, TypeScript 타입 검사, 파싱 시간·메모리의 하드 상한은 지원 범위 밖입니다. [아키텍처와 지원 범위](ARCHITECTURE.md)
