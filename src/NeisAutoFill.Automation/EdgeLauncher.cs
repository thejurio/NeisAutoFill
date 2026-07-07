using System.Diagnostics;

namespace NeisAutoFill.Automation;

/// <summary>디버그 포트로 Edge 를 실행한다. §6 ① / §2.1 로그인 자동화 금지 원칙.</summary>
public sealed class EdgeLauncher(EngineOptions options)
{
    public void Launch()
    {
        Directory.CreateDirectory(options.ProfileDir);
        var edge = options.EdgePaths.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Edge 실행 파일을 찾을 수 없습니다.");

        var psi = new ProcessStartInfo(edge)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"--remote-debugging-port={options.DebugPort}");
        psi.ArgumentList.Add($"--user-data-dir={options.ProfileDir}");
        psi.ArgumentList.Add(options.NeisUrl);
        Process.Start(psi);
    }
}
