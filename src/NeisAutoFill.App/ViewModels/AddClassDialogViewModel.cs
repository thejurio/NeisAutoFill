using NeisAutoFill.App.Mvvm;

namespace NeisAutoFill.App.ViewModels;

/// <summary>학년·반 추가 대화상자 (F9 M4a). ClassVisible=false 면 학년만(계획용).</summary>
public sealed class AddClassDialogViewModel : ObservableObject
{
    public IReadOnlyList<int> GradeOptions { get; } = new[] { 1, 2, 3, 4, 5, 6 };

    private int _grade = 3;
    public int Grade { get => _grade; set => SetProperty(ref _grade, value); }

    private string _className = "1";
    public string ClassName { get => _className; set => SetProperty(ref _className, value); }

    private bool _classVisible = true;
    /// <summary>반 입력 표시 여부 (학년만 추가할 땐 false).</summary>
    public bool ClassVisible { get => _classVisible; set => SetProperty(ref _classVisible, value); }

    public string Title => ClassVisible ? "학년·반 추가" : "학년 추가";

    /// <summary>입력이 유효한지 — 학년만이면 학년만, 반 포함이면 반 이름도.</summary>
    public bool IsValid => !ClassVisible || NeisAutoFill.Core.SubjectModePaths.IsValidClass(ClassName?.Trim());
}
