using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 런타임 카탈로그와 정렬 독립 지문 (기술설계 §5·§10, D-001·D-004).
/// 실제 교사명·계정은 쓰지 않고 가상값으로만 검증한다(D-012).
/// </summary>
public class TimetableCatalogTests
{
    private static readonly string[] 메뉴 =
    {
        "국어(교사A(account-a))",
        "체육(교사A(account-a))",
        "체육(교사B(account-b))",
        "자율·자치활동(교사A(account-a))",
        "행사처리",
        "수업삭제",
        "취소",
    };

    private static TimetableCatalog 만들기(IEnumerable<string> menu) =>
        new(TimetableMenuParser.ParseAll(menu.ToList()));

    [Fact]
    public void 후보와_명령을_나눈다()
    {
        var c = 만들기(메뉴);

        Assert.Equal(4, c.Assignable.Count);   // 국어, 체육A, 체육B, 창체
        Assert.Equal(3, c.Commands.Count);     // 행사처리, 수업삭제, 취소
        Assert.Empty(c.Unknown);
    }

    [Fact]
    public void 안정_키로_항목을_찾는다()
    {
        var c = 만들기(메뉴);
        var 체육B = TimetableMenuParser.Parse("체육(교사B(account-b))");

        var found = c.Find(체육B.StableKey);

        Assert.NotNull(found);
        Assert.Equal("교사B", found!.TeacherName);
    }

    [Fact]
    public void 사라진_대상은_null_로_알려준다()
    {
        // 저장된 규칙의 교사가 목록에서 빠진 상황 — 자동 실행하면 안 된다
        var c = 만들기(메뉴.Where(m => !m.Contains("교사B")));

        Assert.Null(c.Find("L|체육|교사b|account-b"));
    }

    [Fact]
    public void 같은_과목의_교사가_여럿이면_모두_후보로_남는다()
    {
        var c = 만들기(메뉴);

        var 체육 = c.FindBySubject("체육");

        Assert.Equal(2, 체육.Count);   // 자동 확정 금지(D-006) — 사용자가 골라야 한다
    }

    [Fact]
    public void 지문은_메뉴_순서와_무관하다()
    {
        var a = 만들기(메뉴);
        var b = 만들기(메뉴.Reverse());

        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void 항목이_바뀌면_지문도_바뀐다()
    {
        var a = 만들기(메뉴);
        var b = 만들기(메뉴.Where(m => !m.Contains("교사B")));

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);   // 재확인 트리거
    }

    [Fact]
    public void 명령이_바뀌어도_후보가_같으면_지문은_같다()
    {
        // 지문은 "입력 대상"의 구성만 본다 — 명령 목록 변화로 매핑을 무효화할 이유는 없다
        var a = 만들기(메뉴);
        var b = 만들기(메뉴.Concat(new[] { "공백삽입" }));

        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void 해석하지_못한_항목은_따로_모은다()
    {
        var c = 만들기(메뉴.Concat(new[] { "이상한(항목" }));

        Assert.Single(c.Unknown);
        Assert.DoesNotContain(c.Assignable, o => o.RawText == "이상한(항목");
    }
}
