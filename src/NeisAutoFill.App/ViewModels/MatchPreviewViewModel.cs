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

        // ── 학생 매핑 (화면에 있는데 엑셀에 없는 학생) ──
        var studentOptions = new List<string> { ExcludeLabel };
        studentOptions.AddRange(sheet.Students.Select(s => s.Name));
        foreach (var (no, name) in issues.UnmatchedStudents)
        {
            var suggestion = Suggest(name, sheet.Students.Select(s => s.Name).ToList());
            StudentMaps.Add(new MapItem($"{no}번 {name}", name, studentOptions,
                suggestion ?? ExcludeLabel, suggestion is not null));
        }

        // ── 영역 매핑 ──
        // 순서 모드가 강제되는 경우: 화면 영역명 중복 (이름으로 구분 불가)
        OrderModeForced = issues.DuplicateAreas;
        _useOrderMode = OrderModeForced || (issues.AreaCountMismatch && issues.UnmatchedAreas.Count > 0);

        var areaOptions = new List<string> { ExcludeLabel };
        areaOptions.AddRange(sheet.Areas);

        foreach (var area in issues.UnmatchedAreas)
        {
            var suggestion = Suggest(area, sheet.Areas);
            AreaMaps.Add(new MapItem($"화면 영역 '{area}'", area, areaOptions,
                suggestion ?? ExcludeLabel, suggestion is not null));
        }

        // 순서 매핑: 화면 행 순서 1..N → 엑셀 영역 (기본값: 같은 위치)
        for (int i = 0; i < issues.RowsPerStudent; i++)
        {
            var screenArea = i < issues.ScreenAreas.Count ? issues.ScreenAreas[i] : $"{i + 1}번째";
            var def = i < sheet.Areas.Count ? sheet.Areas[i] : ExcludeLabel;
            OrderMaps.Add(new MapItem($"{i + 1}번째 행 '{screenArea}'", screenArea, areaOptions, def, false));
        }

        HasStudentIssues = StudentMaps.Count > 0;
        HasAreaIssues = AreaMaps.Count > 0 || issues.DuplicateAreas || issues.AreaCountMismatch;
    }

    public string SubjectWarning { get; }
    public bool HasSubjectWarning => SubjectWarning != "";
    public bool HasStudentIssues { get; }
    public bool HasAreaIssues { get; }
    public bool OrderModeForced { get; }

    private bool _subjectAccepted;
    public bool SubjectAccepted { get => _subjectAccepted; set => SetProperty(ref _subjectAccepted, value); }

    private bool _useOrderMode;
    public bool UseOrderMode { get => _useOrderMode; set => SetProperty(ref _useOrderMode, value); }
    public bool UseNameMode { get => !_useOrderMode; set { if (value) UseOrderMode = false; OnPropertyChanged(); } }

    public ObservableCollection<MapItem> StudentMaps { get; } = new();
    public ObservableCollection<MapItem> AreaMaps { get; } = new();
    public ObservableCollection<MapItem> OrderMaps { get; } = new();

    /// <summary>진행 가능 여부 검증. 문제가 있으면 사유 반환.</summary>
    public string? Validate()
    {
        if (HasSubjectWarning && !SubjectAccepted)
            return "과목이 다릅니다. 계속하려면 아래 확인란에 체크하세요.";
        if (UseOrderMode && OrderMaps.All(m => m.Selected == ExcludeLabel))
            return "순서 매핑에서 입력할 영역이 하나도 선택되지 않았습니다.";
        // 같은 엑셀 영역을 두 위치에 매핑하면 오입력
        var dup = OrderMaps.Where(m => m.Selected != ExcludeLabel)
            .GroupBy(m => m.Selected).FirstOrDefault(g => UseOrderMode && g.Count() > 1);
        if (dup is not null) return $"엑셀 영역 '{dup.Key}'이(가) 두 위치에 매핑되었습니다.";
        return null;
    }

    /// <summary>사용자 선택 → 엔진 결정으로 변환.</summary>
    public MatchDecision BuildDecision()
    {
        var nameMap = StudentMaps.ToDictionary(
            m => m.SourceKey,
            m => m.Selected == ExcludeLabel ? "" : m.Selected);

        if (UseOrderMode)
        {
            var ordered = OrderMaps.Select(m => m.Selected == ExcludeLabel ? "" : m.Selected).ToList();
            return new MatchDecision(StudentMatcher.MatchMode.ByOrder,
                NameMap: nameMap.Count > 0 ? nameMap : null,
                OrderedExcelAreas: ordered);
        }

        var areaMap = AreaMaps.ToDictionary(
            m => m.SourceKey,
            m => m.Selected == ExcludeLabel ? "" : m.Selected);
        return new MatchDecision(StudentMatcher.MatchMode.ByName,
            AreaMap: areaMap.Count > 0 ? areaMap : null,
            NameMap: nameMap.Count > 0 ? nameMap : null);
    }

    /// <summary>간단 유사도 제안 — 정규화 후 포함/편집거리 1 이내.</summary>
    private static string? Suggest(string source, IReadOnlyList<string> candidates)
    {
        var s = NameNormalizerLite(source);
        string? best = null;
        int bestScore = int.MaxValue;
        foreach (var c in candidates)
        {
            var t = NameNormalizerLite(c);
            int score;
            if (s == t) score = 0;
            else if (t.Contains(s) || s.Contains(t)) score = 1;
            else score = Levenshtein(s, t) <= 1 ? 2 : int.MaxValue;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return bestScore <= 2 ? best : null;
    }

    private static string NameNormalizerLite(string s) =>
        new(s.Where(ch => !char.IsWhiteSpace(ch) && ch != '·' && ch != '.').ToArray());

    private static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 99;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
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
    public string SuggestionNote => Suggested ? "(자동 추천)" : "";

    private string _selected;
    public string Selected { get => _selected; set => SetProperty(ref _selected, value); }
}
