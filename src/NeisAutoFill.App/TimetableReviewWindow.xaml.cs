using System.Windows;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App;

/// <summary>
/// 문서 인식 결과 확인 창 (로드맵 T2).
/// 사용자가 눈으로 보고 고친 뒤에만 다음 단계로 넘어간다 — 문서 양식은 학교마다 다르다.
/// </summary>
public partial class TimetableReviewWindow : Window
{
    private readonly TimetableReviewViewModel _vm;

    public TimetableReviewWindow(TimetableReviewViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    /// <summary>검토를 마친 수업 목록. 취소하면 null.</summary>
    public IReadOnlyList<TimetableSourceLesson>? Lessons { get; private set; }

    /// <summary>창을 띄워 검토 결과를 받는다. 취소 시 null.</summary>
    public static IReadOnlyList<TimetableSourceLesson>? Ask(TimetableReviewViewModel vm, Window? owner)
    {
        var w = new TimetableReviewWindow(vm) { Owner = owner };
        return w.ShowDialog() == true ? w.Lessons : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Lessons = _vm.BuildLessons();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
