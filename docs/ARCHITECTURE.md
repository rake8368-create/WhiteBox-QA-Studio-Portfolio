# 분석 아키텍처

| 단계 | 코드 | 계약과 검증 |
| --- | --- | --- |
| 소스 가져오기 | [Infrastructure](../src/WhiteBoxQA.Infrastructure/) | 로컬 파일·폴더와 공개 HTTPS Git HEAD를 읽습니다. Git은 체크아웃 없이 객체에서 읽고, 취소·시간·파일 수·용량 한도를 적용합니다. |
| 분류와 실행 | [Core](../src/WhiteBoxQA.Core/) | 확장자를 분류하고 분석기 레지스트리를 사용합니다. Roslyn은 C#, Tree-sitter는 Python·Java·JavaScript/JSX·C++·TypeScript/TSX를 분석합니다. `.tsx`는 TSX 문법을 사용합니다. |
| 결과 모델 | [Core](../src/WhiteBoxQA.Core/) | 발견·분석 수, 규칙별 위험도, 원본 위치와 분석 제한을 전달합니다. 실패 파일을 격리해 읽을 수 있는 다른 파일의 분석을 이어갑니다. |
| 검토와 저장 | [WPF](../src/WhiteBoxQA.App/) · [Infrastructure](../src/WhiteBoxQA.Infrastructure/) | 규칙 검색과 줄 이동, C#·Python CFG, JSON v1 이력 및 Excel 4개 시트를 제공합니다. |
| 배포 게이트 | [CI](../.github/workflows/dotnet-ci.yml) · [테스트](../tests/) | Windows Release 빌드, xUnit, 단일 EXE, 다섯 스모크, 스크립트 및 버전 확인을 실행합니다. |

## 설계 선택

- 언어별 분석 기능을 독립적으로 선언합니다. C#과 Python만 CFG를 제공하며 Java, JavaScript, C++, TypeScript는 정적 규칙을 제공합니다.
- 네이티브 문법을 지연 로딩하고 파일별 parser와 tree를 해제합니다. 구문 복구 진단은 같은 시작 위치를 중복 표시하지 않으며, 원본의 1부터 시작하는 UTF-16 줄·열로 변환합니다.
- 발견, 읽기, 분석 가능한 파일 수를 분리해 집계합니다. 구버전 JSON v1의 식별 전용 파일도 당시 상태 그대로 복원합니다.
- 단일 EXE는 Tree-sitter 런타임과 Python, Java, JavaScript, C++, TypeScript, TSX 문법 DLL을 포함합니다. 게시 스크립트는 언어별로 별도 네이티브 추출 폴더를 사용합니다.

[CFG 상세](CONTROL_FLOW_DESIGN.md) · [C# 프로젝트 참조](PROJECT_REFERENCE_DESIGN.md) · [결과 및 재현 절차](VERIFICATION.md)
