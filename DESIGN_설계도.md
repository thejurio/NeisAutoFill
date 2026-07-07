# NEIS 교과평가 자동입력기 — C# 설계도 (Architecture)

작성일: 2026-07-07
기반: `NEIS_자동입력기_개발노트.md` (Python 프로토타입 검증 완료본)
목표: 검증된 알고리즘을 유지하면서 유지보수 가능한 C# 애플리케이션으로 재구현

---

## 1. 기술 스택 결정

| 역할 | 선택 | 이유 |
|---|---|---|
| 런타임 | **.NET 8 (LTS)** | 장기 지원, self-contained 단일 exe 배포 |
| 브라우저 제어 | **Playwright for .NET** (`ConnectOverCDPAsync`) | §8 마지막 행 이슈를 `Mouse.WheelAsync`(trusted) 로 해결 가능. 완전 async 모델 |
| 엑셀 | **ClosedXML** | MIT 라이선스, `data_only` 값 읽기 용이, EPPlus는 상용 라이선스 이슈 |
| GUI | **WPF + MVVM** | async/await 친화, 엔진/UI 완전 분리, DataGrid·TabControl 내장 |
| 진행 보고 | `IProgress<T>` + `async/await` | Python 판의 queue 폴링을 대체 (UI 스레드 자동 마셜링) |
| DI | `Microsoft.Extensions.DependencyInjection` | ViewModel·서비스 조립, 테스트 대체 용이 |
| 테스트 | **xUnit** | Core/Excel 계층 단위 테스트 |
| 로깅 | **Seric Serilog** (파일 싱크) | §7.5 감사 추적 로그 |

> Playwright 선택의 핵심 근거: 프로토타입에서 확인된 유일한 미해결 이슈(마지막 행 미렌더링)의
> 1순위 해법이 trusted wheel 이벤트이며, 이는 Playwright `Page.Mouse.WheelAsync` 로 네이티브 제공된다.

---

## 2. 솔루션 구조 (계층 분리)

```
NeisAutoFill.sln
├─ src/
│  ├─ NeisAutoFill.Core/          # 도메인 — 외부 의존성 0 (순수 C#)
│  │   ├─ Models/
│  │   │   ├─ Student.cs          # no, Name, Grades(Dictionary<area,grade>)
│  │   │   ├─ SubjectSheet.cs     # SubjectName, Areas[], Students[]
│  │   │   ├─ GradeTask.cs        # RowIndex, No, Name, Area, TargetGrade
│  │   │   ├─ RowMeta.cs          # No, Name, Area (화면 파싱 결과)
│  │   │   └─ RunReport.cs        # Done/Skipped/Failed/Missing 목록
│  │   ├─ Matching/
│  │   │   ├─ NameNormalizer.cs   # "박서연(전입학)" → "박서연"
│  │   │   └─ StudentMatcher.cs   # (번호,이름)→이름 우선순위 매칭 + 화이트리스트
│  │   └─ GradeValues.cs          # ALLOWED={잘함,보통,노력요함}, DataId 매핑
│  │
│  ├─ NeisAutoFill.Excel/         # 엑셀 파서
│  │   └─ WorkbookLoader.cs       # xlsx → List<SubjectSheet> (ClosedXML)
│  │
│  ├─ NeisAutoFill.Automation/    # 브라우저 엔진 (Playwright)
│  │   ├─ Abstractions/
│  │   │   ├─ INeisEngine.cs      # 인터페이스 (ViewModel이 의존)
│  │   │   └─ IProgressSink.cs    # 로그/진행 콜백 추상화
│  │   ├─ NeisSelectors.cs        # ★ 모든 CSS 셀렉터·정규식·타이밍 상수 집중
│  │   ├─ NeisEngine.cs           # attach/과목검증/그리드파싱/set_grade
│  │   ├─ RowMapBuilder.cs        # build_row_index 포팅
│  │   ├─ ComboBoxDriver.cs       # 콤보 열기(네이티브 클릭)·옵션 선택·검증
│  │   ├─ GridScroller.cs         # 프록시 스크롤 + trusted wheel (§8 해결부)
│  │   └─ EdgeLauncher.cs         # 디버그 Edge 프로세스 실행
│  │
│  └─ NeisAutoFill.App/           # WPF (MVVM)
│      ├─ App.xaml(.cs)           # DI 컨테이너 부트스트랩
│      ├─ Views/
│      │   ├─ MainWindow.xaml     # 상단 버튼바 + 탭 + 진행바 + 로그
│      │   └─ SubjectTabView.xaml # 과목 탭 (DataGrid + 실행 버튼)
│      ├─ ViewModels/
│      │   ├─ MainViewModel.cs    # 연결상태/엑셀로드/탭 목록/로그
│      │   └─ SubjectViewModel.cs # dry-run/입력/중지 커맨드
│      └─ Services/
│          ├─ DialogService.cs    # 확인 대화상자·파일 선택
│          └─ AppSettings.cs      # 경로/URL/배율 설정 (JSON)
│
└─ tests/
   └─ NeisAutoFill.Tests/         # Core·Excel 단위 테스트 (브라우저 불필요)
```

