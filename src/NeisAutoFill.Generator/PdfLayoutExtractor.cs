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
    public static DocumentLayout Extract(string path)
    {
        using var doc = PdfDocument.Open(path);
        var pages = new List<DocumentPage>(doc.NumberOfPages);

        for (var i = 1; i <= doc.NumberOfPages; i++)
        {
            var page = doc.GetPage(i);
            var glyphs = page.Letters
                .Where(l => !string.IsNullOrWhiteSpace(l.Value))
                .Select(l => new TextGlyph(l.Value, l.BoundingBox.Left, l.BoundingBox.Bottom, l.BoundingBox.Width))
                .ToList();
            pages.Add(new DocumentPage(i, glyphs));
        }

        return new DocumentLayout(Path.GetFileName(path), pages);
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
