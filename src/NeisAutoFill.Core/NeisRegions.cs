namespace NeisAutoFill.Core;

/// <summary>시도 교육청 나이스 접속 지역.</summary>
public sealed record NeisRegion(string Code, string Name)
{
    /// <summary>4세대 나이스 접속 주소 — {교육청코드}.neis.go.kr 패턴 (서울 sen·경기 goe·전북 jbe 실확인).</summary>
    public string Url => $"https://{Code}.neis.go.kr";
}

/// <summary>17개 시도 교육청 목록. 코드 = 각 교육청 표준 약칭.</summary>
public static class NeisRegions
{
    public static readonly IReadOnlyList<NeisRegion> All = new NeisRegion[]
    {
        new("sen", "서울특별시"),
        new("pen", "부산광역시"),
        new("dge", "대구광역시"),
        new("ice", "인천광역시"),
        new("gen", "광주광역시"),
        new("dje", "대전광역시"),
        new("use", "울산광역시"),
        new("sje", "세종특별자치시"),
        new("goe", "경기도"),
        new("kwe", "강원특별자치도"),
        new("cbe", "충청북도"),
        new("cne", "충청남도"),
        new("jbe", "전북특별자치도"),
        new("jne", "전라남도"),
        new("gbe", "경상북도"),
        new("gne", "경상남도"),
        new("jje", "제주특별자치도"),
    };

    public static NeisRegion Find(string code) =>
        All.FirstOrDefault(r => r.Code == code) ?? All.First(r => r.Code == "jbe");
}
