using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 시간표 우클릭 메뉴 문자열 해석 (기술설계 §10, 로드맵 T1).
/// 여기가 틀리면 작업 명령을 수업으로 착각하거나 교사를 잘못 고른다 — 가장 위험한 파싱이다.
/// 실제 교사명·계정은 쓰지 않고 가상값(교사A/account-a)으로만 검증한다(D-012).
/// </summary>
public class TimetableMenuParserTests
{
    [Fact]
    public void 과목_교사_계정을_분리한다()
    {
        var o = TimetableMenuParser.Parse("국어(교사A(account-a))");

        Assert.Equal(TimetableOptionKind.Lesson, o.Kind);
        Assert.Equal("국어", o.Subject);
        Assert.Equal("교사A", o.TeacherName);
        Assert.Equal("account-a", o.TeacherAccount);
        Assert.True(o.IsAssignable);
    }

    [Fact]
    public void 과목명에_괄호가_있어도_오른쪽에서_교사를_찾는다()
    {
        // 왼쪽 첫 괄호에서 자르면 "체육(구기)" 의 "(구기)" 를 교사로 오해한다
        var o = TimetableMenuParser.Parse("체육(구기)(교사B(account-b))");

        Assert.Equal(TimetableOptionKind.Lesson, o.Kind);
        Assert.Equal("체육(구기)", o.Subject);
        Assert.Equal("교사B", o.TeacherName);
        Assert.Equal("account-b", o.TeacherAccount);
    }

    [Theory]
    [InlineData("결보강처리")]
    [InlineData("보강등록(현장체험학습)")]   // 괄호가 있어 수업처럼 보이는 함정
    [InlineData("행사처리")]
    [InlineData("공백삽입")]
    [InlineData("공백삭제")]
    [InlineData("수업삭제")]
    [InlineData("전체 수업삭제")]
    [InlineData("교사추가")]
    public void 작업_명령은_수업_후보가_아니다(string raw)
    {
        var o = TimetableMenuParser.Parse(raw);

        Assert.Equal(TimetableOptionKind.Command, o.Kind);
        Assert.False(o.IsAssignable);
    }

    [Fact]
    public void 취소는_별도_종류다()
    {
        var o = TimetableMenuParser.Parse("취소");

        Assert.Equal(TimetableOptionKind.Cancel, o.Kind);
        Assert.False(o.IsAssignable);
    }

    [Theory]
    [InlineData("자율·자치활동(교사A(account-a))", CreativeActivityKind.Autonomy)]
    [InlineData("동아리활동(교사A(account-a))", CreativeActivityKind.Club)]
    [InlineData("진로활동(교사A(account-a))", CreativeActivityKind.Career)]
    public void 창체_세_종류를_구분한다(string raw, CreativeActivityKind expected)
    {
        var o = TimetableMenuParser.Parse(raw);

        Assert.Equal(TimetableOptionKind.CreativeActivity, o.Kind);
        Assert.Equal(expected, o.CreativeKind);
        Assert.True(o.IsAssignable);
    }

    [Fact]
    public void 창체_가운뎃점_표기가_달라도_같게_읽는다()
    {
        // 화면마다 ·, ㆍ, ・ 가 섞여 나온다
        foreach (var dot in new[] { "·", "ㆍ", "・" })
        {
            var o = TimetableMenuParser.Parse($"자율{dot}자치활동(교사A(account-a))");
            Assert.Equal(CreativeActivityKind.Autonomy, o.CreativeKind);
        }
    }

    [Fact]
    public void 같은_과목의_두_교사는_서로_다른_옵션으로_남는다()
    {
        var a = TimetableMenuParser.Parse("체육(교사A(account-a))");
        var b = TimetableMenuParser.Parse("체육(교사B(account-b))");

        Assert.NotEqual(a.StableKey, b.StableKey);   // 자동으로 하나로 합쳐지면 안 된다(D-006)
    }

    [Fact]
    public void 동명이인은_계정으로_구별된다()
    {
        var a = TimetableMenuParser.Parse("수학(김교사(account-a))");
        var b = TimetableMenuParser.Parse("수학(김교사(account-b))");

        Assert.NotEqual(a.StableKey, b.StableKey);
    }

    [Fact]
    public void 교사_표기가_없으면_과목만_읽는다()
    {
        var o = TimetableMenuParser.Parse("창의적 체험활동");

        Assert.Equal("창의적 체험활동", o.Subject);
        Assert.Equal("", o.TeacherName);
        Assert.Equal("", o.TeacherAccount);
    }

    [Fact]
    public void 해석하지_못한_항목은_버리지_않고_Unknown_으로_남긴다()
    {
        var o = TimetableMenuParser.Parse("???(짝이 안 맞는 괄호");

        Assert.Equal(TimetableOptionKind.Unknown, o.Kind);
        Assert.False(o.IsAssignable);
        Assert.Equal("???(짝이 안 맞는 괄호", o.RawText);   // 원문 보존 — 사용자에게 보고해야 한다
    }

