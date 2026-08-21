using NeisAutoFill.Core.Evaluation;

namespace NeisAutoFill.Tests;

/// <summary>
/// 평가계획 문서 읽기 (기술설계 §5). 여기 있는 것은 <b>PDF 없이</b> 돌아가는 순수 판정이다.
/// 표 복원 자체는 실제 문서로 검산한다(로드맵 E1 진행 기록).
/// </summary>
public class EvalPlanParsingTests
{
    // ── 단계 수 판정 (E-002) ───────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void 나이스가_받는_단계_수는_그대로_쓴다(int count)
    {
        Assert.Equal(count, EvalLevelScale.Resolve(count, out var why));
        Assert.Null(why);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(8)]
    public void 나이스가_모르는_단계_수는_멈춘다(int count)
    {
        // 가까운 값으로 슬쩍 맞추지 않는다 — 틀린 채로 들어가는 것이 더 나쁘다
        Assert.Null(EvalLevelScale.Resolve(count, out var why));
        Assert.Contains($"{count}줄", why);
    }

    [Fact]
    public void 평가기준이_없으면_단계를_정할_수_없다()
    {
        Assert.Null(EvalLevelScale.Resolve(0, out var why));
        Assert.Contains("없습니다", why);
    }

    [Fact]
    public void 단계_수는_평가기준_줄_수로_센다()
    {
        // 문서에는 "3단계"라고 적혀 있지 않다. 잘함·보통·노력요함 세 줄이 있을 뿐이다.
        var s = new EvalStandard("성취기준", "평가요소", new[]
        {
            new EvalCriterion("잘함", "…"),
            new EvalCriterion("보통", "…"),
            new EvalCriterion("노력요함", "…"),
        });

        Assert.Equal(3, s.LevelCount);
        Assert.Equal("3단계", EvalLevelScale.Label(s.LevelCount));
    }

    // ── 성취기준 코드로 교과 가리기 ─────────────────────────

    [Theory]
    [InlineData("[6국04-05] 글과 담화에 쓰인…", "국어")]
    [InlineData("[6도03-01] 인권과 관련된…", "도덕")]
    [InlineData("[6수01-09] 분수의 곱셈의…", "수학")]
    [InlineData("[6실04-02] 생활 속 디지털…", "실과")]
    [InlineData("[6영01-03] 간단한 단어…", "영어")]
    public void 성취기준_코드에서_교과를_읽는다(string text, string subject)
        => Assert.Equal(subject, EvalSubjectCode.Of(text));

    [Theory]
    [InlineData("")]
    [InlineData("코드가 없는 성취기준 문장")]
    [InlineData("[6xx01-01] 모르는 약자")]
    public void 코드를_못_알아보면_교과를_지어내지_않는다(string text)
        => Assert.Null(EvalSubjectCode.Of(text));

    // ── 머리글로 열 찾기 (양식마다 다르다) ──────────────────

    [Fact]
    public void 이지에듀_1학기_양식의_열을_찾는다()
    {
        var c = EvalTableColumns.Detect(new[]
            { "영역", "성취기준", "단원명", "평가방법평가요소", "수업 방법", "", "평가기준", "평가시기" });

        Assert.True(c.IsUsable);
        Assert.Equal(new EvalTableColumns(Area: 0, Standard: 1, Element: 3, Level: 5, Result: 6), c);
    }

    [Fact]
    public void 같은_학교_2학기_양식은_열_순서도_이름도_다르다()
    {
        // 평가기준 열이 "성취수준" 으로 적혀 있고, 성취기준이 맨 앞이다 (실측 2026-08-21)
        var c = EvalTableColumns.Detect(new[]
            { "성취기준", "단원명", "영역", "평가요소", "수업 평가방법", "", "성취수준", "평가시기" });

        Assert.True(c.IsUsable);
        Assert.Equal(new EvalTableColumns(Area: 2, Standard: 0, Element: 3, Level: 5, Result: 6), c);
    }

    [Fact]
    public void 스쿨마스터_양식의_열을_찾는다()
    {
        var c = EvalTableColumns.Detect(new[]
            { "시기", "성취기준", "단원명", "평가 영역", "평가 요소", "수업․평가 방법", "", "평가 기준" });

        Assert.True(c.IsUsable);
        Assert.Equal(new EvalTableColumns(Area: 3, Standard: 1, Element: 4, Level: 6, Result: 7), c);
    }

    [Fact]
    public void 평가기준_열이_없으면_쓸_수_없다고_한다()
    {
        var c = EvalTableColumns.Detect(new[] { "번호", "이름", "비고" });

        Assert.False(c.IsUsable);
    }

    [Fact]
    public void 평가단계_열은_머리글이_비어_있어_평가기준_왼쪽으로_찾는다()
    {
        var c = EvalTableColumns.Detect(new[] { "성취기준", "", "평가기준" });

        Assert.Equal(1, c.Level);
        Assert.Equal(2, c.Result);
    }

    // ── 교과를 못 가리는 것은 뺀다 (E-008) ──────────────────

    [Fact]
    public void 뺀_영역은_세어서_알린다()
    {
        var doc = new EvalPlanDocument(
            new[] { new EvalSubjectPlan("국어", new[] { new EvalArea("문법", Array.Empty<EvalStandard>()) }) },
            Ignored: new[] { "자율 활동", "동아리 활동" });

        // 조용히 버리지 않는다 — 몇 건을 왜 뺐는지 사용자가 알아야 한다
        Assert.Equal(2, doc.Skipped.Count);
        Assert.Contains("뺀 영역 2", doc.Describe());
    }

    [Fact]
    public void 뺀_것이_없으면_설명에_나오지_않는다()
    {
        var doc = new EvalPlanDocument(Array.Empty<EvalSubjectPlan>());

        Assert.Empty(doc.Skipped);
        Assert.DoesNotContain("뺀 영역", doc.Describe());
    }
}