### 2.1 의존성 방향 (안쪽으로만)

```
App  ──→  Automation ──→ Core
 │                        ↑
 └────→   Excel ──────────┘
```

- **Core 는 아무것에도 의존하지 않음** → 매칭·정규화 로직을 브라우저 없이 100% 단위 테스트.
- App 은 `INeisEngine` 인터페이스에만 의존 → 테스트/모의 구현 교체 가능.

---

## 3. 핵심 컴포넌트 설계

### 3.1 NeisSelectors — 셀렉터 단일 집중 (변경 위험 격리)

나이스 DOM이 바뀌면 **이 파일 하나만** 수정하면 되도록 모든 마법 문자열을 모은다.

```csharp
public static class NeisSelectors
{
    // §3.2 조회조건
    public const string SubjectCombo = "div[role='combobox'][aria-label^='교과,']";

    // §3.3 그리드
    public const string Grid = "div.cl-grid[role='grid'][aria-colcount='8']";
    public const string DataRow = "div.cl-grid-row[data-rowindex]";
    // 셀 인덱스: 1=번호 2=성명 3=영역 6=단계콤보 7=평가결과

    // §3.4 단계 콤보 (커스텀)
    public const string ComboInCell =
        "div[role='gridcell'][data-cellindex='6'] [role='combobox']";

    // §3.5 옵션 팝업 (열렸을 때만 존재)
    public const string OptionItem =
        ".cl-global-aside .cl-combobox-item[role='option']";

    // §3.6 프록시 스크롤바
    public const string VScroll =
        "div.cl-grid-detail-band .cl-blank .cl-scrollbar";
    public const string DetailBand = "div.cl-grid-detail-band";

    // §3.3 정규식 (Python과 동일 패턴)
    public static readonly Regex NoRe   = new(@"^\d+행 반/번호 (.+?)(?:\s|$)");
    public static readonly Regex NameRe = new(@"^\d+행 성명 (\S+)");
    public static readonly Regex AreaRe = new(@"^\d+행 영역 (.+?)$");
}

public static class Timings   // §4.3 실기기 검증 상수
{
    public static readonly TimeSpan AfterOptionClick = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan AfterScroll      = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan AfterScrollIntoView = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan PopupPollTimeout = TimeSpan.FromMilliseconds(2500);
    public static readonly TimeSpan PopupPollStep    = TimeSpan.FromMilliseconds(150);
}
```

### 3.2 INeisEngine — 엔진 인터페이스

```csharp
public interface INeisEngine
{
    Task LaunchEdgeAsync();                 // §6 ① 디버그 Edge 실행
    Task<bool> AttachAsync();               // CDP attach + neis 탭 선택
    bool Connected { get; }
    Task<string?> GetCurrentSubjectAsync(); // §3.2 화면 과목명

    Task<RunReport> RunSubjectAsync(
        SubjectSheet sheet,
        bool dryRun,
        IProgress<ProgressInfo> progress,
        CancellationToken ct);              // §4.1 한 과목 전체 흐름
}
```

- Python 의 `cancel_flag`(Event) → **`CancellationToken`** 으로 대체 (건 단위 협조적 취소).
- Python 의 `log` 콜백 → `IProgressSink` 또는 `IProgress<T>` 로 대체.

### 3.3 set_grade 신뢰성 루프 (§4.3 — 가장 중요)

Python `set_grade` 를 그대로 이식하되 async 화. 멱등성·재검증·1회 재시도를 유지한다.

```
for attempt in [1, 2]:
    combo ← EnsureRowVisibleAsync(idx)        // 없으면 예상위치 스크롤 → wheel 바닥
    if combo == null: return Fail("행 못 띄움")
    cur ← combo.InnerText (NBSP 제거·trim)
    if cur == target: return Ok("이미 설정됨")  // 멱등성
    (ok, why) ← ComboBoxDriver.OpenAndPickAsync(combo, target)
    if ok:
        combo2 ← GetFreshComboAsync(idx)       // ★ 참조 재획득 (stale 방지)
        if combo2.Text == target: return Ok()
        why = $"검증 불일치('{combo2.Text}')"
    if attempt == 1: await Task.Delay(400ms)
return Fail(why)
```

