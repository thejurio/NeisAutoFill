# GAS 서버 (code.gs) 운영 규칙

프로그램의 AI 기능(서술문 생성·평가계획 인식)은 **Google Apps Script 웹앱**을 프록시로 쓴다.
Gemini API 키는 서버(GAS)가 시트에서 꺼내 호출하므로, 프로그램 사용자는 키를 알 필요도 입력할 필요도 없다.

> `code.gs` 는 저장소에서 제외(.gitignore)돼 로컬(`C:\autoclick\code.gs`)에만 있다.
> 배포는 Git이 아니라 **Apps Script 콘솔에서** 한다.

---

## 1. 어느 Apps Script 프로젝트를 고치나

키 관리 시트에 붙은 스크립트가 아니라, **프로그램이 실제로 호출하는 웹앱(doPost가 있는 프로젝트)** 을 고친다.

- 시트(APIKeys·RequestLog)는 코드가 **ID로 열어서**(`SpreadsheetApp.openById(...)`) 접근한다 → 스크립트가 어느 시트에 "붙어" 있는지는 무관하다.
- **맞는 프로젝트 판별법**: 편집기 코드 안에 `function doPost(e)` 와 `function generateSubjectEvaluation(...)` 이 있고, [배포 관리]의 웹앱 URL이 프로그램이 쓰는 주소(`주소.txt`, `.../AKfycbw…/exec`)와 같은 프로젝트.

## 2. 재배포 절차

1. 로컬 `code.gs` 전체를 Apps Script 편집기에 붙여넣기
2. **[배포] → [배포 관리] → (기존 배포) 편집 → 버전: 새 버전** 선택 후 배포
3. **URL은 그대로 유지** — 새 배포를 만들면 URL이 바뀌어 프로그램이 못 찾는다

> 프로그램 릴리스와 GAS 배포는 별개다. 서버 로직만 바꿨으면 프로그램 재빌드 없이 GAS만 재배포하면 된다.

---

## 3. 스프레드시트 구조

한 스프레드시트(`LOG_SPREADSHEET_ID = 1Uu_krt08KK…`)에 두 시트가 있다.

### APIKeys 시트 — 키 보관·상태
| 열 | 내용 |
|---|---|
| A | API Key |
| B | 상태 (사용가능 / 한도초과) |
| C | 마지막 사용 시각 |

- **키는 오직 이 시트에서 관리**한다. 프로그램 안에 키 입력 UI를 두지 않는다. 키 발급·추가 경로는 GAS(시트) 단일.
- 로테이션은 서버가 처리: 가장 오래 안 쓴 사용가능 키를 골라 쓰고, 403/429가 나면 그 키를 `한도초과`로 표시(`_markExhausted`)한 뒤 다음 키로 자동 폴백.

### RequestLog 시트 — 사용 기록
| 열 | 내용 | 예 |
|---|---|---|
| A | 시각 | 2026-07-17 … |
| B | action | generateBatch / parsePlan / startup |
| C | 결과 | SUCCESS / FAIL / ERROR |
| D | client | NeisAutoFill (사용자명) |
| E | 상세(어디에 썼는지) | `국어 24명 · 수학 20명`, `11과목 인식` |
| **F** | **사용한 키(뒤 4자리)** | `a1b2` 또는 `a1b2,c3d4` |

---

## 4. 로깅 규칙 (지저분하지 않게 — 요청당 1건)

학생·과목마다 한 줄씩 남기지 않는다. **논리적 요청 하나에 요약 1건**만.

| action | 언제 | 건수 | F열 |
|---|---|---|---|
| `startup` | 앱 시작 | 실행당 1건 | 없음 |
| `generateBatch` | 서술문 생성 **배치 완료 시** | 배치당 1건 | 배치에서 실제로 쓴 키(들) |
| `parsePlan`(logPlanImport) | 평가계획 인식 **전체 완료 시** | 가져오기당 1건 | 인식에 쓴 키(들) |
| `generate` / `parsePlan` FAIL | 개별 실패 | 실패 건만 | 상황에 따라 |

- 평가계획 인식 요약: 전부 성공이면 `N과목 인식`, 일부 실패면 `M/N 인식, k번째 실패`.
- **키를 F열에 남기는 구조**: 로테이션은 서버 안에서만 일어나 클라이언트(C#)는 어떤 키를 썼는지 모른다.
  그래서 GAS가 `_withKeyRotation` 이 성공한 키의 뒤 4자리를 응답(`keyHint`)에 실어 돌려주고,
  C#이 배치/인식 동안 모은 키들을 요약 로그의 F열로 보낸다. 여러 키면 쉼표로 합치고 중복 제거.

## 5. doPost 액션 목록

| action | 용도 |
|---|---|
| `startup` | 실행 기록 |
| `logBatch` | 생성 배치 사용 기록 (info, keyHint) |
| `logPlanImport` | 평가계획 인식 결과 요약 기록 (result, info, keyHint) |
| `listPlanSubjects` | 2단계 파싱 1차 — 문서의 과목 목록만 인식 |
| `parsePlan` | 평가계획 상세 인식 (onlySubject 지정 시 그 과목만) |
| (그 외) | 서술문 생성 |

- 모델: 인식은 `gemini-3.1-flash-lite`(404면 `gemini-3.5-flash` 폴백), 생성은 `GEMINI_MODEL`.
- 2단계 파싱(목록 → 과목별)은 대형 문서에서 출력이 잘리지 않게 과목당 응답을 작게 유지하려는 것.
