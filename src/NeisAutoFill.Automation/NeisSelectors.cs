using System.Text.RegularExpressions;

namespace NeisAutoFill.Automation;

/// <summary>
/// 나이스(4세대) 교과별 평가 화면의 검증된 CSS 셀렉터·정규식 모음.
/// 개발노트 §3 의 실측 사실을 단일 지점에 집중시켜, DOM 변경 시 이 파일만 고치면 되도록 한다.
///
/// ★ 원격 구성(F14): 나이스가 UI 를 개편하면 앱 재배포 없이 GAS 에서 셀렉터만 갱신해 대응할 수 있도록,
///   값을 런타임 오버라이드 가능한 프로퍼티로 둔다. 기본값 = 아래 상수(현재 검증값)이며,
///   <see cref="ApplyRemote"/> 로 원격 값을 덮어쓴다(검증 통과분만). 원격이 없으면 기본값 그대로 동작.
/// </summary>
public static class NeisSelectors
{
    // ── 기본값 (현재 검증된 값 — 원격이 없으면 이대로) ──
    private const string DefaultSubjectCombo = "div[role='combobox'][aria-label^='교과,']";
    private const string DefaultGrid = "div.cl-grid[role='grid'][aria-colcount='8']";
    private const string DefaultDataRow = "div.cl-grid-row[data-rowindex]";
    private const string DefaultGridCell = "div[role='gridcell']";
    private const string DefaultComboInCell = "div[role='gridcell'][data-cellindex='6'] [role='combobox']";
    private const string DefaultOptionItem = ".cl-global-aside .cl-combobox-item[role='option']";
    private const string DefaultOptionText = "div.cl-text";
    private const string DefaultVScroll = "div.cl-grid-detail-band .cl-blank .cl-scrollbar";
    private const string DefaultDetailBand = "div.cl-grid-detail-band";
    private const string DefaultAnyButton = "[role='button'], button";
    private const string DefaultQueryButtonName = "조회";
    private const string DefaultSaveButtonName = "저장";
    private const string DefaultDialogButton =
        ".cl-dialog [role='button'], .cl-dialog button, " +
        "[role='dialog'] [role='button'], [role='dialog'] button, " +
        ".cl-alert [role='button'], .cl-alert button, " +
        ".cl-messagebox [role='button'], .cl-messagebox button";
    private const string DefaultDisabledClass = "cl-disabled";
    private const string DefaultNoPattern = @"^\d+행 (?:마지막 행 )?반/번호 (.+?)(?:\s|$)";
    private const string DefaultNamePattern = @"^\d+행 (?:마지막 행 )?성명 (\S+)";
    private const string DefaultAreaPattern = @"^\d+행 (?:마지막 행 )?영역 (.+?)$";

    // ── 현재 값 (기본값으로 초기화, ApplyRemote 로 덮어쓸 수 있음) ──

    // §3.2 조회조건 — 현재 과목 콤보 (aria-label "교과, 국어")
    public static string SubjectCombo { get; private set; } = DefaultSubjectCombo;
    // §3.3 평가 그리드 (성취기준별 탭은 colcount=5 라 8 로 구분)
    public static string Grid { get; private set; } = DefaultGrid;
    public static string DataRow { get; private set; } = DefaultDataRow;
    // 셀 인덱스: 0=체크박스 1=반/번호 2=성명 3=영역 4=성취기준 5=평가요소 6=단계콤보 7=평가결과
    public static string GridCell { get; private set; } = DefaultGridCell;
    // §3.4 단계 콤보박스 (커스텀, <select> 아님). 셀 6 내부의 role=combobox 요소.
    public static string ComboInCell { get; private set; } = DefaultComboInCell;
    // §3.5 옵션 팝업 — 열렸을 때만 cl-global-aside 안에 존재
    public static string OptionItem { get; private set; } = DefaultOptionItem;
    public static string OptionText { get; private set; } = DefaultOptionText;
    // §3.6 가상 스크롤 — 우측 프록시 스크롤바 / 휠 대상 디테일 밴드
    public static string VScroll { get; private set; } = DefaultVScroll;
    public static string DetailBand { get; private set; } = DefaultDetailBand;

    /// <summary>지정 rowindex 행의 단계 콤보 셀렉터를 만든다 (§4.3 조작 직전 fresh 획득용).</summary>
    public static string ComboForRow(int rowIndex) =>
        $"div.cl-grid-row[data-rowindex='{rowIndex}'] " +
        "div[role='gridcell'][data-cellindex='6'] [role='combobox']";

