using System.Text.RegularExpressions;

namespace NeisAutoFill.Automation;

/// <summary>
/// 나이스(4세대) 교과별 평가 화면의 검증된 CSS 셀렉터·정규식 모음.
/// 개발노트 §3 의 실측 사실을 단일 지점에 집중시켜, DOM 변경 시 이 파일만 고치면 되도록 한다.
/// </summary>
public static partial class NeisSelectors
{
    // §3.2 조회조건 — 현재 과목 콤보 (aria-label "교과, 국어")
    public const string SubjectCombo = "div[role='combobox'][aria-label^='교과,']";

    // §3.3 평가 그리드 (성취기준별 탭은 colcount=5 라 8 로 구분)
    public const string Grid = "div.cl-grid[role='grid'][aria-colcount='8']";
    public const string DataRow = "div.cl-grid-row[data-rowindex]";
    // 셀 인덱스: 0=체크박스 1=반/번호 2=성명 3=영역 4=성취기준 5=평가요소 6=단계콤보 7=평가결과
    public const string GridCell = "div[role='gridcell']";

    // §3.4 단계 콤보박스 (커스텀, <select> 아님). 셀 6 내부의 role=combobox 요소.
    public const string ComboInCell =
        "div[role='gridcell'][data-cellindex='6'] [role='combobox']";

    // §3.5 옵션 팝업 — 열렸을 때만 cl-global-aside 안에 존재
    public const string OptionItem =
        ".cl-global-aside .cl-combobox-item[role='option']";
    public const string OptionText = "div.cl-text";

    // §3.6 가상 스크롤 — 우측 프록시 스크롤바 / 휠 대상 디테일 밴드
    public const string VScroll =
        "div.cl-grid-detail-band .cl-blank .cl-scrollbar";
    public const string DetailBand = "div.cl-grid-detail-band";

    /// <summary>지정 rowindex 행의 단계 콤보 셀렉터를 만든다 (§4.3 조작 직전 fresh 획득용).</summary>
    public static string ComboForRow(int rowIndex) =>
        $"div.cl-grid-row[data-rowindex='{rowIndex}'] " +
        "div[role='gridcell'][data-cellindex='6'] [role='combobox']";

    // ── Phase 5.5 전과목 자동 업로드 (★ 잠정 — 실기기 [🔎 진단] 덤프로 확정할 것) ──
    // CLX 버튼은 div[role='button'] + aria-label 패턴. 엔진은 접근성 이름으로 탐색한다.
    public const string AnyButton = "[role='button'], button";
    /// <summary>[조회]/[저장] 버튼의 접근성 이름 (aria-label 또는 표시 텍스트에 포함).</summary>
    public const string QueryButtonName = "조회";
    public const string SaveButtonName = "저장";
    /// <summary>저장 확인·완료 대화상자 안의 버튼 (잠정 — CLX 다이얼로그 컨테이너 후보들).</summary>
    public const string DialogButton =
        ".cl-dialog [role='button'], .cl-dialog button, " +
        "[role='dialog'] [role='button'], [role='dialog'] button, " +
        ".cl-alert [role='button'], .cl-alert button, " +
        ".cl-messagebox [role='button'], .cl-messagebox button";
    /// <summary>대화상자 안 긍정 버튼 이름 후보.</summary>
    public static readonly string[] DialogYesNames = { "확인", "예", "저장" };

    // §3.3 aria-label 정규식. 행마다 번호/성명/영역이 개별 기재됨.
    // ★ 마지막 행만 "N행 마지막 행 성명 …" 처럼 '마지막 행' 토큰이 추가된다
    //   (2026-07-07 실기기 덤프로 확정 — §8 '미렌더링'의 진짜 원인은 이 인식 실패였음).
    [GeneratedRegex(@"^\d+행 (?:마지막 행 )?반/번호 (.+?)(?:\s|$)")]
    public static partial Regex NoRegex();

    [GeneratedRegex(@"^\d+행 (?:마지막 행 )?성명 (\S+)")]
    public static partial Regex NameRegex();

    [GeneratedRegex(@"^\d+행 (?:마지막 행 )?영역 (.+?)$")]
    public static partial Regex AreaRegex();
}

/// <summary>§4.3 실기기에서 안정 동작이 확인된 타이밍 상수.</summary>
public static class Timings
{
    public static readonly TimeSpan AfterOptionClick = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan AfterScroll = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan AfterScrollIntoView = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan PopupPollTimeout = TimeSpan.FromMilliseconds(2500);
    public static readonly TimeSpan PopupPollStep = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan WheelStep = TimeSpan.FromMilliseconds(50);
}
