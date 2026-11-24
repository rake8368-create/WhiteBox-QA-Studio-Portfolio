# Python 정적 분석 범위

## 파서 선택

`TreeSitter.DotNet` 1.3.0은 .NET Standard 2.0 바인딩과 미리 빌드된 네이티브 파일을 포함하며 NuGet 전이 의존성이 없습니다. 해당 릴리스의 Tree-sitter 런타임은 0.26.3(`cd4b6e2`), Python 문법은 0.25.0(`26855ea`, ABI 15)입니다. 세 구성 요소의 MIT 고지를 Core 어셈블리에 포함합니다. 앱 게시에는 Tree-sitter 런타임과 Python, Java, JavaScript, C++, TypeScript, TSX 문법 DLL을 포함합니다. [NuGet 메타데이터](https://www.nuget.org/packages/TreeSitter.DotNet/1.3.0) · [바인딩 소스](https://github.com/mariusgreuel/tree-sitter-dotnet-bindings/tree/v1.3.0) · [Python 문법](https://github.com/tree-sitter/tree-sitter-python/blob/26855eabccb19c6abf499fbc5b8dc7cc9ab8bc64/grammar.js) · [라이선스 고지](THIRD_PARTY_NOTICES.txt)

CPython `ast.parse`는 릴리스별 Python AST를 만들며 구문 검증 요구가 커질 때 비교할 수 있습니다. 이를 호출하려면 CPython 실행 파일이나 내장 런타임을 배포해야 하고, 스코프 등 일부 검사는 컴파일 단계에서 수행됩니다. Day 04는 외부 Python 설치 없이 WPF 단일 EXE에서 사용할 수 있는 Tree-sitter 구문 트리를 채택했습니다. [CPython AST 문서](https://docs.python.org/3/library/ast.html#ast.parse) · [Microsoft 단일 파일 배포 안내](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)

- `.py`와 `.pyi`에 같은 문법을 사용합니다. `async def`와 `await`, 데코레이터, 타입 주석과 타입 매개변수, `match/case`, `except*` 파싱을 테스트했습니다. `.pyi`의 타입 검사 및 Python 버전별 구문 인증은 범위 밖입니다.
- 분석기는 원본 모듈, import, 함수, 데코레이터를 실행하지 않습니다. 분석 시 네트워크 요청이나 패키지 설치를 수행하지 않습니다. 사용자가 실행한 공개 HTTPS Git 가져오기는 분석 단계 전에 소스 스냅샷을 읽습니다.
- Tree-sitter의 복구 문법은 CPython 의미 검증을 제공하지 않습니다. 빈 블록, 같은 블록 안의 들여쓰기 불일치, 잘못된 `try` 절 구성을 추가 검사합니다. 스코프 밖의 `return`처럼 별도 의미 검사가 필요한 경우는 놓칠 수 있습니다. 구문 진단이 있는 파일은 경계가 바뀐 복구 트리에서 오탐을 방지하기 위해 PY-001만 적용하고, 인식된 함수에는 CFG 분석 불가를 표시합니다. [Tree-sitter ERROR·MISSING 노드](https://tree-sitter.github.io/tree-sitter/using-parsers/queries/1-syntax.html#special-nodes)

## 규칙

| ID | 위험도 | 판정 범위 |
| --- | --- | --- |
| PY-001 | 높음 | 트리의 `ERROR` 또는 `MISSING`, 빈 필수 블록, 블록에서 일관되지 않은 들여쓰기, 타입 없는 `except*`, `except`와 `except*` 혼합, 잘못된 bare `except` 순서, 필수 `try` 절 누락. 중첩 복구 오류와 같은 오프셋의 반복 진단을 억제합니다. |
| PY-002 | 낮음 | 주석을 제외한 `except` 본문이 `pass`, 생략 기호 `...`, 치환식 없는 문자열로만 구성됩니다. 괄호로 감싼 형태도 포함합니다. 의도적인 예외 무시인지 검토할 항목입니다. 본문 없이 주석만 있는 필수 블록은 PY-001에서 다룹니다. |
| PY-003 | 보통 | 예외 타입이 생략된 `except:`를 검토합니다. `BaseException` 계열 전체를 잡으며 일반 `Exception` 바깥의 `KeyboardInterrupt`, `SystemExit`도 포함합니다. 명시적인 `except BaseException:`과 `except Exception:`은 이 규칙의 대상에서 제외됩니다. |
| PY-004 | 낮음 | 일반 `except … as 이름`의 본문과 재바인딩 가능성이 없는 `if/elif/else` 분기의 `raise 이름`을 검토합니다. 괄호로 감싼 같은 이름도 식별합니다. 앞선 구문에서 이름이 다시 바인딩될 가능성이 있거나, 명시적 `from` 체인, `except*`, 중첩 스코프에서 바깥 변수를 사용하는 경우는 제외합니다. 중첩 핸들러 자체가 잡은 변수는 독립적으로 검사합니다. |

PY-004는 대입 대상과 import가 실제 바인딩하는 이름을 구분합니다. 중첩 함수의 지역변수 쓰기와 선언 시 평가될 수 있는 기본값을 구분하고, 같은 이름의 `global`·`nonlocal`이 있으면 보수적으로 봅니다. 중첩 `try`, 반복문, `with`, `match` 본문의 재던지기와 동적 호출·별칭 효과는 추적 범위 밖입니다. 현재 예외를 재전파하려는 경우 `raise`를 검토할 수 있습니다. [Python 예외 핸들러 명세](https://docs.python.org/3/reference/compound_stmts.html#the-try-statement)

## 함수 제어 흐름 및 기초 복잡도

- 일반 `def`의 시작·종료 블록, 원본 줄, 실행문 사이 연결, 정적으로 도달 가능한 경로를 제공합니다. 메서드와 중첩 함수는 클래스·함수명을 붙여 독립적으로 표시합니다.
- `if/elif/else`, `for/while` 및 반복문의 `else`, `break`, `continue`, `return`을 연결합니다. `for`의 반복자 초기화와 다음 항목 검사를 분리하며, `break`는 해당 반복문의 `else`를 건너뜁니다. 리터럴 `True`·`False`로 결정되는 간선을 제거합니다. 변수 값, 함수 호출의 결과 및 암시적 예외는 추적 범위 밖입니다. [Python 조건 및 반복문 명세](https://docs.python.org/3/reference/compound_stmts.html)
- `if/elif/while` 조건의 단순한 `A and B`와 `A or B`는 피연산자별 블록을 만듭니다. `and`에서는 왼쪽이 참일 때만, `or`에서는 왼쪽이 거짓일 때만 오른쪽으로 이동합니다. 이름, 호출, 속성·인덱스 접근, 단일 비교, `True`·`False`를 단순 피연산자로 지원합니다. 호출은 한 피연산자로 표현합니다. [Python 논리 연산 명세](https://docs.python.org/3/reference/expressions.html#boolean-operations)
- 기초 복잡도는 **1 + 시작점에서 도달할 수 있고 두 분기를 표현한 지원 결정 수**입니다. 상수가 간선을 하나로 결정하면 **단일 경로**, 건너뛴 피연산자에는 **도달 불가**를 표시하고 집계에서 제외합니다.
- 세 항 이상, 중첩 논리식, `not`과 결합한 논리식, 대입·반환 값의 논리식은 **범위 제한**을 유지합니다. 연쇄 비교, 조건 표현식, 컴프리헨션, `assert` 내부 흐름도 제외합니다. 미지원 표현식이 들어간 조건 자체는 집계에서 제외하며, 독립적으로 표현한 다른 분기는 집계합니다.
- 예외 처리·`raise`, `match`, `with`, 비동기 함수·반복, 제너레이터 및 함수 안에서 실행되는 클래스 본문이 있으면 해당 함수 전체가 **분석 불가**입니다. `.pyi`의 선언과 구문 오류 파일에서 인식된 함수에도 CFG와 복잡도 숫자를 표시하지 않습니다. 모듈 최상위, 람다 본문, 커버리지는 분석 범위 밖입니다.
- 함수당 블록 4,096개와 중첩 96단계, Python 분석 전체에 블록 100,000개와 함수 결과 20,000개 한도를 적용합니다. 그래프 한도에 걸린 함수는 사유를 표시하고, 함수 결과 한도로 생략한 파일은 분석 참고에 안내합니다.

## 입력, 상태 및 위치

- 로컬 파일·폴더와 공개 HTTPS Git에서 C# 및 Python 파일을 읽습니다. 가져오기마다 소스 후보 2,000개, 개별 2 MiB, 합계 32 MiB를 적용합니다. 확장자로 발견한 파일 수에는 한도, 오류, 미지원 언어 때문에 분석하지 못한 파일도 포함됩니다.
- Python은 UTF-8 및 UTF-8 BOM을 기본으로 읽습니다. 처음 두 줄에서 유효한 `coding` 선언을 인식하며 UTF-8, ASCII, Latin-1, CP949, EUC-KR, CP1252의 지정 별칭을 지원합니다. 지원하지 않는 인코딩, BOM 충돌, 잘못된 바이트, NUL과 Git LFS 포인터는 파일별 가져오기 오류로 표시합니다. C#의 기존 Unicode BOM 지원을 유지합니다. [PEP 263](https://peps.python.org/pep-0263/)
- 줄과 열은 1부터 시작합니다. 열은 WPF/.NET과 같은 UTF-16 코드 단위라서 서로게이트 쌍인 이모지는 두 열, 탭은 한 열입니다. 물리적 줄바꿈 LF, CRLF, CR을 같은 방식으로 처리합니다.
- 구문 오류가 보고된 파일은 구문을 분석한 파일로 집계합니다. 네이티브 DLL 누락, 런타임 또는 ABI 초기화 실패로 트리를 만들 수 없는 경우는 파일별 분석 오류이며 분석 파일 수에 포함하지 않습니다. 같은 입력에서 분석된 C# 파일은 결과에 남습니다.
- Python 규칙은 공통 결과 표, JSON v1 이력, Excel 상세 분석 결과에 표시합니다. 이전 JSON v1 이력에서 언어별 분석 수가 없는 경우에는 기존 발견 수를 표시합니다. Excel은 기존 네 시트를 유지합니다. 제어 흐름 시트에 C#·Python 함수 요약과 언어를 기록하고 Python 조건·분기 상세를 그 아래에 기록합니다. JSON 스키마 v1을 유지하며, 상수 결정의 선택 필드가 없는 이전 이력도 복원합니다.
