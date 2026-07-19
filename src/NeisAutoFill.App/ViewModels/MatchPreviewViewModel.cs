using System.Collections.ObjectModel;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.App.ViewModels;

/// <summary>
/// 입력 전 매칭 미리보기 — 과목·학생·영역 불일치를 한 화면에서 사용자 결정으로 해소.
/// 결과는 Decision (null = 취소).
/// </summary>
public sealed class MatchPreviewViewModel : ObservableObject
{
    public const string ExcludeLabel = "입력 안 함";

    private readonly MatchAnalyzer.Issues _issues;
    private readonly SubjectSheet _sheet;

    public MatchPreviewViewModel(MatchAnalyzer.Issues issues, SubjectSheet sheet,
        IReadOnlyDictionary<string, string>? presetNames = null)
    {
        _issues = issues;
        _sheet = sheet;

        // ── 과목 불일치 배너 ──
        SubjectWarning = issues.SubjectMismatch
            ? $"화면 과목은 '{issues.ScreenSubject}'인데 입력 대상은 '{sheet.SubjectName}'입니다."
            : "";

        // ── 학생 확인 (화면에 있는데 내 자료에 없는 학생) ──
        // 후보 = 자동 매칭 안 된 '남은 엑셀 이름'만 (이미 화면에서 찾은 이름은 제외).
        // 남은 후보가 없으면(구버전·계산 실패) 전체 명단으로 폴백.
        var candidates = issues.UnmatchedExcelStudents.Count > 0
            ? issues.UnmatchedExcelStudents
            : sheet.Students.Select(s => s.Name).ToList();
        bool allPreset = issues.UnmatchedStudents.Count > 0;
        foreach (var (no, name) in issues.UnmatchedStudents)
        {
            // 이전 과목의 결정(presetNames)이 있으면 초기값으로. 빈 값("")도 '입력 안 함'으로 이미 결정된 것.
            string? preset = null;
            if (presetNames is not null && presetNames.TryGetValue(name, out var pv))
                preset = pv == "" ? ExcludeLabel : (candidates.Contains(pv) ? pv : null);
            if (preset is null) allPreset = false;
            var selected = preset ?? SimilaritySuggester.Suggest(name, candidates.ToList()) ?? ExcludeLabel;
            var item = new MapItem($"{no}번 {name}", name, candidates,
                selected, suggested: selected != ExcludeLabel, fromPreset: preset is not null);
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MapItem.Selected)) RecomputeStudentOptions(); };
            StudentMaps.Add(item);
        }
        RecomputeStudentOptions();   // 초기 선택(자동제안·캐시) 반영해 다른 행에서 비활성화
        // 학생 이름이 전부 이전 매핑으로 채워졌으면 학생 섹션은 숨기고 영역만 보여준다(창 분리 느낌)
        AllNamesPreset = allPreset;

        // ── 영역 확인 — 화면 순서대로 "어떤 영역의 성적을 넣을지" 하나씩 고른다 ──
        var areaOptions = sheet.Areas.ToList();

        var assigned = SimilaritySuggester.AssignAreasByOrder(
            issues.ScreenAreas.Cast<string?>().ToList(), issues.RowsPerStudent, sheet.Areas);
        for (int i = 0; i < assigned.Count; i++)
        {
            var screenArea = i < issues.ScreenAreas.Count ? issues.ScreenAreas[i] : "(이름 확인 안 됨)";
            OrderMaps.Add(new MapItem($"화면 {i + 1}번째 · {screenArea}", screenArea,
                areaOptions, assigned[i].Area ?? ExcludeLabel, assigned[i].AutoPicked));
        }

        HasStudentIssues = StudentMaps.Count > 0;
        HasAreaIssues = issues.UnmatchedAreas.Count > 0 || issues.DuplicateAreas || issues.AreaCountMismatch;
    }

    /// <summary>학생 이름이 전부 이전 매핑(캐시)으로 채워졌나 — 이땐 학생 섹션을 숨긴다.</summary>
    public bool AllNamesPreset { get; }
    /// <summary>학생 이름 매핑이 필요한가 — 이름 이슈가 있고, 전부 캐시로 채워진 게 아닐 때.</summary>
    public bool ShowStudentSection => HasStudentIssues && !AllNamesPreset;

    // ── 창 분리: 학생 이름 매핑창 → (필요시) 영역 매핑창 을 순차로 보여준다 ──
    public enum Step { Student, Area }
    private Step _step = Step.Student;
    public Step CurrentStep
    {
        get => _step;
        set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(StudentSectionVisible));
            OnPropertyChanged(nameof(AreaSectionVisible));
            OnPropertyChanged(nameof(SubjectWarningVisible));
            OnPropertyChanged(nameof(StepTitle));
        }
    }
    /// <summary>이 단계에서 학생 섹션을 그릴지.</summary>
    public bool StudentSectionVisible => _step == Step.Student && ShowStudentSection;
    /// <summary>이 단계에서 영역 섹션을 그릴지.</summary>
    public bool AreaSectionVisible => _step == Step.Area && HasAreaIssues;
    /// <summary>과목 경고 배너는 먼저 뜨는 창에만 (학생창 있으면 학생창, 없으면 영역창).</summary>
    public bool SubjectWarningVisible => HasSubjectWarning &&
        ((_step == Step.Student && ShowStudentSection) || (_step == Step.Area && !ShowStudentSection));
    public string StepTitle => _step == Step.Student
        ? "입력 전 확인 — 학생 이름 매칭"
        : "입력 전 확인 — 평가 영역 매칭";

    /// <summary>다른 행에서 이미 고른 엑셀 이름은 이 행 후보에서 비활성(회색). '입력 안 함'·자기 선택값은 항상 활성.</summary>
    private void RecomputeStudentOptions()
    {
        foreach (var row in StudentMaps)
        {
            var takenByOthers = StudentMaps
                .Where(r => !ReferenceEquals(r, row) && r.Selected != ExcludeLabel)
                .Select(r => r.Selected).ToHashSet();
            foreach (var opt in row.Options)
                opt.IsEnabled = opt.Name == ExcludeLabel || opt.Name == row.Selected || !takenByOthers.Contains(opt.Name);
        }
    }

    public string SubjectWarning { get; }
    public bool HasSubjectWarning => SubjectWarning != "";
    public bool HasStudentIssues { get; }
    public bool HasAreaIssues { get; }

    private bool _subjectAccepted;
    public bool SubjectAccepted { get => _subjectAccepted; set => SetProperty(ref _subjectAccepted, value); }

    public ObservableCollection<MapItem> StudentMaps { get; } = new();
    public ObservableCollection<MapItem> OrderMaps { get; } = new();

    /// <summary>현재 단계 검증. 문제가 있으면 사유 반환.</summary>
    public string? Validate()
    {
        if (SubjectWarningVisible && !SubjectAccepted)
            return "과목이 다릅니다. 계속하려면 아래 확인란에 체크하세요.";
        if (_step != Step.Area) return null;   // 학생 단계는 하드 검증 없음('입력 안 함' 허용)

        if (OrderMaps.All(m => m.Selected == ExcludeLabel))
            return "넣을 영역이 하나도 선택되지 않았습니다. 최소 한 줄은 영역을 골라 주세요.";
        // 같은 영역을 두 줄에 고르면 오입력
        var dup = OrderMaps.Where(m => m.Selected != ExcludeLabel)
            .GroupBy(m => m.Selected).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            return $"'{dup.Key}' 영역이 두 곳에 선택되어 있습니다. 한 곳만 남기고 나머지는 다른 영역이나 '입력 안 함'으로 바꿔 주세요.";
        return null;
    }

    /// <summary>사용자 선택 → 엔진 결정으로 변환.</summary>
    public MatchDecision BuildDecision()
    {
        var nameMap = StudentMaps.ToDictionary(
            m => m.SourceKey,
            m => m.Selected == ExcludeLabel ? "" : m.Selected);

        if (HasAreaIssues)
        {
            var ordered = OrderMaps.Select(m => m.Selected == ExcludeLabel ? "" : m.Selected).ToList();
            return new MatchDecision(StudentMatcher.MatchMode.ByOrder,
                NameMap: nameMap.Count > 0 ? nameMap : null,
                OrderedExcelAreas: ordered);
        }
        return new MatchDecision(StudentMatcher.MatchMode.ByName,
            NameMap: nameMap.Count > 0 ? nameMap : null);
    }

    // 유사도·기본값 선택 로직은 Core/Matching/SimilaritySuggester (순수, 테스트됨)
}