**불변 규칙 (§3.6):** 요소 참조를 보관하지 말고 조작 직전마다 셀렉터로 새로 찾는다.
Playwright 의 `ILocator` 는 지연 평가라 이 패턴에 유리 — 매 조작 시 `.First`/`.Nth(idx)` 재평가.

### 3.4 GridScroller — 마지막 행 이슈 해결부 (§8)

```csharp
// 1순위: trusted wheel (Playwright 네이티브)
public async Task WheelToBottomAsync(int steps)
{
    var band = _page.Locator(NeisSelectors.DetailBand).First;
    var box = await band.BoundingBoxAsync();
    await _page.Mouse.MoveAsync(box.X + box.Width/2, box.Y + box.Height/2);
    for (int i = 0; i < steps; i++)
    {
        await _page.Mouse.WheelAsync(0, 300);   // ← trusted 이벤트
        await Task.Delay(50);
    }
}

// 2순위 fallback: 프록시 스크롤바 scrollTop 직접 설정 (Python 방식)
public async Task ScrollProxyAsync(double top) { /* JS evaluate */ }
```

해결 순서(문서 §8.4): ① 배율 100% 안내 → ② trusted wheel → ③ CLX 내부 API →
④ 그래도 실패 시 "미처리 행"으로 리포트(현재 동작 유지).

---

## 4. 데이터 모델

```csharp
public record Student(string No, string Name, IReadOnlyDictionary<string,string> Grades);

public record SubjectSheet(string SubjectName, IReadOnlyList<string> Areas,
                           IReadOnlyList<Student> Students);

public record RowMeta(string? No, string? Name, string? Area);      // 화면 파싱

public record GradeTask(int RowIndex, string No, string Name,
                        string Area, string TargetGrade);

public record RunReport(
    IReadOnlyList<GradeTask> Done,
    IReadOnlyList<(string No,string Name,string Area,string Why)> Skipped,
    IReadOnlyList<(string No,string Name,string Area,string Why)> Failed,
    IReadOnlyList<int> Missing);
```

---

## 5. 안전장치 (§4.5 — C#에서도 전부 유지, 절대 삭제 금지)

| # | 장치 | 구현 위치 |
|---|---|---|
| 1 | 화면 과목 ≠ 대상 과목 → 실행 거부 | `RunSubjectAsync` 진입부 |
| 2 | dry-run 기본 + 실행 전 확인 대화상자 | `SubjectViewModel` + `DialogService` |
| 3 | 값 화이트리스트 {잘함,보통,노력요함} | `GradeValues` / `StudentMatcher` |
| 4 | 입력 후 재검증 + 1회 재시도 | `NeisEngine.SetGradeAsync` |
| 5 | **저장 버튼 미자동화** (사용자 수동) | 기본 정책 — 저장 코드 자체를 넣지 않음 |
| 6 | 매 건 로그 + 종료 리포트(성공/건너뜀/실패) | `IProgress` + `RunReport` |
| 7 | 멱등성 (이미 맞으면 건너뜀) | `SetGradeAsync` cur==target 분기 |

---

## 6. GUI 구성 (§6 → WPF 매핑)

```
┌ MainWindow ────────────────────────────────────────────────┐
│ [① NEIS 접속] [② 연결] ●상태  [③ 엑셀]  파일명            │  ← 상단 ToolBar
├────────────────────────────────────────────────────────────┤
│ TabControl(과목):  국어│사회│수학│실과│체육│음악│미술      │
│  ┌ SubjectTabView ──────────────────────────────────────┐  │
│  │ [검증(dry-run)] [▶ 입력(저장 수동)] [■ 중지]  영역:… │  │
│  │ DataGrid: 번호│이름│영역1│영역2│…                    │  │
│  └──────────────────────────────────────────────────────┘  │
├────────────────────────────────────────────────────────────┤
│ ProgressBar                                                 │
│ 로그 (TextBox, 실시간 [i/total] ✓/✗)                        │
└────────────────────────────────────────────────────────────┘
```

- 드래그앤드롭: WPF `AllowDrop=true` + `Drop` 이벤트 (Python tkinterdnd2 대체, 추가 패키지 불필요).
- 백그라운드 작업: `async` 커맨드 + `IProgress<T>` → UI 스레드 자동 마셜링 (queue 폴링 불필요).
- 중지: `CancellationTokenSource` (버튼이 `Cancel()` 호출).