    [Fact]
    public void 빈_문자열은_Unknown()
    {
        Assert.Equal(TimetableOptionKind.Unknown, TimetableMenuParser.Parse("").Kind);
        Assert.Equal(TimetableOptionKind.Unknown, TimetableMenuParser.Parse(null).Kind);
    }

    [Fact]
    public void 안정키는_메뉴_순서와_무관하다()
    {
        string[] menu = { "국어(교사A(account-a))", "수학(교사B(account-b))", "취소" };
        string[] shuffled = { "취소", "수학(교사B(account-b))", "국어(교사A(account-a))" };

        var keys1 = TimetableMenuParser.ParseAll(menu).Select(o => o.StableKey).OrderBy(k => k);
        var keys2 = TimetableMenuParser.ParseAll(shuffled).Select(o => o.StableKey).OrderBy(k => k);

        Assert.Equal(keys1, keys2);   // uuid·순서가 바뀌어도 같은 카탈로그(D-004)
    }

    [Fact]
    public void 공백_차이는_안정키에_영향을_주지_않는다()
    {
        var a = TimetableMenuParser.Parse("국어(교사A(account-a))");
        var b = TimetableMenuParser.Parse("국어( 교사A (account-a) )");

        Assert.Equal(a.StableKey, b.StableKey);
    }

    // ── 2026-08-18 실기기에서 확인된 실제 형식 ────────────────────

    [Fact]
    public void 셀_표시값은_과목과_교사가_줄바꿈으로_나뉜다()
    {
        // 실측: 메뉴는 "과목(교사(계정))" 인데 셀 표시는 과목과 교사 사이에 줄바꿈이 들어간다
        var o = TimetableMenuParser.Parse("과학\n(교사A(account-a))");

        Assert.Equal(TimetableOptionKind.Lesson, o.Kind);
        Assert.Equal("과학", o.Subject);
        Assert.Equal("교사A", o.TeacherName);
    }

    [Fact]
    public void 셀_표시값과_메뉴_항목은_같은_안정_키를_만든다()
    {
        // 현재 값(셀)과 목표(메뉴)를 비교하려면 반드시 같은 키가 나와야 한다
        var fromCell = TimetableMenuParser.Parse("과학\n(교사A(account-a))");
        var fromMenu = TimetableMenuParser.Parse("과학(교사A(account-a))");

        Assert.Equal(fromMenu.StableKey, fromCell.StableKey);
    }

    [Fact]
    public void 계정이_샵으로_시작하는_내부_ID_여도_읽는다()
    {
        // 실측: 일반 ID 외에 "#P1000014282608001" 형태의 계정이 섞여 있다
        var o = TimetableMenuParser.Parse("과학(교사A(#P1000014282608001))");

        Assert.Equal("과학", o.Subject);
        Assert.Equal("#P1000014282608001", o.TeacherAccount);
    }

    [Fact]
    public void 실측_메뉴_29개를_후보_20개와_명령_9개로_가른다()
    {
        // 2026-08-18 실기기 구성 (교사명·계정은 가상값으로 치환)
        string[] menu =
        {
            "국어(교사A(account-a))", "사회(교사A(account-a))",
            "도덕(교사B(account-b))", "도덕(교사A(account-a))",
            "수학(교사C(account-c))", "수학(교사A(account-a))",
            "과학(교사D(#P1000014282608001))", "과학(교사E(account-e))", "과학(교사A(account-a))",
            "실과(교사A(account-a))", "체육(교사A(account-a))", "음악(교사A(account-a))",
            "미술(교사A(account-a))", "영어(교사F(account-f))", "영어(교사A(account-a))",
            "자율·자치활동(교사A(account-a))", "자율·자치활동(교사B(account-b))", "자율·자치활동(교사F(account-f))",
            "동아리활동(교사A(account-a))", "진로활동(교사A(account-a))",
            "결보강처리", "보강등록(현장체험학습)", "행사처리", "공백삽입", "공백삭제",
            "수업삭제", "전체 수업삭제", "교사추가", "취소",
        };

        var catalog = new TimetableCatalog(TimetableMenuParser.ParseAll(menu));

        Assert.Equal(29, catalog.All.Count);
        Assert.Equal(20, catalog.Assignable.Count);
        Assert.Equal(9, catalog.Commands.Count);
        Assert.Empty(catalog.Unknown);

        // 실제로 복수 교사가 존재한다 — 자동 확정 금지(D-006)의 근거
        Assert.Equal(3, catalog.FindBySubject("과학").Count);
        Assert.Equal(3, catalog.FindBySubject("자율·자치활동").Count);
    }
}