/// <summary>콤보 옵션 한 개 — 이름 + 활성 여부(다른 행에서 이미 고르면 회색 비활성).</summary>
public sealed class OptionItem : ObservableObject
{
    public OptionItem(string name) => Name = name;
    public string Name { get; }
    private bool _isEnabled = true;
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
}

/// <summary>매핑 한 줄: 화면 항목 → 엑셀 항목 선택.</summary>
public sealed class MapItem : ObservableObject
{
    public MapItem(string display, string sourceKey, IReadOnlyList<string> options, string selected, bool suggested, bool fromPreset = false)
    {
        Display = display;
        SourceKey = sourceKey;
        Options = new ObservableCollection<OptionItem>(
            new[] { MatchPreviewViewModel.ExcludeLabel }.Concat(options).Distinct().Select(o => new OptionItem(o)));
        _selected = selected;
        Suggested = suggested;
        FromPreset = fromPreset;
    }

    public string Display { get; }
    public string SourceKey { get; }
    public ObservableCollection<OptionItem> Options { get; }
    public bool Suggested { get; }
    public bool FromPreset { get; }   // 이전 과목에서 매핑한 값을 초기값으로 가져옴
    public string SuggestionNote => Suggested && !FromPreset ? "✓ 이름이 같아 자동 선택됨" : "";

    private string _selected;
    public string Selected { get => _selected; set => SetProperty(ref _selected, value); }
}
