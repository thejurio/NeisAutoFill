using System.Windows;
using System.Windows.Controls;

namespace NeisAutoFill.App;

/// <summary>
/// 급하게 생긴 쉬는 날을 표시한다.
/// 학교를 운영하다 보면 계획서에 없던 휴업일이 생긴다 — 문서를 다시 받지 않고 여기서 넣는다.
/// </summary>
public partial class HolidayPromptWindow : Window
{
    public HolidayPromptWindow(DateOnly date)
    {
        InitializeComponent();

        DateText.Text = $"{date:yyyy년 M월 d일} ({Korean(date.DayOfWeek)}요일)";
        NameBox.Text = "재량휴업일";
        NameBox.SelectAll();
        NameBox.Focus();
    }

    /// <summary>붙일 이름. 취소하면 null.</summary>
    public string? HolidayName { get; private set; }

    public static string? Ask(DateOnly date, Window? owner)
    {
        var w = new HolidayPromptWindow(date) { Owner = owner };
        return w.ShowDialog() == true ? w.HolidayName : null;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string text }) NameBox.Text = text;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        HolidayName = name.Length == 0 ? "휴업일" : name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Korean(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "월",
        DayOfWeek.Tuesday => "화",
        DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목",
        DayOfWeek.Friday => "금",
        DayOfWeek.Saturday => "토",
        _ => "일",
    };
}
