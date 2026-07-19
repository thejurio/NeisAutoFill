using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NeisAutoFill.App;

/// <summary>업데이트 안내 창 — 릴리스 노트를 보여주고 [지금 업데이트]/[나중에] 를 묻는다.
/// '나중에'는 영구 건너뛰기가 아니라 다음 실행 때 다시 안내된다.</summary>
public partial class UpdatePromptWindow : Window
{
    private UpdatePromptWindow() => InitializeComponent();

    private static readonly Brush HeadBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x5B, 0xE0));

    /// <summary>릴리스 노트(마크다운)를 서식 그대로 렌더 — ##=파란 제목, **굵게**, `코드`, - 불릿.</summary>
    private void SetNotes(string md)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(8, 6, 8, 6) };
        foreach (var raw in md.Replace("\r\n", "\n").Trim().Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;   // 빈 줄은 문단 간격이 대신한다

            var head = Regex.Match(line, @"^(#{1,6})\s*(.*)$");
            Paragraph p;
            if (head.Success)   // 헤딩 — 크고 파랗게, 위 여백
            {
                p = new Paragraph { Margin = new Thickness(0, 10, 0, 4), FontSize = 14.5,
                                    FontWeight = FontWeights.Bold, Foreground = HeadBrush };
                AddInlines(p.Inlines, head.Groups[2].Value);
            }
            else if (Regex.Match(line, @"^\s*-\s+(.*)$") is { Success: true } li)   // 불릿
            {
                p = new Paragraph { Margin = new Thickness(14, 1, 0, 1), LineHeight = 19 };
                p.Inlines.Add(new Run("•  ") { Foreground = HeadBrush });
                AddInlines(p.Inlines, li.Groups[1].Value);
            }
            else
            {
                p = new Paragraph { Margin = new Thickness(0, 2, 0, 2), LineHeight = 19 };
                AddInlines(p.Inlines, line.Trim());
            }
            doc.Blocks.Add(p);
        }
        NotesRich.Document = doc;
    }

    /// <summary>한 줄 안의 **굵게**·`코드` 를 인라인 서식으로.</summary>
    private static void AddInlines(InlineCollection target, string text)
    {
        int i = 0;
        foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|`([^`]+)`"))
        {
            if (m.Index > i) target.Add(new Run(text[i..m.Index]));
            if (m.Groups[1].Success) target.Add(new Bold(new Run(m.Groups[1].Value)));
            else target.Add(new Run(m.Groups[2].Value)
            { Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)) });
            i = m.Index + m.Length;
        }
        if (i < text.Length) target.Add(new Run(text[i..]));
    }

    /// <summary>true = 지금 업데이트, false = 나중에.</summary>
    public static bool Ask(string latest, string current, string notes, Window? owner)
    {
        var win = new UpdatePromptWindow { Owner = owner };
        win.HeadText.Text = $"새 버전 v{latest} 이 있습니다  (현재 v{current})";
        win.SetNotes(string.IsNullOrWhiteSpace(notes) ? "이번 버전의 변경 내용 안내가 없습니다." : notes);
        return win.ShowDialog() == true;
    }

    /// <summary>업데이트 직후 1회 — "이번 버전에서 새로워진 점"(패치로그)을 보여준다.</summary>
    public static void ShowWhatsNew(string version, string notes, DateTime? publishedAt, Window? owner)
    {
        var win = new UpdatePromptWindow { Owner = owner };
        win.TitleText.Text = "업데이트 완료";
        win.HeadText.Text = $"🎉 v{version} 으로 업데이트되었습니다";
        win.SubText.Text = publishedAt is { } d
            ? $"이번 버전에서 새로워진 점입니다.  ·  {d:yyyy-MM-dd} 업데이트"
            : "이번 버전에서 새로워진 점입니다.";
        win.SetNotes(string.IsNullOrWhiteSpace(notes) ? "변경 내용 안내를 불러오지 못했습니다." : notes);
        win.FootText.Text = "";
        win.LaterBtn.Visibility = Visibility.Collapsed;
        win.NowBtn.Content = "확인";
        win.ShowDialog();   // NowBtn → DialogResult=true 로 닫힘
    }

    private void Now_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Later_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
