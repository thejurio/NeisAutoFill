using System.Data;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.App.ViewModels;

/// <summary>한 과목 탭. 편집 가능한 성적 표(DataTable) + 입력/중지 커맨드.
/// 표를 편집하면 Sheet(과목 시트)가 최신 값으로 재구성돼 나이스 입력·서술문 생성에 반영된다.
/// (dry-run 은 UI 에서 제거 — 복원 방법은 docs/보관_진단_검증도구.md)</summary>
public sealed class SubjectViewModel : ObservableObject
{
    public const string NoteColumn = "특기사항";

    private readonly MainViewModel _main;
    private readonly IReadOnlyList<string> _areas;

    public string SubjectName { get; }
    public IReadOnlyList<string> Areas => _areas;
    public string AreasText => "영역: " + string.Join(", ", _areas);
    public DataTable Grid { get; }
    public DataView GridView => Grid.DefaultView;

    /// <summary>사용자가 표를 수정했는지 (저장 확인용).</summary>
    public bool IsDirty { get; private set; }

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }

    public SubjectViewModel(MainViewModel main, SubjectSheet sheet)
    {
        _main = main;
        SubjectName = sheet.SubjectName;
        _areas = sheet.Areas;

        Grid = new DataTable();
        Grid.Columns.Add("번호");
        Grid.Columns.Add("이름");
        foreach (var a in sheet.Areas) Grid.Columns.Add(a);
        Grid.Columns.Add(NoteColumn);

        foreach (var s in sheet.Students)
        {
            var row = Grid.NewRow();
            row["번호"] = s.No;
            row["이름"] = s.Name;
            foreach (var a in sheet.Areas)
                row[a] = s.Grades.TryGetValue(a, out var g) ? g : "";
            row[NoteColumn] = s.SpecialNote ?? "";
            Grid.Rows.Add(row);
        }

        Grid.ColumnChanged += (_, _) => IsDirty = true;   // 사용자 편집 감지

        RunCommand = new AsyncRelayCommand(() => _main.RunSubjectAsync(Sheet, dryRun: false));
        CancelCommand = new RelayCommand(() => _main.Cancel());
    }

    /// <summary>현재 표 상태로 과목 시트를 재구성 (편집 반영).</summary>
    public SubjectSheet Sheet
    {
        get
        {
            var students = new List<Student>();
            foreach (DataRow row in Grid.Rows)
            {
                var name = (row["이름"]?.ToString() ?? "").Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var no = (row["번호"]?.ToString() ?? "").Trim();
                var grades = new Dictionary<string, string>();
                foreach (var a in _areas)
                {
                    var v = (row[a]?.ToString() ?? "").Trim();
                    if (!string.IsNullOrEmpty(v)) grades[a] = v;
                }
                var note = (row[NoteColumn]?.ToString() ?? "").Trim();
                students.Add(new Student(no, name, grades, string.IsNullOrEmpty(note) ? null : note));
            }
            return new SubjectSheet(SubjectName, _areas, students);
        }
    }

    public void MarkSaved() => IsDirty = false;
}