---

## 7. 배포

- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
- Playwright 브라우저는 **불필요** — 사용자의 기존 Edge 에 CDP attach 하므로 번들 안 함.
- 최초 실행 시 `AppSettings.json` 생성 (Edge 경로/URL/프로필 폴더/배율 안내).

---

## 8. Python → C# 매핑 요약표

| Python | C# | 비고 |
|---|---|---|
| `NeisEngine` (Selenium) | `NeisEngine` (Playwright) | async 화 |
| `cancel_flag` (threading.Event) | `CancellationToken` | 협조적 취소 |
| `msg_q` queue 폴링 | `IProgress<T>` | UI 마셜링 자동 |
| `find_elements` + `is_displayed` | `ILocator` + `IsVisibleAsync` | 지연 평가 |
| `element.click()` (네이티브) | `locator.ClickAsync()` | JS 클릭 금지(§3.7) |
| `self.wheel()` (untrusted) | `Mouse.WheelAsync` (trusted) | §8 해결 |
| openpyxl | ClosedXML | data_only |
| Tkinter Notebook | WPF TabControl | MVVM |

---

## 9. 확장 설계 — 전체 파이프라인 (평가척도 · AI 생성기 · 서술문 업로드)

프로토타입은 "등급 드롭다운 입력"만 다뤘지만, 실제 목표는 **교과평가 전 과정의 자동화**다.
DooEval(GAS 웹앱)을 흡수해 하나의 파이프라인으로 통합한다.

### 9.1 통합 파이프라인

```
[1] 평가계획      영역·성취기준·평가기준내용        (엑셀 or 인앱 입력)
      │
[2] 성적 채점     학생 × 영역 → 등급 (사용자정의 척도)
      │
[3] AI 서술문 생성  교과학습발달상황 서술문 (Gemini)   ← DooEval 흡수
      │
[4] 나이스 자동입력
      ├ 4a. 등급 드롭다운  「교과별 평가」          ← 프로토타입 검증 완료
      └ 4b. 서술문 텍스트  「교과학습발달상황/세특」  ← ★ 최종 목표(신규)
```

- 데이터가 **C# 메모리 안에서** [2]→[3]→[4] 로 흐른다. 엑셀 왕복·화면 스크래핑 없음.
- 안전정책은 전 단계 동일: **저장은 사용자 수동**, dry-run 우선, 값 검증.

### 9.2 평가척도(GradeScale) — 사용자 정의 등급

`잘함/보통/노력요함` 3단계 하드코딩을 제거하고, 학교별로 다른 척도(4·5단계, 상/중/하 등)를
사용자가 정의·저장한다. **생성기와 입력기가 이 하나의 설정을 공유**한다.

```csharp
public record GradeLevel(
    string Label,          // 화면·엑셀 표기: "잘함" | "상" | "매우잘함"
    string? NeisOptionText,// 나이스 드롭다운 표시 텍스트 (보통 Label과 동일, 다르면 지정)
    string AiNuance);      // AI 서술 방향 지시문 (code.gs 하드코딩 분기를 데이터화)

public record GradeScale(
    string Name,                          // "3단계(잘함/보통/노력요함)" 등
    IReadOnlyList<GradeLevel> Levels);    // 순서 = 상위→하위

public interface IScaleStore                 // scales.json 프리셋 저장/로드/편집
{
    IReadOnlyList<GradeScale> Presets { get; }
    GradeScale Active { get; set; }
}
```

기본 프리셋(내장): `잘함/보통/노력요함`, `상/중/하`, `4단계`, `5단계` 예시.
편집 UI: WPF `ScaleEditorView` — 레벨 추가/삭제/순서변경 + 레벨별 나이스텍스트·AI뉘앙스 입력.

**동적 화이트리스트 (안전장치 재설계):** 고정 집합 대신 —
1. 목표값이 **활성 척도의 레벨**에 속하는지 검사 (엑셀 파싱 단계)
2. 입력 직전, **나이스 실제 드롭다운 옵션(§3.5)을 읽어** 목표 `NeisOptionText` 존재 확인
   → 없으면 "학교 나이스에 해당 등급 없음"으로 skip (오입력 원천 차단, §4.5 정신 유지)

영향 범위: `GradeValues`(제거→척도로 대체), `StudentMatcher`(척도 기반 검증),
`WorkbookLoader`(등급 감지를 척도 레벨 집합으로), AI 프롬프트(뉘앙스 주입).

