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

    public MatchPreviewViewModel(MatchAnalyzer.Issues issues, SubjectSheet sheet)
    {
        _issues = issues;
        _sheet = sheet;

        // ── 과목 불일치 배너 ──
        SubjectWarning = issues.SubjectMismatch
            ? $"화면 과목은 '{issues.ScreenSubject}'인데 입력 대상은 '{sheet.SubjectName}'입니다."
            : "";

        // ── 학생 확인 (화면에 있는데 내 자료에 없는 학생) ──
        var studentOptions = new List<string> { ExcludeLabel };
        studentOptions.AddRange(sheet.Students.Select(s => s.Name));
        foreach (var (no, name) in issues.UnmatchedStudents)
        {
            var suggestion = SimilaritySuggester.Suggest(name, sheet.Students.Select(s => s.Name).ToList());
            StudentMaps.Add(new MapItem($"{no}번 {name}", name, studentOptions,
                suggestion ?? ExcludeLabel, suggestion is not null));
        }

        // ── 영역 확인 — 화면 순서대로 "어떤 영역의 성적을 넣을지" 하나씩 고른다 ──
        var areaOptions = new List<string> { ExcludeLabel };
        areaOptions.AddRange(sheet.Areas);

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

    public string SubjectWarning { get; }
    public bool HasSubjectWarning => SubjectWarning != "";
    public bool HasStudentIssues { get; }
    public bool HasAreaIssues { get; }

    private bool _subjectAccepted;
    public bool SubjectAccepted { get => _subjectAccepted; set => SetProperty(ref _subjectAccepted, value); }

    public ObservableCollection<MapItem> StudentMaps { get; } = new();
    public ObservableCollection<MapItem> OrderMaps { get; } = new();

    /// <summary>진행 가능 여부 검증. 문제가 있으면 사유 반환.</summary>
    public string? Validate()
    {
        if (HasSubjectWarning && !SubjectAccepted)
            return "과목이 다릅니다. 계속하려면 아래 확인란에 체크하세요.";
        if (HasAreaIssues && OrderMaps.All(m => m.Selected == ExcludeLabel))
            return "넣을 영역이 하나도 선택되지 않았습니다. 최소 한 줄은 영역을 골라 주세요.";
        // 같은 영역을 두 줄에 고르면 오입력
        var dup = OrderMaps.Where(m => m.Selected != ExcludeLabel)
            .GroupBy(m => m.Selected).FirstOrDefault(g => g.Count() > 1);
        if (HasAreaIssues && dup is not null)
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

/// <summary>매핑 한 줄: 화면 항목 → 엑셀 항목 선택.</summary>
public sealed class MapItem : ObservableObject
{
    public MapItem(string display, string sourceKey, IReadOnlyList<string> options, string selected, bool suggested)
    {
        Display = display;
        SourceKey = sourceKey;
        Options = options;
        _selected = selected;
        Suggested = suggested;
    }

    public string Display { get; }
    public string SourceKey { get; }
    public IReadOnlyList<string> Options { get; }
    public bool Suggested { get; }
    public string SuggestionNote => Suggested ? "✓ 이름이 같아 자동 선택됨" : "";

    private string _selected;
    public string Selected { get => _selected; set => SetProperty(ref _selected, value); }
}
