using System.Collections.ObjectModel;
using System.Data;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.App.ViewModels;

/// <summary>
/// 명단·평가계획 인앱 편집. 저장 시 SubjectPlan 목록과 명단을 재조립해
/// PlanWorkbookWriter 로 엑셀에 쓰고 메인이 다시 로드한다 (엑셀 직접 수정과 같은 경로).
/// </summary>
public sealed class PlanEditorViewModel : ObservableObject
{
    private const int EmptyRosterRows = 30;   // 빈 명단일 때 미리 깔아두는 빈 셀 행 수

    private readonly GradeScale _scale;

    public PlanEditorViewModel(
        IReadOnlyList<SubjectPlan> plans,
        IReadOnlyList<(string No, string Name)> roster,
        GradeScale scale)
    {
        _scale = scale;

        // 이름 입력 시 자동 번호 부여를 위해 행 추가/제거 때 감시를 붙인다
        Roster.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (RosterRow row in e.NewItems) row.PropertyChanged += RosterRow_PropertyChanged;
            if (e.OldItems is not null)
                foreach (RosterRow row in e.OldItems) row.PropertyChanged -= RosterRow_PropertyChanged;
        };

        foreach (var (no, name) in roster) Roster.Add(new RosterRow { No = no, Name = name });
        // 빈 명단은 빈 셀 행으로 채워 바로 입력·붙여넣기 가능하게 (저장 시 빈 행은 걸러짐)
        while (Roster.Count < EmptyRosterRows) Roster.Add(new RosterRow());

        foreach (var p in plans) Subjects.Add(new PlanSubjectEdit(p, scale));
        SelectedSubject = Subjects.FirstOrDefault();

        AddSubjectCommand = new RelayCommand(AddSubject);
        RemoveSubjectCommand = new RelayCommand(RemoveSubject, () => SelectedSubject is not null);
        AddRosterRowCommand = new RelayCommand(() => Roster.Add(new RosterRow()));
        RemoveRosterRowCommand = new RelayCommand(RemoveRosterRow, () => SelectedRosterRow is not null);
    }

    public ObservableCollection<RosterRow> Roster { get; } = new();
    public ObservableCollection<PlanSubjectEdit> Subjects { get; } = new();

    /// <summary>
    /// 이름을 입력하면 번호가 빈 행에 다음 번호를 자동 부여.
    /// 다음 번호 = 현재 숫자 번호의 최댓값 + 1 — 사용자가 번호를 고쳐 빈 번호가 생겨도
    /// (예: 14 다음 16) 이어지는 자동 번호는 17이 된다.
    /// </summary>
    private void RosterRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RosterRow.Name) || sender is not RosterRow row) return;
        if (string.IsNullOrWhiteSpace(row.Name) || !string.IsNullOrWhiteSpace(row.No)) return;

        var max = Roster
            .Where(r => int.TryParse(r.No, out _))
            .Select(r => int.Parse(r.No))
            .DefaultIfEmpty(0)
            .Max();
        row.No = (max + 1).ToString();
    }

    private RosterRow? _selectedRosterRow;
    public RosterRow? SelectedRosterRow
    {
        get => _selectedRosterRow;
        set { if (SetProperty(ref _selectedRosterRow, value)) ((RelayCommand)RemoveRosterRowCommand).RaiseCanExecuteChanged(); }
    }

    private PlanSubjectEdit? _selectedSubject;
    public PlanSubjectEdit? SelectedSubject
    {
        get => _selectedSubject;
        set { if (SetProperty(ref _selectedSubject, value)) ((RelayCommand)RemoveSubjectCommand).RaiseCanExecuteChanged(); }
    }

    public string ScaleSummary => string.Join(" / ", _scale.Levels.Select(l => l.Label));

    public ICommand AddSubjectCommand { get; }
    public ICommand RemoveSubjectCommand { get; }
    public ICommand AddRosterRowCommand { get; }
    public ICommand RemoveRosterRowCommand { get; }

    /// <summary>행 삭제 — 지운 번호보다 큰 번호들은 하나씩 당긴다 (18 삭제 → 19가 18로).</summary>
    private void RemoveRosterRow()
    {
        var row = SelectedRosterRow;
        if (row is null) return;
        Roster.Remove(row);

        if (int.TryParse(row.No, out var removed))
            foreach (var r in Roster)
                if (int.TryParse(r.No, out var n) && n > removed)
                    r.No = (n - 1).ToString();
    }

    private void AddSubject()
    {
        var baseName = "새과목";
        var name = baseName;
        for (int i = 2; Subjects.Any(s => s.Name == name); i++) name = $"{baseName}{i}";
        var subject = new PlanSubjectEdit(name, _scale);
        Subjects.Add(subject);
        SelectedSubject = subject;
    }

    private void RemoveSubject()
    {
        if (SelectedSubject is null) return;
        var idx = Subjects.IndexOf(SelectedSubject);
        Subjects.Remove(SelectedSubject);
        SelectedSubject = Subjects.Count > 0 ? Subjects[Math.Min(idx, Subjects.Count - 1)] : null;
    }

    /// <summary>클립보드 표를 명단으로 반영 (전체 교체).</summary>
    public int PasteRoster(string clipboardText)
    {
        var parsed = ClipboardTable.ToRoster(ClipboardTable.Parse(clipboardText));
        if (parsed.Count == 0) return 0;
        Roster.Clear();
        foreach (var (no, name) in parsed) Roster.Add(new RosterRow { No = no, Name = name });
        return parsed.Count;
    }

    /// <summary>편집 내용 검증 + (계획, 명단) 조립. 문제가 있으면 error 에 사유를 담고 null.</summary>
    public (IReadOnlyList<SubjectPlan> Plans, IReadOnlyList<(string, string)> Roster)? Build(out string? error)
    {
        var roster = Roster
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select((r, i) => (No: string.IsNullOrWhiteSpace(r.No) ? (i + 1).ToString() : r.No.Trim(),
                               Name: r.Name.Trim()))
            .ToList();

        var plans = new List<SubjectPlan>();
        var names = new HashSet<string>();
        foreach (var s in Subjects)
        {
            var name = s.Name.Trim();
            if (name == "") { error = "이름이 빈 과목이 있습니다."; return null; }
            if (!names.Add(name)) { error = $"과목명 '{name}'이(가) 중복됩니다."; return null; }

            var plan = s.BuildPlan(out var subjectError);
            if (plan is null) { error = $"[{name}] {subjectError}"; return null; }
            if (plan.Domains.Count > 0) plans.Add(plan);
        }

        error = null;
        return (plans, roster);
    }
}

