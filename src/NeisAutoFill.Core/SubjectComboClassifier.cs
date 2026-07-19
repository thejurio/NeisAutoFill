namespace NeisAutoFill.Core;

/// <summary>
/// 나이스 화면 콤보의 aria-label 을 분류해 '과목 콤보'를 찾는 순수 로직.
///
/// 정상 화면: 과목 콤보 라벨 = "교과, 국어".
/// ★ 종합의견 화면 버그: 라벨이 전부 "학기, …" 로 잘못 붙는다 (2026-07-17 실측).
///   이때는 조회조건 콤보(학년도/학년/학기/반) 중 값이 숫자가 아닌 것을 과목으로 본다(폴백).
///
/// 폴백이 발동했는지를 결과로 드러내, 엔진이 로그에 남길 수 있게 한다
/// (나이스가 버그를 고쳐 정상 라벨이 돌아오면 폴백은 자연히 안 쓰이고, 그 사실이 로그로 보인다).
/// </summary>
public static class SubjectComboClassifier
{
    public enum Kind { NotACombo, Subject, QueryConditionCandidate }

    private static readonly string[] QueryKeys = { "학년도", "학년", "학기", "반" };

    /// <summary>라벨 한 개 분류. Subject = 정상 과목 콤보, QueryConditionCandidate = 폴백 후보.</summary>
    public static (Kind Kind, string? Value) Classify(string? ariaLabel)
    {
        if (string.IsNullOrWhiteSpace(ariaLabel)) return (Kind.NotACombo, null);
        var parts = ariaLabel.Split(',', 2);
        if (parts.Length != 2) return (Kind.NotACombo, null);

        var key = parts[0].Trim();
        var value = parts[1].Trim();

        if (key == "교과") return (Kind.Subject, value);   // 정상 라벨

        // 조회조건 콤보인데 값이 숫자(연도·학기·반)가 아니면 과목명일 가능성 → 폴백 후보
        if (Array.IndexOf(QueryKeys, key) >= 0 && value != "" && !int.TryParse(value, out _))
            return (Kind.QueryConditionCandidate, value);

        return (Kind.NotACombo, null);
    }

    /// <summary>여러 라벨 중 과목 콤보의 인덱스·값·폴백여부를 고른다. 못 찾으면 Index=-1.</summary>
    public static (int Index, string? Value, bool UsedFallback) Pick(IReadOnlyList<string?> ariaLabels)
    {
        int fallbackIdx = -1; string? fallbackValue = null;
        for (int i = 0; i < ariaLabels.Count; i++)
        {
            var (kind, value) = Classify(ariaLabels[i]);
            if (kind == Kind.Subject) return (i, value, false);   // 정상 라벨 우선 — 즉시 확정
            if (kind == Kind.QueryConditionCandidate && fallbackIdx < 0)
            {
                fallbackIdx = i; fallbackValue = value;   // 첫 후보만 기억, 정상 라벨이 없을 때만 사용
            }
        }
        return fallbackIdx >= 0 ? (fallbackIdx, fallbackValue, true) : (-1, null, false);
    }

    /// <summary>조회조건 콤보를 라벨 키로 찾는다 (전담 반·학년 전환용, F9 M6).
    /// 라벨 "학년, 5" → key="학년"이면 매칭. 반환: (인덱스, 현재값). 못 찾으면 (-1, null).
    ///
    /// ★ 실측 버그(2026-07-19): 교과별 평가 화면에 "학년" 라벨 콤보가 둘이다 —
    ///   학년도(예: "학년, 2026")와 진짜 학년("학년, 5"). prefer 로 값이 학년(1~6)인 쪽을 고른다.
    /// prefer 가 참인 후보를 우선, 없으면 첫 key 일치를 폴백으로 돌려준다.</summary>
    public static (int Index, string? Value) FindQueryCombo(
        IReadOnlyList<string?> ariaLabels, string key, Func<string, bool>? prefer = null)
    {
        int firstIdx = -1; string? firstVal = null;
        for (int i = 0; i < ariaLabels.Count; i++)
        {
            var label = ariaLabels[i];
            if (string.IsNullOrWhiteSpace(label)) continue;
            var parts = label.Split(',', 2);
            if (parts.Length != 2 || parts[0].Trim() != key) continue;
            var val = parts[1].Trim();
            if (prefer is null || prefer(val)) return (i, val);   // 선호 조건 맞으면 즉시 확정
            if (firstIdx < 0) { firstIdx = i; firstVal = val; }   // 아니면 첫 후보만 폴백으로 기억
        }
        return (firstIdx, firstVal);
    }

    /// <summary>종합의견·세특 화면의 학년·반·교과 콤보 인덱스를 값으로 찾는다 (F9 M10).
    /// 이 화면들은 라벨이 전부 "학기"로 깨져 있어 라벨로는 못 찾는다 → 값의 성질로 판별:
    ///   · 학년도 = 4자리 연도(2026)   · 교과 = 숫자가 아닌 값(국어)
    ///   · 나머지 숫자 콤보를 화면 순서대로 = [학기, 학년, 반] → 2번째=학년, 3번째=반
    /// 조회조건 콤보(라벨 key ∈ 학년도/학년/학기/반/교과)만 대상. 못 찾으면 인덱스 -1.</summary>
    public static (int GradeIndex, int ClassIndex, int SubjectIndex) ClassifyNarrativeAxis(IReadOnlyList<string?> ariaLabels)
    {
        var query = new List<(int Index, string Value)>();
        for (int i = 0; i < ariaLabels.Count; i++)
        {
            var label = ariaLabels[i];
            if (string.IsNullOrWhiteSpace(label)) continue;
            var parts = label.Split(',', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            if (key is "학년도" or "학년" or "학기" or "반" or "교과")
                query.Add((i, parts[1].Trim()));
        }

        int subjectIdx = -1;
        var numericNonYear = new List<int>();   // 학기·학년·반 (연도 제외한 숫자 콤보, 화면 순서)
        foreach (var (idx, value) in query)
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            bool isNumeric = digits.Length > 0 && digits == value.Trim();
            if (!isNumeric) { if (subjectIdx < 0) subjectIdx = idx; continue; }   // 비숫자 = 교과
            if (digits.Length == 4) continue;                                     // 4자리 = 학년도 (건너뜀)
            numericNonYear.Add(idx);                                              // 학기/학년/반 후보
        }

        // 순서: [학기, 학년, 반] → index 1 = 학년, index 2 = 반
        int gradeIdx = numericNonYear.Count >= 2 ? numericNonYear[1] : -1;
        int classIdx = numericNonYear.Count >= 3 ? numericNonYear[2] : -1;
        return (gradeIdx, classIdx, subjectIdx);
    }
}
