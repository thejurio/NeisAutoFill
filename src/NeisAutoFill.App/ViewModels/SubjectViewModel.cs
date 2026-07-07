using System.Data;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.App.ViewModels;

/// <summary>한 과목 탭. 성적 표(DataTable) + 입력/중지 커맨드.
/// (dry-run 은 UI 에서 제거 — 복원 방법은 docs/보관_진단_검증도구.md)</summary>
public sealed class SubjectViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SubjectSheet Sheet { get; }
    public string SubjectName => Sheet.SubjectName;
    public string AreasText => "영역: " + string.Join(", ", Sheet.Areas);
    public DataTable Grid { get; }
    public DataView GridView => Grid.DefaultView;

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }

    public SubjectViewModel(MainViewModel main, SubjectSheet sheet)
    {
        _main = main;
        Sheet = sheet;

        Grid = new DataTable();
        Grid.Columns.Add("번호");
        Grid.Columns.Add("이름");
        foreach (var a in sheet.Areas) Grid.Columns.Add(a);
        foreach (var s in sheet.Students)
        {
            var row = Grid.NewRow();
            row["번호"] = s.No;
            row["이름"] = s.Name;
            foreach (var a in sheet.Areas)
                row[a] = s.Grades.TryGetValue(a, out var g) ? g : "";
            Grid.Rows.Add(row);
        }

        RunCommand = new AsyncRelayCommand(() => _main.RunSubjectAsync(Sheet, dryRun: false));
        CancelCommand = new RelayCommand(() => _main.Cancel());
    }
}
