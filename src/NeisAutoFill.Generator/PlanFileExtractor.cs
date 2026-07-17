using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NeisAutoFill.Generator;

/// <summary>
/// 평가계획서 파일에서 AI 분석용 내용 추출.
///  pdf  → 원문 그대로 (base64, Gemini 가 레이아웃까지 직접 읽음 — 품질 최상)
///  hwpx → zip 안 section XML 에서 텍스트 추출 (표는 탭/줄바꿈으로 평탄화)
///  hwp  → 한컴오피스 COM 으로 PDF 변환 후 pdf 경로 (미설치 시 안내 오류)
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]   // hwp 변환이 한컴 COM 의존 (프로그램 자체가 Windows 전용)
public static class PlanFileExtractor
{
    /// <summary>PdfBase64 와 Text 중 하나만 채워진다. Method 는 로그용 설명.</summary>
    public sealed record Extraction(string? PdfBase64, string? Text, string Method);

    public static Extraction Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".pdf":
                return new(Convert.ToBase64String(File.ReadAllBytes(path)), null, "PDF 원문");

            case ".hwpx":
                var text = ExtractHwpxText(path);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("hwpx 에서 텍스트를 추출하지 못했습니다. PDF 로 저장해서 넣어 보세요.");
                return new(null, text, "HWPX 텍스트");

            case ".hwp":
                var pdfPath = HwpToPdf(path);   // 한컴 미설치 시 안내 예외
                try
                {
                    return new(Convert.ToBase64String(File.ReadAllBytes(pdfPath)), null, "HWP→PDF 변환");
                }
                finally
                {
                    try { File.Delete(pdfPath); } catch { /* 임시파일 정리 실패는 무시 */ }
                }

            default:
                throw new InvalidOperationException($"지원하지 않는 형식입니다: {ext} (pdf / hwpx / hwp 만 가능)");
        }
    }

    // ── HWPX (zip + XML) ─────────────────────

    public static string ExtractHwpxText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            sb.AppendLine(FlattenSectionXml(reader.ReadToEnd()));
        }
        return sb.ToString().Trim();
    }

    /// <summary>섹션 XML → 평문. 표 셀은 탭, 행·문단은 줄바꿈으로 구분해 표 구조를 남긴다.</summary>
    public static string FlattenSectionXml(string xml)
    {
        var s = Regex.Replace(xml, "</hp:p>\\s*</hp:tc>", "\t");   // 셀 마지막 문단 = 셀 경계 (탭 보존)
        s = s.Replace("</hp:tc>", "\t")     // 표 셀 경계
             .Replace("</hp:tr>", "\n")     // 표 행 경계
             .Replace("</hp:p>", "\n");     // 문단 경계
        s = Regex.Replace(s, "<[^>]+>", "");                       // 태그 제거
        s = System.Net.WebUtility.HtmlDecode(s);                   // &amp; 등 복원
        s = Regex.Replace(s, " *\n *", "\n");                      // 줄 주변 공백 정리 (탭은 보존)
        s = Regex.Replace(s, "\t\n", "\n");                        // 행 끝 잉여 탭 제거
        s = Regex.Replace(s, "\n{3,}", "\n\n");                    // 과다 빈 줄 압축
        return s.Trim();
    }

    // ── HWP → PDF (한컴오피스 COM) ────────────

    /// <summary>한컴오피스로 hwp → 임시 pdf 변환. 반환 = pdf 경로 (호출자가 삭제).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string HwpToPdf(string hwpPath)
    {
        var progType = Type.GetTypeFromProgID("HWPFrame.HwpObject");
        if (progType is null)
            throw new InvalidOperationException(
                "이 PC 에 한컴오피스(한글)가 없어 .hwp 를 변환할 수 없습니다.\n" +
                "한글에서 [파일 > PDF로 저장] 하거나 [다른 이름으로 저장 > hwpx] 후 그 파일을 넣어 주세요.");

        dynamic? hwp = null;
        try
        {
            hwp = Activator.CreateInstance(progType)!;
            // 보안 승인 대화상자 억제 (한컴 기본 제공 모듈 — 미등록 환경이면 최초 1회 승인 창이 뜰 수 있음)
            try { hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }

            if (!(bool)hwp.Open(hwpPath, "HWP", "forceopen:true"))
                throw new InvalidOperationException("한글이 파일을 열지 못했습니다: " + Path.GetFileName(hwpPath));

            var pdfPath = Path.Combine(Path.GetTempPath(), $"neisautofill_{Guid.NewGuid():N}.pdf");
            if (!(bool)hwp.SaveAs(pdfPath, "PDF", ""))
                throw new InvalidOperationException("한글이 PDF 저장에 실패했습니다.");
            return pdfPath;
        }
        finally
        {
            try { hwp?.Quit(); } catch { }
            if (hwp is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(hwp);
        }
    }
}
