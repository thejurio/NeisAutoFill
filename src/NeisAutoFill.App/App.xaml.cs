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

        var services = new ServiceCollection();
        services.AddSingleton(new EngineOptions());
        services.AddSingleton<INeisEngine, NeisEngine>();
        services.AddSingleton<IScaleStore>(_ => new JsonScaleStore(AppPaths.ScalesJson));
        services.AddSingleton<GeneratorSettingsStore>();
        services.AddSingleton(_ => new NarrativeStore(AppPaths.NarrativesJson));
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        services.AddSingleton<UpdateService>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<MainWindow>().Show();

        // 자동업데이트 확인 (백그라운드 — 설정에 UpdateRepo 가 있을 때만 동작)
        _ = provider.GetRequiredService<UpdateService>().CheckAndPromptAsync();
    }
}