    // ── Phase 5.5 전과목 자동 업로드 (✔ 2026-07-17 실기기 진단 덤프로 확정) ──
    public static string AnyButton { get; private set; } = DefaultAnyButton;
    /// <summary>[조회]/[저장] 버튼의 접근성 이름 (aria-label 또는 표시 텍스트에 포함).</summary>
    public static string QueryButtonName { get; private set; } = DefaultQueryButtonName;
    public static string SaveButtonName { get; private set; } = DefaultSaveButtonName;
    /// <summary>저장 확인·완료 대화상자 안의 버튼.</summary>
    public static string DialogButton { get; private set; } = DefaultDialogButton;
    /// <summary>대화상자 안 긍정 버튼 이름 후보 (실측: '확인').</summary>
    public static string[] DialogYesNames { get; private set; } = { "확인", "예", "저장" };
    /// <summary>CLX 비활성 버튼 표시 클래스 — 저장할 변경이 없으면 [저장]에 붙는다.</summary>
    public static string DisabledClass { get; private set; } = DefaultDisabledClass;

    // §3.3 aria-label 정규식 (런타임 — 원격 패턴으로 교체 가능). 행마다 번호/성명/영역이 개별 기재됨.
    // ★ 마지막 행만 "N행 마지막 행 성명 …" 처럼 '마지막 행' 토큰이 추가된다.
    public static Regex NoRegex { get; private set; } = Compile(DefaultNoPattern);
    public static Regex NameRegex { get; private set; } = Compile(DefaultNamePattern);
    public static Regex AreaRegex { get; private set; } = Compile(DefaultAreaPattern);

    private static Regex Compile(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 원격 셀렉터 구성 적용 (F14). 알려진 키만, 검증 통과분만 덮어쓴다.
    /// 잘못된 값(빈 셀렉터·컴파일 불가 정규식)은 무시하고 기존값 유지 → 잘못된 원격이 앱을 망가뜨리지 않음.
    /// 반환: 실제로 적용된 키 수.
    /// </summary>
    public static int ApplyRemote(IReadOnlyDictionary<string, string> values)
    {
        int applied = 0;
        foreach (var (key, raw) in values)
        {
            var v = raw?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            switch (key)
            {
                case nameof(SubjectCombo): SubjectCombo = v; applied++; break;
                case nameof(Grid): Grid = v; applied++; break;
                case nameof(DataRow): DataRow = v; applied++; break;
                case nameof(GridCell): GridCell = v; applied++; break;
                case nameof(ComboInCell): ComboInCell = v; applied++; break;
                case nameof(OptionItem): OptionItem = v; applied++; break;
                case nameof(OptionText): OptionText = v; applied++; break;
                case nameof(VScroll): VScroll = v; applied++; break;
                case nameof(DetailBand): DetailBand = v; applied++; break;
                case nameof(AnyButton): AnyButton = v; applied++; break;
                case nameof(QueryButtonName): QueryButtonName = v; applied++; break;
                case nameof(SaveButtonName): SaveButtonName = v; applied++; break;
                case nameof(DialogButton): DialogButton = v; applied++; break;
                case nameof(DisabledClass): DisabledClass = v; applied++; break;
                case nameof(DialogYesNames):
                    var names = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (names.Length > 0) { DialogYesNames = names; applied++; }
                    break;
                case nameof(NoRegex): if (TryCompile(v, out var r1)) { NoRegex = r1; applied++; } break;
                case nameof(NameRegex): if (TryCompile(v, out var r2)) { NameRegex = r2; applied++; } break;
                case nameof(AreaRegex): if (TryCompile(v, out var r3)) { AreaRegex = r3; applied++; } break;
                // 알 수 없는 키는 무시
            }
        }
        return applied;
    }

    /// <summary>정규식이 컴파일 가능하고 캡처 그룹이 1개 이상이어야 유효 (파싱 결과를 뽑아야 하므로).</summary>
    private static bool TryCompile(string pattern, out Regex regex)
    {
        try
        {
            var r = Compile(pattern);
            if (r.GetGroupNumbers().Length >= 2) { regex = r; return true; }   // 그룹 0(전체)+최소 1개
        }
        catch (Exception) { /* 잘못된 패턴 무시 */ }
        regex = null!;
        return false;
    }
}

/// <summary>§4.3 실기기에서 안정 동작이 확인된 타이밍 상수.
/// 기본(배율 1.0)이 검증된 최고 속도 — 설정의 자동클릭 속도(보통/느림)는 배율만 올린다 (느린 PC 안정용).</summary>
public static class Timings
{
    private static double _multiplier = 1.0;

    /// <summary>"fast"=1.0(기본) / "normal"=1.6 / "slow"=2.5</summary>
    public static void SetSpeed(string speed) => _multiplier = speed switch
    {
        "slow" => 2.5,
        "normal" => 1.6,
        _ => 1.0,
    };

    public static TimeSpan AfterOptionClick => Ms(300);
    public static TimeSpan AfterScroll => Ms(400);
    public static TimeSpan AfterScrollIntoView => Ms(200);
    public static TimeSpan RetryDelay => Ms(400);
    public static TimeSpan PopupPollTimeout => Ms(2500);
    public static TimeSpan PopupPollStep => Ms(150);
    public static TimeSpan WheelStep => Ms(50);

    private static TimeSpan Ms(int baseMs) => TimeSpan.FromMilliseconds(baseMs * _multiplier);
}