### 9.3 AI 생성기 통합 — "C# UI + GAS 백엔드 API" (권장)

DooEval 재구현 방식 결정:

| 방식 | 채택 | 이유 |
|---|---|---|
| A. C# 완전 재구현 (직접 Gemini 호출) | 폴백 | 사용자 자체 키 입력 시 GAS 불필요. 프롬프트가 exe에 고정됨 |
| **B. C# UI + GAS 백엔드(REST)** | **★ 권장** | API 키 풀·프롬프트를 GAS에 유지(재배포 없이 수정), 서술문은 C#이 소유 |
| C. WebView로 GAS 화면 임베드 | 편의용만 | 서술문이 브라우저 블랙박스에 갇혀 4b(업로드)와 단절 → 통합 부적합 |

구현:
- `code.gs`에 **`doPost(e)` 추가** → `{name, subject, domains[], note, scale}` 수신,
  기존 `generateSubjectEvaluation` 로직 재사용하되 뉘앙스를 **요청의 scale에서** 주입.
- C# `AiGeneratorClient` : `HttpClient` 로 `주소.txt`의 `/exec` 에 POST → 서술문 수신.
- `IEvaluationGenerator` 인터페이스 뒤에 두 구현(GAS백엔드 / 직접Gemini) → 설정으로 전환.
- WebView2 는 `LegacyGeneratorView`(선택) 에서 GAS 원본 화면을 그대로 여는 용도로만.

```csharp
public interface IEvaluationGenerator
{
    Task<string> GenerateAsync(
        string studentName, string subject,
        IReadOnlyList<DomainPoint> domains, string? note,
        GradeScale scale, CancellationToken ct);
}
```

`DomainPoint`(영역명·등급·평가기준내용·성취기준) = Index.html의 `st.domains` 구조와 동형.
1단계 평가기준 파싱(Index.html `analyzeStep1File`)도 C# `PlanWorkbookLoader` 로 이식하여
`criteriaMap[영역_등급]` 을 C#에서 구성 → 생성 요청에 실어 보낸다.

### 9.4 서술문 나이스 자동 입력 (4b, 최종 목표)

생성된 교과학습발달상황 서술문을 나이스 화면에 자동 기입. 프로토타입이 다룬
「교과별 평가」와 **다른 화면**(교과학습발달상황/세부능력 및 특기사항)이므로,
§3 과 동일한 **DOM 실측 조사 절차**를 신규로 거쳐야 한다(셀렉터 미확정).

- 대상: 학생별 과목별 대용량 textarea. `[role='textbox']`/`textarea` 계열로 추정.
- 입력: Playwright `FillAsync`/`TypeAsync` (CLX 커스텀 에디터면 §3.4처럼 네이티브 조작 필요할 수 있음 — 조사 요).
- 안전장치 동일: 과목·학생 매칭 검증, dry-run, **저장 미자동화**, 재검증.
- 글자수 제한(나이스 바이트 제한) 사전 검사 + 초과 시 경고.

> 이 단계는 셀렉터가 실측 전이라 **불확실성이 가장 크다.** 로드맵 최후 단계로 배치하고,
> §3 을 만든 것과 같은 진단(diag_capture 상당)부터 시작한다.

### 9.5 확장된 솔루션 구조 (추가분)

```
src/
├─ NeisAutoFill.Core/
│   ├─ Scale/  GradeLevel, GradeScale, IScaleStore, ScaleStore(json)
│   └─ Models/ DomainPoint, EvaluationPlan (criteriaMap)
├─ NeisAutoFill.Generator/            # 신규
│   ├─ IEvaluationGenerator.cs
│   ├─ GasBackendGenerator.cs         # HttpClient → GAS doPost (권장 경로)
│   └─ DirectGeminiGenerator.cs       # 사용자 키 직접 호출 (폴백)
├─ NeisAutoFill.Excel/
│   └─ PlanWorkbookLoader.cs          # 1단계 평가기준 파싱 (Index.html 이식)
├─ NeisAutoFill.Automation/
│   └─ NarrativeWriter.cs             # 4b 서술문 입력 (셀렉터 실측 후)
└─ NeisAutoFill.App/
    ├─ Views/ ScaleEditorView, GeneratorView, LegacyGeneratorView(WebView2)
    └─ ...
```

추가 NuGet: `Microsoft.Web.WebView2`(선택), `System.Net.Http.Json`(Gemini/GAS 호출).
```
