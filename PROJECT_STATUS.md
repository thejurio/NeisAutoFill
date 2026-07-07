# NeisAutoFill — 개발 현황

최종 업데이트: 2026-07-07

## 완료 (빌드·테스트 통과, 앱 부팅 확인)

| Phase | 내용 | 상태 |
|---|---|---|
| 0 | .NET 8 솔루션 스캐폴딩, 6개 프로젝트, 셀렉터/타이밍 상수 | ✅ |
| 1 | Core 모델 + 매칭/정규화 + Excel 파서(ClosedXML) | ✅ |
| 1.5 | 평가척도(GradeScale) 사용자 설정, 동적 화이트리스트 | ✅ |
| doPost | code.gs REST 엔드포인트 (외부 HTTP 호출용) | ✅ (코드) |
| 2-3 | Playwright 브라우저 엔진 (attach/파싱/행지도/콤보/set_grade) | ✅ (코드) |
| 4 | WPF GUI (MVVM, 탭/DataGrid/진행바/로그/DnD/척도선택) | ✅ |
| 7 | AI 서술문 생성기 통합 (PlanWorkbookLoader/EvaluationAssembler/GAS·Gemini 생성기/GeneratorWindow) | ✅ (코드) |
| 8 | 서술문 나이스 입력 (DomInspector 진단도구/NarrativeWriter/바이트제한/GUI 업로드) | ✅ (코드, **셀렉터 잠정**) |
| UI | 평가척도 전용 설정 창 (단계 수·이름 직접 정의, 프리셋은 불러오기로 강등) | ✅ |

- 단위 테스트 **43개 전부 통과** (정규화·매칭·척도·엑셀·aria-label·평가계획·도메인 조립·프롬프트·서술문 매칭·바이트)
- 전체 솔루션 빌드 클린 (경고 0), WPF 앱 부팅 확인
- 규모: 소스 36파일 / 약 1,488라인

## 검증 남음 (실기기·외부 필요)

- **엔진 실동작**: 실제 나이스 세션에서 attach→파싱→입력. 코드는 §3~4 검증 알고리즘 이식했으나
  live 테스트 미실시. `dotnet run` 후 ① Edge 실행 → 로그인/조회 → ② 연결 → ③ 엑셀 → 검증(dry-run)부터.
- **doPost**: GAS 웹앱 **재배포 필수** — 현재 배포본에 POST 시 HTML 오류 페이지 확인(2026-07-07).
  Apps Script 편집기에서 로컬 code.gs 반영 → 배포 관리 → 새 버전 → "액세스: 모든 사용자".
  재배포로 URL이 바뀌면 생성기 창 ⚙ 설정에서 GAS URL 갱신.
- **AI 생성 실동작**: GAS 재배포 후 생성기 창에서 실제 생성 1건 테스트 필요.
  (폴백: ⚙ 설정에서 GAS 체크 해제 + 본인 Gemini API 키 입력 시 GAS 없이 동작)

- **Phase 8 셀렉터 확정 (최우선 실측)**: NarrativeWriter 의 셀렉터는 CLX 공통 패턴 유추(잠정).
  절차: 나이스에서 교과학습발달상황(서술문 입력) 화면을 띄움 → 앱 [🔎 화면 진단] 클릭 →
  `%AppData%\NeisAutoFill\dom_inspect_*.txt` 확인 → `NarrativeSelectors`(NarrativeWriter.cs 상단)만 수정.
  진단 리포트를 Claude 에게 주면 셀렉터 확정 가능.
  주의: 실운영 전 반드시 [검증(dry-run)] 먼저. textarea 가 read-only 이거나 CLX 전용 에디터면
  FillAsync 가 모델에 반영 안 될 수 있음 — 검증 불일치로 안전하게 실패 처리됨.

## ★ §8 "마지막 행 미렌더링" 진짜 원인 규명 (2026-07-07)

개발노트 §8 의 가설(가상 스크롤 경계 문제)은 **틀렸다**. 실기기 DOM 덤프로 확정한 진실:
- 마지막 행은 프록시 스크롤 바닥에서 **항상 정상 렌더링된다**.
- 단, 마지막 행만 aria-label 에 **'마지막 행' 토큰이 추가**된다:
  일반 행 `"17행 성명 …"` vs 마지막 행 `"17행 마지막 행 성명 …"`
- 프로토타입부터 써온 정규식 `^\d+행 성명 …` 이 이를 못 읽어 행 지도에서 빠졌던 것.
- 수정: 정규식 3종에 `(?:마지막 행 )?` 추가 (NeisSelectors + NarrativeWriter JS). 회귀 테스트 포함.
- 부수 확보: CLX 공식 API `reveal(rowIndex)` 경로(ClxGridApi) — 진짜 미렌더 상황 대비 폴백으로 유지.

## 프로젝트 구조

```
NeisAutoFill.sln
├─ src/NeisAutoFill.Core         모델·매칭·척도 (의존성 0, 테스트 완비)
├─ src/NeisAutoFill.Excel        WorkbookLoader (ClosedXML)
├─ src/NeisAutoFill.Automation   NeisEngine·GridScroller·ComboBoxDriver·RowMapBuilder
├─ src/NeisAutoFill.Generator    IEvaluationGenerator·GasBackend·DirectGemini·PromptBuilder
├─ src/NeisAutoFill.App          WPF MVVM (MainWindow + GeneratorWindow)
└─ tests/NeisAutoFill.Tests      xUnit 37개
```

## 실행 방법

```
dotnet build
dotnet test                              # 26개 통과
dotnet run --project src/NeisAutoFill.App
```

설정 파일: `%AppData%\NeisAutoFill\scales.json` (평가척도 프리셋·활성 선택 저장)
```
