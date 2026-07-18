using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NeisAutoFill.Core;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F7 — 작업공간 백업 zip 생성.</summary>
public class BackupArchiveTests
{
    [Fact]
    public void 파일명은_날짜시각_형식()
    {
        var name = BackupArchive.SuggestFileName(new DateTime(2026, 7, 18, 15, 30, 0));
        Assert.Equal("NeisAutoFill_백업_20260718_1530.zip", name);
    }

    [Fact]
    public void 존재하는_파일만_zip에_담긴다()
    {
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            var a = Path.Combine(tmp.FullName, "a.json");
            var b = Path.Combine(tmp.FullName, "b.xlsx");
            File.WriteAllText(a, "{}");
            File.WriteAllText(b, "xlsx-data");
            var missing = Path.Combine(tmp.FullName, "none.json");
            var dest = Path.Combine(tmp.FullName, "out.zip");

            var (ok, error, count) = BackupArchive.CreateZip(dest, new[]
            {
                ("data/a.json", a),
                ("workspace/b.xlsx", b),
                ("data/none.json", missing),   // 없는 파일 → 건너뜀
            });

            Assert.True(ok, error);
            Assert.Equal(2, count);
            using var zip = ZipFile.OpenRead(dest);
            var entries = zip.Entries.Select(e => e.FullName).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "data/a.json", "workspace/b.xlsx" }, entries);
        }
        finally { tmp.Delete(recursive: true); }
    }

    [Fact]
    public void 담을_파일이_하나도_없으면_실패()
    {
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            var dest = Path.Combine(tmp.FullName, "out.zip");
            var (ok, error, count) = BackupArchive.CreateZip(dest, new[]
            {
                ("data/x.json", Path.Combine(tmp.FullName, "x.json")),   // 존재 안 함
            });
            Assert.False(ok);
            Assert.Equal(0, count);
            Assert.False(File.Exists(dest));
            Assert.Contains("백업할 자료가 없", error);
        }
        finally { tmp.Delete(recursive: true); }
    }
}
