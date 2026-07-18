using System.Collections.Generic;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>입력 전 불일치 분석 — 특히 '과목만 다름' 판별(간단 확인 경로).</summary>
public class MatchAnalyzerTests
{
    private static Student Stu(string no, string name, params string[] areas)
    {
        var g = new Dictionary<string, string>();
        foreach (var a in areas) g[a] = "잘함";
        return new Student(no, name, g);
    }

    /// <summary>화면 행지도: 학생마다 영역 행들. rowIndex 순서대로.</summary>
    private static Dictionary<int, RowMeta> Screen(params (string No, string Name, string Area)[] rows)
    {
        var map = new Dictionary<int, RowMeta>();
        for (int i = 0; i < rows.Length; i++)
            map[i] = new RowMeta(rows[i].No, rows[i].Name, rows[i].Area);
        return map;
    }

    [Fact]
    public void 과목만_다르고_학생_영역_같으면_SubjectOnlyMismatch()
    {
        var students = new[] { Stu("1", "김하늘", "듣기", "읽기") };
        var screen = Screen(("1", "김하늘", "듣기"), ("1", "김하늘", "읽기"));

        var issues = MatchAnalyzer.Analyze("수학", "국어", screen, students, new[] { "듣기", "읽기" });

        Assert.True(issues.SubjectMismatch);
        Assert.True(issues.SubjectOnlyMismatch);   // 과목만 다름 → 간단 확인 경로
        Assert.False(issues.Clean);
    }

    [Fact]
    public void 과목_같으면_Clean이고_SubjectOnly_아님()
    {
        var students = new[] { Stu("1", "김하늘", "듣기", "읽기") };
        var screen = Screen(("1", "김하늘", "듣기"), ("1", "김하늘", "읽기"));

        var issues = MatchAnalyzer.Analyze("국어", "국어", screen, students, new[] { "듣기", "읽기" });

        Assert.True(issues.Clean);
        Assert.False(issues.SubjectOnlyMismatch);
    }

    [Fact]
    public void 과목도_영역도_다르면_SubjectOnly_아님()
    {
        // 화면엔 없는 영역(말하기)이 있어 영역 불일치까지 → 복잡한 매핑 창으로 가야 함
        var students = new[] { Stu("1", "김하늘", "듣기", "읽기") };
        var screen = Screen(("1", "김하늘", "말하기"), ("1", "김하늘", "쓰기"));

        var issues = MatchAnalyzer.Analyze("수학", "국어", screen, students, new[] { "듣기", "읽기" });

        Assert.True(issues.SubjectMismatch);
        Assert.False(issues.SubjectOnlyMismatch);   // 영역도 다르므로 간단 확인 대상 아님
    }
}
