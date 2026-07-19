using System.Collections.ObjectModel;
using NeisAutoFill.App.Mvvm;

namespace NeisAutoFill.App.ViewModels;

/// <summary>평가계획 인식 후 불러올 항목을 고르는 창의 한 행 (F9 M4b).
/// 담임=과목만, 전담=학년+과목(학년 불명이면 사용자가 콤보로 지정).</summary>
public sealed class PlanPickItem : ObservableObject
{
    private bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    private int _grade;
    public int Grade { get => _grade; set => SetProperty(ref _grade, value); }

    public string Subject { get; }

    /// <summary>학년 콤보를 보일지 (전담 모드에서만 true).</summary>
    public bool ShowGrade { get; }

    /// <summary>학년 콤보 선택지 1~6.</summary>
    public static IReadOnlyList<int> GradeOptions { get; } = new[] { 1, 2, 3, 4, 5, 6 };

    public PlanPickItem(string subject, int grade, bool showGrade)
    {
        Subject = subject;
        _grade = grade;
        ShowGrade = showGrade;
    }
}

/// <summary>불러올 과목(담임)/학년·과목(전담)을 고르는 대화상자 VM.</summary>
public sealed class PlanPickerViewModel : ObservableObject
{
    public ObservableCollection<PlanPickItem> Items { get; } = new();

    /// <summary>학년 열을 보일지 (전담이면 true).</summary>
    public bool ShowGradeColumn { get; }

    public string Title { get; }
    public string Hint { get; }

    private PlanPickerViewModel(string title, string hint, bool showGrade)
    {
        Title = title;
        Hint = hint;
        ShowGradeColumn = showGrade;
    }

    /// <summary>담임: 과목 목록만.</summary>
    public static PlanPickerViewModel ForSubjects(IReadOnlyList<string> subjects)
    {
        var vm = new PlanPickerViewModel("불러올 과목 선택",
            "평가계획을 넣을 과목에 체크하세요.", showGrade: false);
        foreach (var s in subjects) vm.Items.Add(new PlanPickItem(s, 0, showGrade: false));
        return vm;
    }

    /// <summary>전담: (학년·과목) 단위. 학년 불명(0)은 currentGrade 를 기본값으로 채워 사용자가 고치게 한다.</summary>
    public static PlanPickerViewModel ForUnits(IReadOnlyList<Core.PlanUnit> units, int currentGrade)
    {
        var vm = new PlanPickerViewModel("불러올 학년·과목 선택",
            "불러올 항목에 체크하고, 학년이 비어 있으면 지정하세요.", showGrade: true);
        foreach (var u in units)
        {
            var grade = u.HasGrade ? u.Grade
                : (currentGrade is >= 1 and <= 6 ? currentGrade : 1);
            vm.Items.Add(new PlanPickItem(u.Subject, grade, showGrade: true));
        }
        return vm;
    }

    /// <summary>체크된 과목명 (담임).</summary>
    public IReadOnlyList<string> SelectedSubjects() =>
        Items.Where(i => i.IsChecked).Select(i => i.Subject).ToList();

    /// <summary>체크된 (학년·과목) 단위 (전담) — 학년은 콤보에서 확정된 값.</summary>
    public IReadOnlyList<Core.PlanUnit> SelectedUnits() =>
        Items.Where(i => i.IsChecked).Select(i => new Core.PlanUnit(i.Grade, i.Subject)).ToList();

    public bool AnyChecked => Items.Any(i => i.IsChecked);
}
