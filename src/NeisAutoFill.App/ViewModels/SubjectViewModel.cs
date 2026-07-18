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

    // 영역명 ↔ DataTable 안전 컬럼ID. 영역명에 쉼표·대괄호가 있으면 WPF 인덱서 바인딩([영역])이
    // 깨지므로(값 안 뜨고 입력 불가), 실제 컬럼명은 특수문자 없는 ID 로 두고 표시 헤더만 영역명으로 쓴다.
    private readonly Dictionary<string, string> _colByArea = new();   // 영역명 → 컬럼ID
    private readonly Dictionary<string, string> _areaByCol = new();   // 컬럼ID → 영역명

    public string SubjectName { get; }
    public IReadOnlyList<string> Areas => _areas;
    public string AreasText => "영역: " + string.Join(", ", _areas);
    public DataTable Grid { get; }
    public DataView GridView => Grid.DefaultView;

    /// <summary>표시 헤더(영역명·번호·이름·특기사항)를 실제 DataTable 컬럼명으로. 영역이면 안전ID, 나머지는 그대로.</summary>
    public string DataColumnOf(string header) => _colByArea.TryGetValue(header, out var id) ? id : header;

    /// <summary>DataTable 컬럼명(안전ID 등)을 표시 헤더로. 영역 컬럼ID면 영역명, 나머지는 그대로.</summary>
    public string HeaderOf(string column) => _areaByCol.TryGetValue(column, out var area) ? area : column;

    /// <summary>이 컬럼명이 영역(등급) 컬럼인지.</summary>
    public bool IsAreaColumn(string column) => _areaByCol.ContainsKey(column);

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
        for (int i = 0; i < sheet.Areas.Count; i++)
        {
            var colId = "col_area_" + i;   // 특수문자 없는 안전 컬럼ID (표시엔 안 쓰임 — 헤더는 영역명)
            _colByArea[sheet.Areas[i]] = colId;
            _areaByCol[colId] = sheet.Areas[i];
            Grid.Columns.Add(colId);
        }
        Grid.Columns.Add(NoteColumn);

        foreach (var s in sheet.Students)
        {
            var row = Grid.NewRow();
            row["번호"] = s.No;
            row["이름"] = s.Name;
            foreach (var a in sheet.Areas)
                row[_colByArea[a]] = s.Grades.TryGetValue(a, out var g) ? g : "";
            row[NoteColumn] = s.SpecialNote ?? "";
            Grid.Rows.Add(row);
        }

        Grid.ColumnChanging += (_, e) =>
        {
            // 변경 전 값을 실행취소 스택에 기록 (일괄 작업 중이면 한 묶음으로)
            if (_applyingUndo || e.Row is null || e.Column is null) return;
            var entry = (e.Row, e.Column.ColumnName, e.Row[e.Column]);
            if (_batch is not null) _batch.Add(entry);
            else _undo.Push(new() { entry });
        };

        Grid.ColumnChanged += (_, _) =>
        {
            IsDirty = true;                // 사용자 편집 감지 (실행취소 적용도 편집)
            _main.NotifyGradesEdited();    // 디바운스 자동 저장 예약
        };

        RunCommand = new AsyncRelayCommand(() => _main.RunSubjectAsync(Snapshot(), dryRun: false));
        CancelCommand = new RelayCommand(() => _main.Cancel());
    }

    /// <summary>현재 표 상태의 스냅샷 (호출 시점 기준 — 이후 편집은 반영되지 않는다).
    /// 긴 작업(전과목 입력 등)은 시작 시 한 번 받아 고정해서 쓸 것.</summary>
    public SubjectSheet Snapshot()
    {
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
                    var v = (row[_colByArea[a]]?.ToString() ?? "").Trim();
                    if (!string.IsNullOrEmpty(v)) grades[a] = v;
                }
                var note = (row[NoteColumn]?.ToString() ?? "").Trim();
                students.Add(new Student(no, name, grades, string.IsNullOrEmpty(note) ? null : note));
            }
            return new SubjectSheet(SubjectName, _areas, students);
        }
    }

    public void MarkSaved() => IsDirty = false;

    // ── 실행 취소 (Ctrl+Z) ────────────────────
    private readonly Stack<List<(DataRow Row, string Col, object? Old)>> _undo = new();
    private List<(DataRow Row, string Col, object? Old)>? _batch;
    private bool _applyingUndo;

    /// <summary>일괄 작업(붙여넣기·일괄 지정) 시작 — 이후 변경이 한 번의 Ctrl+Z 로 되돌려진다.</summary>
    public void BeginBulkEdit() => _batch = new();

    public void EndBulkEdit()
    {
        if (_batch is { Count: > 0 }) _undo.Push(_batch);
        _batch = null;
    }

    /// <summary>마지막 편집(또는 일괄 작업 전체)을 되돌린다. 되돌린 게 없으면 false.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var batch = _undo.Pop();
        _applyingUndo = true;
        try
        {
            for (int i = batch.Count - 1; i >= 0; i--)
                batch[i].Row[batch[i].Col] = batch[i].Old ?? "";
        }
        finally { _applyingUndo = false; }
        return true;
    }
}
