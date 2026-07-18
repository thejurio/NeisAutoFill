# NEIS 교과평가 자동입력기 (NeisAutoFill)

[![CI](https://github.com/thejurio/NeisAutoFill/actions/workflows/ci.yml/badge.svg)](https://github.com/thejurio/NeisAutoFill/actions/workflows/ci.yml)

초등교사를 위한 4세대 나이스 교과평가 자동화 도구.
엑셀에 정리한 성적을 나이스 「교과별 평가」 등급 드롭다운에 자동 입력하고,
AI(Gemini)로 교과학습발달상황 서술문을 생성해 「학기말 종합의견」까지 자동 기입한다.

> 이 파일은 저장소의 **마스터 문서**다. 프로젝트 소개 + [문서 지도·분류 규칙](#문서) +
> 유지보수 진입점을 담는다. 세부 문서는 모두 [`docs/`](docs/) 아래에 종류별로 정리돼 있다.

## 주요 기능

- **등급 자동입력** — 학생×영역 등급을 화면 파싱 기반 매칭으로 정확히 입력 (저장은 사용자 수동)
- **AI 서술문 생성** — 평가계획서의 성취기준·평가기준과 교사 특기사항을 종합한 개조식 서술문
- **서술문 자동입력** — 생성 결과를 학기말 종합의견 칸에 자동 기입
- **평가척도 사용자 설정** — 3~7단계, 학교별 등급 이름 그대로 (잘함/보통/노력요함, 상/중/하 등)
- **양식 자동 생성** — 평가계획서 양식 → 성적입력 양식 (명단·영역 자동 세팅)
- **전국 지원** — 17개 시도 교육청 나이스 주소 선택
- **자동 저장/복원** — 생성된 서술문은 자동 저장되고 엑셀로도 내보내기 가능

## 안전 정책

- 나이스 [저장] 버튼은 절대 자동으로 누르지 않는다 — 최종 확정은 반드시 사람이
- 화면에 실제로 표시된 학생 이름·번호를 읽어 매칭 (추측 입력 없음)
- 입력 후 값 재검증 + 재시도, 실패는 사유와 함께 리포트

## 개발

```
dotnet build          # 전체 빌드
dotnet test           # 단위 테스트
dotnet run --project src/NeisAutoFill.App
```

구조: `Core`(도메인) / `Excel`(파서·양식) / `Automation`(Playwright 엔진) /
`Generator`(AI 연동) / `App`(WPF). 상세는 [docs/architecture/DESIGN_설계도.md](docs/architecture/DESIGN_설계도.md) 참고.

---

# 문서

모든 세부 문서는 [`docs/`](docs/) 아래 **종류별 폴더**로 정리한다. 루트에는 이 마스터(README)만 둔다.

## 문서 분류 규칙

| 폴더 | 성격 | 무엇을 넣나 | 수명 |
|---|---|---|---|
| `docs/guide/` | **사용자용** | 최종 사용자가 읽는 사용설명서·안내 | 기능 따라 갱신 |
| `docs/architecture/` | **설계·구조** | 아키텍처, 기술 선택, 파일별 역할, 개발 통합 문서 | 구조 바뀔 때 갱신 |
| `docs/roadmap/` | **계획·진행 추적** | 로드맵, 개선 계획, 리팩토링 계획 (상태 표기로 추적) | 진행 중 계속 갱신 |
| `docs/maintenance/` | **운영·유지보수 규칙** | 릴리스/배포 절차, 서버(GAS) 운영 규칙 | 절차 바뀔 때 갱신 |
| `docs/archive/` | **보관** | 낡았지만 이력으로 남기는 과거 기록·초기 노트 | 동결(수정 안 함) |

**넣을 때 판단 순서**: ① 사용자가 읽나? → `guide` · ② 어떻게 만들어졌나? → `architecture` ·
③ 앞으로 뭘 할지/진행 상황? → `roadmap` · ④ 어떻게 배포·운영하나? → `maintenance` · ⑤ 지난 기록? → `archive`.
새 문서는 루트가 아니라 해당 폴더에 만들고, 아래 인덱스에 한 줄 추가한다.

## 문서 인덱스

### guide — 사용자용
- [사용설명서.html](docs/guide/사용설명서.html) — 프로그램 내장 사용 설명서(HTML)

### architecture — 설계·구조
- [개발문서.md](docs/architecture/개발문서.md) — **최신 통합 개발 문서**(전체 구조·파일별 역할·연혁). 여기부터 보면 됨
- [DESIGN_설계도.md](docs/architecture/DESIGN_설계도.md) — 초기 C# 아키텍처 설계(기술 선택 근거)

### roadmap — 계획·진행 추적
- [개선로드맵.md](docs/roadmap/개선로드맵.md) — **현행 진행 추적 문서**. 작업 시 상태를 여기서 갱신
- [개선감사.md](docs/roadmap/개선감사.md) — 전 계층 감사(잘된 점·문제점·불편·추가 기능) + 조치 상태 추적
- [UX개선로드맵.md](docs/roadmap/UX개선로드맵.md) — v1.4.0 UI/UX 개편 계획
- [리팩토링계획.md](docs/roadmap/리팩토링계획.md) — 구조 부채 정리 계획(R1~R5)
- [ROADMAP_개발로드맵.md](docs/roadmap/ROADMAP_개발로드맵.md) — 최초 C# 이식 로드맵(Phase 0~)

### maintenance — 운영·유지보수 규칙
- [릴리스_배포.md](docs/maintenance/릴리스_배포.md) — **버전 올리기 → 빌드 → 게시 절차**, 자동업데이트 동작
- [GAS_서버.md](docs/maintenance/GAS_서버.md) — code.gs 재배포, APIKeys/RequestLog 시트, 로깅·키 규칙

### archive — 보관(동결)
- [PROJECT_STATUS.md](docs/archive/PROJECT_STATUS.md) — v1.0.0 초기 개발 현황(낡음)
- [보관_진단_검증도구.md](docs/archive/보관_진단_검증도구.md) — 진단·검증 도구 기록
- `NEIS_자동입력기_개발노트.md` — Python 프로토타입 원형 노트(로컬 전용, 저장소 제외)

---

# 배포 · 유지보수 요약

Releases 의 zip 을 받아 풀고 `NeisAutoFill.App.exe` 실행 (설치 불필요, .NET 포함).
새 버전이 나오면 프로그램이 시작할 때 자동으로 알려준다.

- **새 버전 내보내기**: 버전은 `src/NeisAutoFill.App/NeisAutoFill.App.csproj` 의 `<Version>` 한 곳만 올린다 →
  전체 절차는 [docs/maintenance/릴리스_배포.md](docs/maintenance/릴리스_배포.md).
- **AI 서버(GAS) 변경**: `code.gs` 는 저장소에서 제외돼 있고 Apps Script 콘솔에서 재배포한다 →
  [docs/maintenance/GAS_서버.md](docs/maintenance/GAS_서버.md).
- **커밋 규칙**: Conventional Commits, 한글, 현재형 (`feat: …추가`, `fix: …수정`, `chore: …`).
- **저장소 제외(.gitignore)**: `code.gs`, `주소.txt`, 개발노트, `publish/` — 릴리스에 섞지 않는다.
