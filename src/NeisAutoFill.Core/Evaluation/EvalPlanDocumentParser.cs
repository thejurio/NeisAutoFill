using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 평가계획 문서(PDF 로 변환된 한글)를 읽어 <see cref="EvalPlanDocument"/> 로 만든다.
///
/// <b>표의 선으로 칸을 되살린다</b>(<see cref="RuledTable"/>) — 칸이 맞붙어 있어
/// 글자 사이 빈틈으로는 열을 나눌 수 없기 때문이다.
/// 열이 무엇인지는 <b>머리글을 읽어</b> 정한다(<see cref="EvalTableColumns"/>) —
/// 실측한 세 양식이 열 순서도 이름도 서로 달랐다.
/// </summary>
public static class EvalPlanDocumentParser
{
    /// <summary>머리글에 나오는 말 — 이런 값이 자료 칸에 있으면 <b>되풀이된 머리글 행</b>이다.</summary>
    private static readonly string[] HeaderWords =
        { "영역", "성취기준", "평가요소", "평가기준", "성취수준", "단원명", "평가", "시기" };

    public static EvalPlanDocument Parse(DocumentLayout layout)
    {
        // 교과 → 영역명 → 성취기준들. 쪽을 넘나들며 같은 영역이 이어지므로 여기서 모은다.
        var bySubject = new Dictionary<string, Dictionary<string, List<EvalStandard>>>();
        var order = new List<string>();

        foreach (var page in layout.Pages)
        {
            var table = new RuledTable(page);
            if (table.ColumnCount < 4 || table.RowCount < 2) continue;

            var header = Enumerable.Range(0, table.ColumnCount).Select(c => table.Cell(0, c)).ToList();
            var cols = EvalTableColumns.Detect(header);
            if (!cols.IsUsable) continue;

            foreach (var (first, last) in table.GroupRows(cols.BlockColumn, firstRow: 1))
            {
                var standard = table.Span(first, last, cols.Standard).Trim();
                var area = table.Span(first, last, cols.Area).Trim();

                // 성취기준 열이 없는 표(학교자율시간)에서는 영역만으로도 한 건이다
                if (standard.Length == 0 && area.Length == 0) continue;
                if (IsHeaderRow(standard, area)) continue;

                var criteria = new List<EvalCriterion>();
                for (var r = first; r <= last; r++)
                {
                    var result = table.Cell(r, cols.Result).Trim();
                    if (result.Length == 0) continue;
                    criteria.Add(new EvalCriterion(table.Cell(r, cols.Level).Trim(), result));
                }

                if (criteria.Count == 0) continue;

                var subject = EvalSubjectCode.Of(standard) ?? "(교과 미확인)";
                var areaName = area.Length > 0 ? area : "(영역 미확인)";

                if (!bySubject.TryGetValue(subject, out var areas))
                {
                    bySubject[subject] = areas = new Dictionary<string, List<EvalStandard>>();
                    order.Add(subject);
                }

                if (!areas.TryGetValue(areaName, out var list))
                    areas[areaName] = list = new List<EvalStandard>();

                list.Add(new EvalStandard(
                    standard, table.Span(first, last, cols.Element).Trim(), criteria));
            }
        }

        var subjects = order
            .Select(s => new EvalSubjectPlan(s,
                bySubject[s].Select(a => new EvalArea(a.Key, a.Value)).ToList()))
            .ToList();

        return new EvalPlanDocument(subjects);
    }

    /// <summary>
    /// 머리글이 <b>쪽마다 되풀이</b>된다 — 자료인 척 섞여 들어온다(실측: 문서마다 6~10건).
    /// 성취기준 칸에 "성취기준" 같은 말만 들어 있으면 머리글이다.
    /// </summary>
    private static bool IsHeaderRow(string standard, string area) =>
        (standard.Length <= 8 && HeaderWords.Contains(standard.Replace(" ", ""))) ||
        (area.Length <= 8 && HeaderWords.Contains(area.Replace(" ", "")) && standard.Length <= 8);
}
