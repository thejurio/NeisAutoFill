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
}
