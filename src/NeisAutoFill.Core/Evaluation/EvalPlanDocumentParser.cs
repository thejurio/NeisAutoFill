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
        var ignored = new List<string>();

        RuledTable? previous = null;
        EvalTableColumns? previousColumns = null;
        Pending? pending = null;   // 쪽을 넘어 이어질 수 있는 직전 성취기준

        foreach (var page in layout.Pages)
        {
            var table = new RuledTable(page);
            if (table.ColumnCount < 4 || table.RowCount < 1) continue;

            var header = Enumerable.Range(0, table.ColumnCount).Select(c => table.Cell(0, c)).ToList();
            var cols = EvalTableColumns.Detect(header);

            // 머리글이 없고 열 경계가 앞쪽과 같으면 <b>이어지는 표</b>다 —
            // 표 한 줄이 쪽 경계에서 잘려 나머지가 다음 쪽으로 넘어간 것이다(실측: 스쿨마스터 7→8쪽).
            var continued = false;
            if (!cols.IsUsable && previous is not null && previousColumns is not null &&
                table.SameColumns(previous))
            {
                cols = previousColumns;
                continued = true;
            }
            else if (!cols.IsUsable) continue;

            previous = table;
            previousColumns = cols;

            foreach (var (first, last) in table.GroupRows(cols.BlockColumn, firstRow: continued ? 0 : 1))
            {
                // 이어지는 쪽에서 영역 칸이 비어 있으면 <b>앞 성취기준의 뒷부분</b>이다
                if (continued && pending is not null &&
                    table.Span(first, last, cols.Area).Trim().Length == 0)
                {
                    Continue(pending, table, cols, first, last);
                    continue;
                }

                // 묶음 끝에 붙은 꼬리말 줄(평가결과가 없는 줄)은 잘라낸다 —
                // 안 그러면 "…평균 구하기▶ 2학기" 처럼 쪽 꼬리말이 평가요소에 딸려 온다(실측).
                var end = LastWithResult(table, cols, first, last);
                if (end < first) continue;

                var standard = table.Span(first, end, cols.Standard, trim: false);
                var area = table.Span(first, end, cols.Area).Trim();

                // 성취기준 열이 없는 표(학교자율시간)에서는 영역만으로도 한 건이다
                if (standard.Trim().Length == 0 && area.Length == 0) continue;
                if (IsHeaderRow(standard.Trim(), area)) continue;

                var criteria = new List<EvalCriterion>();
                for (var r = first; r <= end; r++)
                {
                    var result = table.Cell(r, cols.Result, trim: false);
                    if (result.Trim().Length == 0) continue;
                    criteria.Add(new EvalCriterion(table.Cell(r, cols.Level).Trim(), result));
                }

                if (criteria.Count == 0) continue;

                // 교과를 가리지 못하면 <b>넣지 않는다</b> — 나이스 평가계획은 교과(목) 단위다.
                // 창체·학교자율시간, 그리고 표에 섞인 쪽 꼬리말이 여기 걸린다.
                // 조용히 버리지 않고 무엇을 뺐는지 남긴다(사용자 결정 2026-08-21).
                var subject = EvalSubjectCode.Of(standard);
                var areaName = area.Length > 0 ? area : "(영역 미확인)";

                if (subject is null)
                {
                    if (!ignored.Contains(areaName)) ignored.Add(areaName);
                    pending = null;
                    continue;
                }


                if (!bySubject.TryGetValue(subject, out var areas))
                {
                    bySubject[subject] = areas = new Dictionary<string, List<EvalStandard>>();
                    order.Add(subject);
                }

                if (!areas.TryGetValue(areaName, out var list))
                    areas[areaName] = list = new List<EvalStandard>();

                pending = new Pending(
                    standard, table.Span(first, end, cols.Element, trim: false), criteria, list);
                list.Add(pending.Build());
            }
        }

        var subjects = order
            .Select(s => new EvalSubjectPlan(s,
                bySubject[s].Select(a => new EvalArea(a.Key, a.Value)).ToList()))
            .ToList();

        return new EvalPlanDocument(subjects, Ignored: ignored);
    }

    /// <summary>평가결과가 들어 있는 <b>마지막</b> 줄. 없으면 <paramref name="first"/> 보다 작다.</summary>
    private static int LastWithResult(RuledTable table, EvalTableColumns cols, int first, int last)
    {
        for (var r = last; r >= first; r--)
            if (table.Cell(r, cols.Result).Trim().Length > 0) return r;

        return first - 1;
    }

    /// <summary>
    /// 아직 이어질 수 있는 성취기준 — 쪽이 넘어가면 뒷부분이 붙는다.
    /// 목록에 이미 넣어 두고, 이어질 때마다 <b>그 자리를 갈아 끼운다</b>.
    /// </summary>
    private sealed class Pending(
        string standard, string element, List<EvalCriterion> criteria, List<EvalStandard> owner)
    {
        public string Standard { get; set; } = standard;

        public string Element { get; set; } = element;

        public List<EvalCriterion> Criteria { get; } = criteria;

        /// <summary>쪽을 넘어 이어붙였는가.</summary>
        public bool Continued { get; set; }

        public EvalStandard Build() => new(
            Standard.Trim(), Element.Trim(),
            Criteria.Select(c => new EvalCriterion(c.Level, c.Result.Trim())).ToList(),
            SpansPages: Continued);

        public void Flush()
        {
            if (owner.Count > 0) owner[^1] = Build();
        }
    }

    /// <summary>
    /// 이어지는 쪽의 내용을 앞 성취기준에 붙인다.
    ///
    /// 단계 이름이 <b>빈 줄</b>은 앞 단계 문장의 뒷부분이고, 이름이 있으면 새 단계다.
    /// 글을 붙일 때 <b>공백을 지우지 않는다</b> — <c>프로그램을␣</c>+<c>작성한다</c> 와
    /// <c>연산</c>+<c>자␣및</c> 처럼, 띄어야 할지 붙여야 할지는 원문 공백만이 안다.
    /// </summary>
    private static void Continue(
        Pending pending, RuledTable table, EvalTableColumns cols, int first, int last)
    {
        last = LastWithResult(table, cols, first, last);
        if (last < first) return;

        pending.Standard += table.Span(first, last, cols.Standard, trim: false);
        pending.Element += table.Span(first, last, cols.Element, trim: false);

        for (var r = first; r <= last; r++)
        {
            var result = table.Cell(r, cols.Result, trim: false);
            if (result.Trim().Length == 0) continue;

            var level = table.Cell(r, cols.Level).Trim();

            if (level.Length == 0 && pending.Criteria.Count > 0)
                pending.Criteria[^1] = pending.Criteria[^1] with
                    { Result = pending.Criteria[^1].Result + result };
            else
                pending.Criteria.Add(new EvalCriterion(level, result));
        }

        pending.Continued = true;
        pending.Flush();
    }

    /// <summary>
    /// 머리글이 <b>쪽마다 되풀이</b>된다 — 자료인 척 섞여 들어온다(실측: 문서마다 6~10건).
    /// 성취기준 칸에 "성취기준" 같은 말만 들어 있으면 머리글이다.
    /// </summary>
    private static bool IsHeaderRow(string standard, string area) =>
        (standard.Length <= 8 && HeaderWords.Contains(standard.Replace(" ", ""))) ||
        (area.Length <= 8 && HeaderWords.Contains(area.Replace(" ", "")) && standard.Length <= 8);
}
