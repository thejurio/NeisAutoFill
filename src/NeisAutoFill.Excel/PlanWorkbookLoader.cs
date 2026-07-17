using ClosedXML.Excel;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Excel;

/// <summary>
/// 1단계 평가계획서(평가기준 기안표) 파서. Index.html analyzeStep1File 이식.
/// - "학생명단" 시트 제외, 등급 라벨이 한 번도 안 나오는 빈 과목 시트 무시
/// - 헤더 순서 무관: "영역"/"성취" 포함 컬럼 탐색, 기준내용은 "내용"/"서술" 포함 컬럼
/// - 영역·성취기준은 병합 표기를 위해 빈 셀이면 직전 값 캐리포워드
/// - 등급 감지가 활성 척도(GradeScale) 라벨 기반 — 3단계 하드코딩 제거 (§9.2)
/// </summary>
public static class PlanWorkbookLoader
{
    public static IReadOnlyList<SubjectPlan> Load(string path, GradeScale scale)
    {
        using var wb = new XLWorkbook(path);
        var result = new List<SubjectPlan>();
        var labels = scale.Labels;

        foreach (var ws in wb.Worksheets)
        {
            var sheetName = ws.Name.Trim();
            if (sheetName == "학생명단") continue;

            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            if (lastCol == 0 || lastRow < 2) continue;

            // 셀 값을 미리 읽어 2차원 배열로 (행 순회 다회라 효율·가독 우선)
            var cells = new string[lastRow + 1, lastCol + 1];
            for (int r = 1; r <= lastRow; r++)
                for (int c = 1; c <= lastCol; c++)
                    cells[r, c] = ws.Cell(r, c).GetString().Trim();

            // 실데이터 검사: 헤더 아래에 척도 라벨이 하나도 없으면 빈 시트로 간주
            bool hasRealData = false;
            for (int r = 2; r <= lastRow && !hasRealData; r++)
                for (int c = 1; c <= lastCol; c++)
                    if (labels.Contains(cells[r, c])) { hasRealData = true; break; }
            if (!hasRealData) continue;

            // 헤더 컬럼 탐색 (공백 제거 후 포함 검사)
            int domCol = FindHeader(cells, lastCol, h => h.Contains("영역"));
            int achCol = FindHeader(cells, lastCol, h => h.Contains("성취"));
            int descCol = FindHeader(cells, lastCol, h => h.Contains("내용") || h.Contains("서술"));
            if (domCol < 0) domCol = 1;
            if (achCol < 0) achCol = 2;

            var domains = new List<string>();
            var criteria = new Dictionary<(string, string), CriteriaEntry>();
            string lastDom = "", lastAch = "";

            for (int r = 2; r <= lastRow; r++)
            {
                var domVal = cells[r, domCol];
                if (domVal != "" && !domVal.Contains("영역"))
                {
                    lastDom = domVal;
                    if (!domains.Contains(lastDom)) domains.Add(lastDom);
                }

                var achVal = cells[r, achCol];
                if (achVal != "" && !achVal.Contains("성취")) lastAch = achVal;

                // 등급 셀: 행 안에서 척도 라벨과 정확 일치하는 첫 셀
                string grade = ""; int gradeCol = -1;
                for (int c = 1; c <= lastCol; c++)
                    if (labels.Contains(cells[r, c])) { grade = cells[r, c]; gradeCol = c; break; }
                if (grade == "") continue;

                // 기준 내용: desc 컬럼 우선, 없으면 등급 셀 오른쪽 첫 비어있지 않은 셀
                string desc = descCol > 0 ? cells[r, descCol] : "";
                if (desc == "")
                    for (int c = gradeCol + 1; c <= lastCol; c++)
                        if (cells[r, c] != "") { desc = cells[r, c]; break; }

                if (lastDom != "" && desc != "")
                    criteria[(lastDom, grade)] = new CriteriaEntry(
                        desc, lastAch == "" ? null : lastAch);
            }

            if (domains.Count > 0)
                result.Add(new SubjectPlan(sheetName, domains, criteria));
        }

        return result;
    }

    /// <summary>
    /// 평가계획서 파일인지 판별. 평가계획서 양식에만 [학생명단] 시트가 있다는 규약을 이용
    /// (성적입력 양식은 과목 시트뿐). 드래그앤드롭 자동 라우팅용.
    /// </summary>
    public static bool LooksLikePlan(string path)
    {
        using var wb = new XLWorkbook(path);
        return wb.Worksheets.Any(w => w.Name.Trim() == "학생명단");
    }

    /// <summary>평가계획서에 [학생명단] 시트가 있는지 — 있으면 그 내용(비어 있어도)이 명단의 전부다.</summary>
    public static bool HasRosterSheet(string path)
    {
        using var wb = new XLWorkbook(path);
        return wb.Worksheets.Any(w => w.Name.Trim() == "학생명단");
    }

    /// <summary>평가계획서의 [학생명단] 시트에서 (번호, 이름) 목록을 읽는다. 없으면 빈 목록.</summary>
    public static IReadOnlyList<(string No, string Name)> LoadRoster(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Trim() == "학생명단")
                 ?? wb.Worksheets.FirstOrDefault();
        if (ws is null) return Array.Empty<(string, string)>();

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var roster = new List<(string, string)>();
        for (int r = 2; r <= lastRow; r++)
        {
            var name = ws.Cell(r, 2).GetString().Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var no = ws.Cell(r, 1).GetString().Trim();
            roster.Add((no == "" ? (r - 1).ToString() : no, name));
        }
        return roster;
    }

    private static int FindHeader(string[,] cells, int lastCol, Func<string, bool> match)
    {
        for (int c = 1; c <= lastCol; c++)
        {
            var h = cells[1, c].Replace(" ", "");
            if (h != "" && match(h)) return c;
        }
        return -1;
    }
}
