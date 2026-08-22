using NeisAutoFill.Core.Evaluation;
using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>
/// 나이스 [일괄업로드] 엑셀 만들기 (E8). 양식이 조금이라도 다르면 나이스가 읽지 못하고,
/// 이미 올라간 것을 또 올리면 <b>두 벌이 된다</b>(E-004) — 그 둘을 지킨다.
/// </summary>
public class EvalUploadTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eval_{Guid.NewGuid():N}.xlsx");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static EvalStandard Std(string standard, string element) =>
        new(standard, element, new[] { new EvalCriterion("잘함", "…") });

    private static EvalSubjectPlan Plan() => new("국어", new[]
    {
        new EvalArea("읽기", new[] { Std("[6국02-05] 긍정적인 읽기 동기", "책 내용 간추리기") }),
        new EvalArea("한국사", new[]
        {
            Std("[6사04-01] 선사시대", "선사 추론"),
            Std("[6사05-02] 조선후기", "서민 문화"),
        }),
    });

    [Fact]
    public void 계획을_영역_순서대로_편다()
    {
        var rows = EvalUploadRows.Of(Plan());

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "읽기", "한국사", "한국사" }, rows.Select(r => r.Area));
        Assert.Equal("책 내용 간추리기", rows[0].Element);
    }

    /// <summary>여러 줄짜리 값은 한 줄로 — 엑셀 한 칸에 들어가야 한다.</summary>
    [Fact]
    public void 줄바꿈은_한_줄로_편다()
    {
        var plan = new EvalSubjectPlan("국어", new[]
        {
            new EvalArea("읽기", new[] { Std("[6국02-05] 앞\n[6국02-03] 뒤", "요소") }),
        });

        Assert.Equal("[6국02-05] 앞 [6국02-03] 뒤", Assert.Single(EvalUploadRows.Of(plan)).Standard);
    }

    [Fact]
    public void 영역이나_성취기준이_비면_올리지_않는다()
    {
        var plan = new EvalSubjectPlan("국어", new[]
        {
            new EvalArea("", new[] { Std("기준", "요소") }),
            new EvalArea("읽기", new[] { Std("", "요소") }),
        });

        Assert.Empty(EvalUploadRows.Of(plan));
    }

    /// <summary>나이스 업로드는 '추가'라, 이미 있는 것을 또 올리면 두 벌이 된다.</summary>
    [Fact]
    public void 화면에_이미_있는_성취기준은_뺀다()
    {
        var pending = EvalUploadRows.Pending(EvalUploadRows.Of(Plan()), new[]
        {
            ("읽기", "[6국02-05] 긍정적인 읽기 동기"),
        });

        Assert.Equal(2, pending.Count);
        Assert.DoesNotContain(pending, r => r.Area == "읽기");
    }

    /// <summary>문서와 화면은 띄어쓰기·줄바꿈이 곧잘 다르다 — 그것 때문에 두 벌이 되면 안 된다.</summary>
    [Fact]
    public void 공백이_달라도_같은_것으로_본다()
    {
        var pending = EvalUploadRows.Pending(EvalUploadRows.Of(Plan()), new[]
        {
            ("읽 기", "[6국02-05]긍정적인읽기동기"),
        });

        Assert.DoesNotContain(pending, r => r.Area == "읽기");
    }

    [Fact]
    public void 우리_목록_안에서_겹쳐도_한_번만_올린다()
    {
        var one = new EvalUploadRow("읽기", "기준", "요소");

        Assert.Single(EvalUploadRows.Pending(new[] { one, one }, Array.Empty<(string, string)>()));
    }

    /// <summary>
    /// 나이스가 받는 양식 그대로여야 한다 — <b>시트 이름과 머리글이 다르면 읽지 못한다</b>.
    /// (샘플엑셀다운로드 파일 실측: 시트 `empty0`, 1행 `영역명 │ 성취기준 │ 평가요소`)
    /// </summary>
    [Fact]
    public void 나이스_양식_그대로_쓴다()
    {
        EvalUploadWorkbook.Write(_path, EvalUploadRows.Of(Plan()));

        using var wb = new ClosedXML.Excel.XLWorkbook(_path);
        var ws = Assert.Single(wb.Worksheets);
        Assert.Equal("empty0", ws.Name);

        Assert.Equal("영역명", ws.Cell(1, 1).GetString());
        Assert.Equal("성취기준", ws.Cell(1, 2).GetString());
        Assert.Equal("평가요소", ws.Cell(1, 3).GetString());

        Assert.Equal("읽기", ws.Cell(2, 1).GetString());
        Assert.Equal("한국사", ws.Cell(3, 1).GetString());
        Assert.Equal("[6사05-02] 조선후기", ws.Cell(4, 2).GetString());
        Assert.Equal("서민 문화", ws.Cell(4, 3).GetString());
        Assert.Equal(4, ws.LastRowUsed()!.RowNumber());
    }

    /// <summary>
    /// <b>나이스가 준 샘플 파일과 직접 대조한다.</b> 위 테스트는 우리가 적어 둔 값과 맞추는 것이라
    /// 그 값 자체가 틀렸으면 잡지 못한다 — 여기서 원본과 견준다.
    /// (샘플이 없는 체크아웃이면 조용히 넘어간다.)
    /// </summary>
    [Fact]
    public void 나이스_샘플과_시트_이름도_머리글도_같다()
    {
        var sample = FindSample("5학년 국어.xlsx");
        if (sample is null) return;

        using var wb = new ClosedXML.Excel.XLWorkbook(sample);
        var ws = Assert.Single(wb.Worksheets);

        Assert.Equal(EvalUploadWorkbook.SheetName, ws.Name);
        for (var c = 0; c < EvalUploadWorkbook.Headers.Length; c++)
            Assert.Equal(EvalUploadWorkbook.Headers[c], ws.Cell(1, c + 1).GetString());

        // 넷째 칸은 없다 — 우리가 열을 더 만들면 안 된다
        Assert.Equal("", ws.Cell(1, EvalUploadWorkbook.Headers.Length + 1).GetString());
    }

    private static string? FindSample(string name)
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

    [Fact]
    public void 올릴_것이_없어도_머리글만_있는_파일을_만든다()
    {
        EvalUploadWorkbook.Write(_path, Array.Empty<EvalUploadRow>());

        using var wb = new ClosedXML.Excel.XLWorkbook(_path);
        var ws = wb.Worksheet("empty0");
        Assert.Equal("영역명", ws.Cell(1, 1).GetString());
        Assert.Equal(1, ws.LastRowUsed()!.RowNumber());
    }
}
