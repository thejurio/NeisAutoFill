using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NeisAutoFill.App.Services;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureRoot();
        _ = Task.Run(CleanupOldLayoutFiles);   // 구버전(다중 DLL) → 단일 exe 업데이트 잔재 청소

        DispatcherUnhandledException += (_, args) =>
        {
            try { System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppPaths.Root, "crash.txt"), args.Exception.ToString()); }
            catch { }
            MessageBox.Show(args.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        services.AddSingleton(new EngineOptions());
        services.AddSingleton<INeisEngine, NeisEngine>();
        services.AddSingleton<IScaleStore>(_ => new JsonScaleStore(AppPaths.ScalesJson));
        services.AddSingleton<GeneratorSettingsStore>();
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton(_ => new NarrativeStore(AppPaths.NarrativesJson));
        services.AddSingleton<GenerationQueue>();
        services.AddSingleton<NarrativeMirror>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        services.AddSingleton<UpdateService>();
        services.AddSingleton<UsageLogger>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<NarrativeMirror>();   // store 변경 구독 시작 (서술문.xlsx 자동 미러)
        var settings = provider.GetRequiredService<GeneratorSettingsStore>();
        Automation.Timings.SetSpeed(settings.Options.ClickSpeed);

        // 화면 표시 배율 — 모든 창이 열릴 때 자동 적용 (한 곳에서 전역 처리)
        UiScaler.Scale = settings.Options.UiScale;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) UiScaler.Apply(w); }));

        provider.GetRequiredService<MainWindow>().Show();

        // 자동업데이트 확인 (백그라운드 — 설정에 UpdateRepo 가 있을 때만 동작)
        _ = provider.GetRequiredService<UpdateService>().CheckAndPromptAsync();

        // 프로그램 시작을 GAS RequestLog 시트에 기록 (백그라운드)
        var version = UpdateService.CurrentVersion.ToString(3);
        _ = provider.GetRequiredService<UsageLogger>().LogStartupAsync(version);
    }

    /// <summary>
    /// v1.3.0 이하(다중 DLL 배포)에서 단일 exe 로 업데이트된 경우, 업데이트 스크립트가
    /// 덮어쓰기만 하므로 옛 DLL 수백 개(~200MB)가 남는다 — 첫 실행 때 조용히 정리.
    /// 단일 파일 실행일 때만 동작 (개발용 다중 파일 빌드에선 아무것도 안 지운다).
    /// </summary>
    private static void CleanupOldLayoutFiles()
    {
        try
        {
            // 단일 파일 번들에서는 EntryAssembly.Location 이 빈 문자열 — 이때만 청소
            // (IL3000: 바로 그 특성을 판별에 이용하는 것이므로 경고 억제)
#pragma warning disable IL3000
            if (!string.IsNullOrEmpty(System.Reflection.Assembly.GetEntryAssembly()?.Location)) return;
#pragma warning restore IL3000

            var dir = AppContext.BaseDirectory;
            // 옛 레이아웃의 확실한 흔적이 있을 때만 (없으면 이미 깨끗한 설치)
            if (!System.IO.File.Exists(System.IO.Path.Combine(dir, "NeisAutoFill.App.dll"))) return;

            string[] patterns = { "*.dll", "*.pdb", "*.deps.json", "*.runtimeconfig.json", "*.xml" };
            foreach (var pattern in patterns)
                foreach (var file in System.IO.Directory.GetFiles(dir, pattern))
                    try { System.IO.File.Delete(file); } catch { /* 잠긴 파일은 다음 실행 때 */ }

            // 옛 언어 리소스 폴더 (cs/de/ko/... — 내용이 전부 .resources.dll 인 폴더만)
            foreach (var sub in System.IO.Directory.GetDirectories(dir))
            {
                try
                {
                    var files = System.IO.Directory.GetFiles(sub, "*", System.IO.SearchOption.AllDirectories);
                    if (files.Length > 0 && files.All(f => f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)))
                        System.IO.Directory.Delete(sub, recursive: true);
                }
                catch { /* 무시 */ }
            }
        }
        catch { /* 청소 실패는 동작에 영향 없음 */ }
    }
}
