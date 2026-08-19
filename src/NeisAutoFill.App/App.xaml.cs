using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NeisAutoFill.App.Services;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Scale;
using Velopack;

namespace NeisAutoFill.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ★ 반드시 맨 처음 — 설치·업데이트·제거 훅을 Velopack 이 여기서 처리하고 필요하면 즉시 종료한다.
        //   UI 나 파일 접근보다 먼저 와야 한다(훅 실행 중에 앱 본체가 뜨면 안 됨).
        VelopackApp.Build().Run();

        base.OnStartup(e);
        AppPaths.EnsureRoot();
        // ★ 프로필을 가장 먼저 로드해 경로 계층에 반영 — 이후 만들어지는 NarrativeStore 등이 올바른 프로필 경로를 쓴다
        var profiles = new ProfileStore();
        Automation.EngineDiag.OnSwallow = Diag.Swallow;   // 엔진의 조용한 예외를 diag.txt 로 (안정화 추적)

        DispatcherUnhandledException += (_, args) =>
        {
            try { System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppPaths.Root, "crash.txt"), args.Exception.ToString()); }
            catch { }
            MessageBox.Show(args.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        services.AddSingleton(profiles);   // 위에서 이미 로드·경로반영된 인스턴스
        services.AddSingleton(new EngineOptions());
        services.AddSingleton<INeisEngine, NeisEngine>();
        services.AddSingleton<IScaleStore>(_ => new JsonScaleStore(AppPaths.ScalesJson));
        services.AddSingleton<GeneratorSettingsStore>();
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton(_ => new NarrativeStore(AppPaths.NarrativesJson));
        services.AddSingleton<GenerationQueue>();
        services.AddSingleton<NarrativeMirror>();
        services.AddSingleton<NeisSessionController>();   // 연결 루프·상태칩·사전점검 게이트 (R9)
        services.AddSingleton<TimetableProfileStore>();      // 시간표 매핑 프로필
        services.AddSingleton<TimetableCheckpointStore>();   // 연간 입력 재개 기록
        services.AddSingleton<TimetableSession>();        // 시간표 읽기 흐름 (이동→조회→주차→카탈로그)
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        services.AddSingleton<UpdateService>();
        services.AddSingleton<UsageLogger>();
        services.AddSingleton<RemoteSelectorService>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<NarrativeMirror>();   // store 변경 구독 시작 (서술문.xlsx 자동 미러)
        var settings = provider.GetRequiredService<GeneratorSettingsStore>();
        Automation.Timings.SetSpeed(settings.Options.ClickSpeed);

        // 화면 표시 배율 — 모든 창이 열릴 때 자동 적용 (한 곳에서 전역 처리)
        UiScaler.Scale = settings.Options.UiScale;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) UiScaler.Apply(w); }));

        provider.GetRequiredService<MainWindow>().Show();

        var update = provider.GetRequiredService<UpdateService>();
        // 업데이트 직후면 패치로그(새로워진 점) 1회 표시 → 이어서 새 버전 확인 (백그라운드)
        _ = Task.Run(async () =>
        {
            await update.ShowWhatsNewIfUpdatedAsync();
            await update.CheckAndPromptAsync();
        });

        // 프로그램 시작을 GAS RequestLog 시트에 기록 (백그라운드)
        var version = UpdateService.CurrentVersion.ToString(3);
        _ = provider.GetRequiredService<UsageLogger>().LogStartupAsync(version);

        // 원격 셀렉터 적용 (백그라운드) — 나이스 개편 시 앱 재배포 없이 대응. 실패 시 기본값 유지
        _ = provider.GetRequiredService<RemoteSelectorService>().ApplyAsync();
    }
}
