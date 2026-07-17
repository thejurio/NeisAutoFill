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
            var suggestion = Suggest(name, sheet.Students.Select(s => s.Name).ToList());
            StudentMaps.Add(new MapItem($"{no}번 {name}", name, studentOptions,
                suggestion ?? ExcludeLabel, suggestion is not null));
        }

        // ── 영역 확인 — 화면 순서대로 "어떤 영역의 성적을 넣을지" 하나씩 고른다 ──
        // (이름이 같은 영역은 자동으로 골라두고, 나머지는 남는 영역을 순서대로 제안)
        var areaOptions = new List<string> { ExcludeLabel };
        areaOptions.AddRange(sheet.Areas);

        int rows = issues.RowsPerStudent;
        var defaults = new string?[rows];
        var autoPicked = new bool[rows];
        var used = new HashSet<string>();
        for (int i = 0; i < rows; i++)   // 1차: 화면 영역명과 같은 이름이 있으면 자동 선택
        {
            var screenArea = i < issues.ScreenAreas.Count ? issues.ScreenAreas[i] : null;
            if (screenArea is not null && sheet.Areas.Contains(screenArea) && used.Add(screenArea))
            {
                defaults[i] = screenArea;
                autoPicked[i] = true;
            }
        }
        var remaining = sheet.Areas.Where(a => !used.Contains(a)).ToList();
        int r = 0;
        for (int i = 0; i < rows; i++)   // 2차: 남은 위치엔 남은 영역을 순서대로 제안 (모자라면 '입력 안 함')
            defaults[i] ??= r < remaining.Count ? remaining[r++] : ExcludeLabel;

        for (int i = 0; i < rows; i++)
        {
            var screenArea = i < issues.ScreenAreas.Count ? issues.ScreenAreas[i] : "(이름 확인 안 됨)";
            OrderMaps.Add(new MapItem($"화면 {i + 1}번째 · {screenArea}", screenArea,
                areaOptions, defaults[i]!, autoPicked[i]));
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
    public string SuggestionNote => Suggested ? "✓ 이름이 같아 자동 선택됨" : "";

    private string _selected;
    public string Selected { get => _selected; set => SetProperty(ref _selected, value); }
}