/// <summary>명단 한 줄 (편집용).</summary>
public sealed class RosterRow : ObservableObject
{
    private string _no = "";
    public string No { get => _no; set => SetProperty(ref _no, value); }

    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
}

/// <summary>
/// 한 과목의 평가계획 편집 표. 행 = 영역, 열 = 영역 | 성취기준 | 등급별 기준내용.
/// </summary>
public sealed class PlanSubjectEdit : ObservableObject
{
    public const string DomainColumn = "영역";
    public const string AchievementColumn = "성취기준";

    private readonly GradeScale _scale;

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public DataTable Grid { get; }
    public DataView GridView => Grid.DefaultView;

    public PlanSubjectEdit(string name, GradeScale scale, int seedEmptyRows = 6)
    {
        _scale = scale;
        _name = name;
        Grid = NewTable(scale);
        // 새 과목은 빈 셀 행을 미리 깔아 바로 입력·붙여넣기 가능하게 (저장 시 빈 행은 걸러짐)
        for (int i = 0; i < seedEmptyRows; i++) Grid.Rows.Add(Grid.NewRow());
    }

    public PlanSubjectEdit(SubjectPlan plan, GradeScale scale) : this(plan.SubjectName, scale, seedEmptyRows: 0)
    {
        foreach (var domain in plan.Domains)
        {
            var row = Grid.NewRow();
            row[DomainColumn] = domain;
            // 성취기준은 영역 내 등급들이 공유 — 첫 번째로 발견되는 값 사용
            row[AchievementColumn] = _scale.Levels
                .Select(l => plan.Criteria.TryGetValue((domain, l.Label), out var e) ? e.Achievement : null)
                .FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? "";
            foreach (var level in _scale.Levels)
                row[level.Label] = plan.Criteria.TryGetValue((domain, level.Label), out var e) ? e.Text : "";
            Grid.Rows.Add(row);
        }
    }

    private static DataTable NewTable(GradeScale scale)
    {
        var t = new DataTable();
        t.Columns.Add(DomainColumn);
        t.Columns.Add(AchievementColumn);
        foreach (var level in scale.Levels) t.Columns.Add(level.Label);
        return t;
    }

    /// <summary>표 → SubjectPlan. 영역명 중복이면 error 와 함께 null.</summary>
    public SubjectPlan? BuildPlan(out string? error)
    {
        var domains = new List<string>();
        var criteria = new Dictionary<(string, string), CriteriaEntry>();

        foreach (DataRow row in Grid.Rows)
        {
            var domain = (row[DomainColumn]?.ToString() ?? "").Trim();
            if (domain == "") continue;   // 영역 없는 행은 무시 (새 행 등)
            if (domains.Contains(domain)) { error = $"영역명 '{domain}'이(가) 중복됩니다."; return null; }
            domains.Add(domain);

            var ach = (row[AchievementColumn]?.ToString() ?? "").Trim();
            foreach (var level in _scale.Levels)
            {
                var text = (row[level.Label]?.ToString() ?? "").Trim();
                if (text != "")
                    criteria[(domain, level.Label)] = new CriteriaEntry(text, ach == "" ? null : ach);
            }
        }

        error = null;
        return new SubjectPlan(Name.Trim(), domains, criteria);
    }
}
