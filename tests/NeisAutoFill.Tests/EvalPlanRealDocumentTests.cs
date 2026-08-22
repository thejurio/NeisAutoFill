using NeisAutoFill.Core.Evaluation;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Generator;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>
/// <b>실제 학교 문서</b>로 가져오기 전 과정을 확인한다 (읽기 → 표로 옮기기).
/// 합성 자료로는 못 잡는 것들 — 띄어쓰기가 사라진다든지, 쪽을 넘는 줄이 빠진다든지 — 을 잡는다.
///
/// 문서는 <c>docs/eval</c> 에 함께 들어 있다. 없으면 조용히 넘어간다(체크아웃 방식에 따라 없을 수 있다).
/// </summary>
public class EvalPlanRealDocumentTests
{
    private static readonly GradeScale Scale = GradePresets.ThreeLevel;

    /// <summary>테스트 실행 폴더에서 저장소 뿌리를 거슬러 찾는다.</summary>
    private static string? Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "docs", "eval", name);
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }
        return null;
    }

    private static PlanConversion? Read(string name)
    {
        var path = Sample(name);
        if (path is null) return null;

        return EvalPlanToWorkspace.Convert(
            EvalPlanDocumentParser.Parse(PdfLayoutExtractor.ExtractAny(path, keepSpaces: true)), Scale);
    }

    [Theory]
    [InlineData("2026학년도 5학년 1학기 평가계획.pdf")]
    [InlineData("2026학년도 5학년 2학기 평가계획.pdf")]
    [InlineData("스쿨마스터.pdf")]
    public void 세_양식_모두_교과와_평가기준을_읽어_낸다(string name)
    {
        var result = Read(name);
        if (result is null) return;   // 문서가 없는 체크아웃

        Assert.Equal(10, result.Plans.Count);
        Assert.All(result.Plans, p =>
        {
            Assert.NotEmpty(p.Domains);
            // 영역마다 세 단계가 다 있어야 한다 — 하나라도 비면 나이스에서 단계 수가 틀어진다
            foreach (var domain in p.Domains)
                Assert.Equal(3, Scale.Levels.Count(l => p.Criteria.ContainsKey((domain, l.Label))));
        });
    }

    /// <summary>
    /// <b>띄어쓰기가 살아 있어야 한다.</b> 뽑을 때 <c>keepSpaces</c> 를 빠뜨리면 공백 글자가 통째로
    /// 버려져 "책내용간추리고생각나누기" 처럼 붙어 버린다 — 그대로 나이스에 들어가면 안 된다.
    /// </summary>
    [Fact]
    public void 문장의_띄어쓰기가_살아_있다()
    {
        var result = Read("스쿨마스터.pdf");
        if (result is null) return;

        var korean = result.Plans.Single(p => p.SubjectName == "국어");
        var entry = korean.Criteria[("읽기", "잘함")];

        Assert.Contains(" ", entry.Text);
        Assert.StartsWith("[6국02-05] 긍정적인 읽기 동기를", entry.Achievement);
        Assert.Equal("책 내용 간추리고 생각 나누기", entry.Element);
    }

    /// <summary>한 영역에 평가가 여럿인 문서 — 줄이 그만큼 늘어나고, 진짜 영역명은 그대로 남는다.</summary>
    [Fact]
    public void 한_영역에_평가가_여럿이면_여러_줄로_나뉜다()
    {
        var result = Read("스쿨마스터.pdf");
        if (result is null) return;

        var social = result.Plans.Single(p => p.SubjectName == "사회");

        // '한국사' 한 영역에 평가가 셋 (선사·조선후기·일제강점기)
        var korean = social.Domains
            .Where(d => social.Criteria[(d, "잘함")].Area == "한국사").ToList();
        Assert.Equal(3, korean.Count);

        // 줄 이름은 겹치지 않고, 평가요소로 갈렸다
        Assert.Equal(3, korean.Distinct().Count());
        Assert.All(korean, d => Assert.StartsWith("한국사 · ", d));

        // 평가마다 성취기준이 따로다 — 합쳐지지 않았다
        Assert.Equal(3, korean.Select(d => social.Criteria[(d, "잘함")].Achievement).Distinct().Count());
    }

    /// <summary>교과를 못 가린 것(창체·학교자율시간)은 애초에 빠진다 — 사용자 결정 2026-08-21.</summary>
    [Fact]
    public void 교과를_못_가린_것은_들어오지_않는다()
    {
        var result = Read("2026학년도 5학년 2학기 평가계획.pdf");
        if (result is null) return;

        Assert.DoesNotContain(result.Plans, p => p.SubjectName.Contains("활동"));
    }
}
