using NeisAutoFill.Core.Timetable;
using UglyToad.PdfPig;

namespace NeisAutoFill.Generator;

/// <summary>
/// PDF 에서 글자와 좌표를 뽑는다 (연간 시간표·창체 계획 로컬 파싱용).
///
/// 왜 좌표까지 뽑나: 이 문서들은 <b>표</b>다. 텍스트만 뽑으면 칸 경계가 사라져
/// "월요일 1교시"가 어느 것인지 알 수 없다. 실제로 hwpx 평탄화 텍스트로는
/// 학기 첫 주(화요일 시작)의 열이 하루씩 밀려 보였고, 좌표로는 정확히 맞았다.
///
/// 라이브러리는 <b>PdfPig</b>(MIT) — 순수 .NET 이라 배포 구조가 그대로 유지되고,
/// 글자별 위치를 준다. AGPL 인 iText 는 배포 때문에 쓰지 않는다.
/// </summary>
public static class PdfLayoutExtractor
{
    /// <summary>PDF 파일에서 쪽별 글자·좌표를 읽는다.</summary>
    /// <param name="path">PDF 경로</param>
    /// <param name="keepSpaces">
    /// 공백 글자도 남길지. <b>기본은 버린다</b> — 시간표 파서가 그 전제로 만들어져 있다.
    /// 평가계획처럼 <b>문장을 그대로 옮겨야 하는</b> 문서는 true 로 받는다:
    /// 이 PDF 들은 띄어쓰기를 진짜 공백 글자로 갖고 있어(실측: 한 쪽에 236개),
    /// 버리고 나면 글자 간격을 재서 되살려야 하는데 <b>양쪽 정렬 때문에 정확하지 않다</b>.
    /// </param>
    public static DocumentLayout Extract(string path, bool keepSpaces = false)
    {
        using var doc = PdfDocument.Open(path);
        var pages = new List<DocumentPage>(doc.NumberOfPages);

        for (var i = 1; i <= doc.NumberOfPages; i++)
        {
            var page = doc.GetPage(i);
            // 색까지 가져온다 — 이 문서들에서 빨강은 공휴일이고, 흰색은 그림자 글꼴의 안 보이는 사본이다
            var glyphs = page.Letters
                .Where(l => keepSpaces ? l.Value.Length > 0 : !string.IsNullOrWhiteSpace(l.Value))
                .Select(l =>
                {
                    var (r, g, b) = l.Color.ToRGBValues();
                    return new TextGlyph(l.Value,
                        l.BoundingBox.Left, l.BoundingBox.Bottom, l.BoundingBox.Width, r, g, b);
                })
                .ToList();
            pages.Add(new DocumentPage(i, glyphs, ReadRules(page)));
        }

        return new DocumentLayout(Path.GetFileName(path), pages);
    }

    /// <summary>
    /// 표를 그린 선을 읽는다 — 얇고 긴 도형만 골라낸다.
    ///
    /// 평가계획 문서는 칸이 <b>맞붙어 있어</b> 글자만으로는 열을 나눌 수 없다.
    /// 선을 읽으면 열 경계가 정확히 나오고(실측: 세로줄 9개 = 8열),
    /// <b>가로줄이 어디까지 뻗었는지</b>로 세로 병합까지 알아낼 수 있다(2026-08-21).
    /// </summary>
    private static List<DocumentRule> ReadRules(UglyToad.PdfPig.Content.Page page)
    {
        // 선 굵기는 보통 0.5~1pt 다. 3pt 까지 선으로 보고, 20pt 넘게 뻗은 것만 표의 선으로 친다.
        const double Thin = 3, Long = 20;
        var rules = new List<DocumentRule>();

        foreach (var path in page.ExperimentalAccess.Paths)
        {
            var box = path.GetBoundingRectangle();
            if (box is null) continue;
            var r = box.Value;

            if (r.Width <= Thin && r.Height > Long)
                rules.Add(new DocumentRule(true, r.Left, r.Bottom, r.Top));
            else if (r.Height <= Thin && r.Width > Long)
                rules.Add(new DocumentRule(false, r.Bottom, r.Left, r.Right));
        }

        return rules;
    }

    /// <summary>
    /// pdf 는 그대로, hwp·hwpx 는 한컴으로 PDF 변환한 뒤 읽는다.
    /// 한컴이 없으면 <see cref="PlanFileExtractor"/> 와 같은 안내 예외가 난다 —
    /// 그때는 사용자가 직접 PDF 로 저장해서 넣으면 된다.
    /// </summary>
    public static DocumentLayout ExtractAny(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return Extract(path);

        if (ext is ".hwp" or ".hwpx")
        {
            var pdf = PlanFileExtractor.HwpToPdf(path);
            try { return Extract(pdf) with { FileName = Path.GetFileName(path) }; }
            finally { try { File.Delete(pdf); } catch { /* 임시파일 정리 실패는 무시 */ } }
        }

        throw new NotSupportedException($"지원하지 않는 형식입니다: {ext} (pdf·hwp·hwpx)");
    }
}
