using System.Collections.ObjectModel;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.App.ViewModels;

/// <summary>척도 편집 그리드의 한 행 (한 단계).</summary>
public sealed class LevelItem : ObservableObject
{
    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }

    private string _aiNuance = "";
    public string AiNuance { get => _aiNuance; set => SetProperty(ref _aiNuance, value); }

    public static LevelItem From(GradeLevel l) => new()
    {
        Label = l.Label,
        AiNuance = l.AiNuance,
    };

    public GradeLevel ToLevel() => new(Label.Trim(), AiNuance.Trim());
}

/// <summary>
/// 평가척도 설정 창. 단계 수와 단계별 이름만 정의한다 — 척도는 항상 하나("내 평가척도")로
/// 저장되므로 이름 입력이나 프리셋 선택이 없다.
/// </summary>
public sealed class ScaleEditorViewModel : ObservableObject
{
    private const string ScaleName = "내 평가척도";

    private readonly IScaleStore _store;

    public ScaleEditorViewModel(IScaleStore store)
    {
        _store = store;

        foreach (var l in store.Active.Levels) Levels.Add(LevelItem.From(l));

        AddCommand = new RelayCommand(() => Levels.Add(new LevelItem { Label = $"단계{Levels.Count + 1}" }));
        RemoveCommand = new RelayCommand(RemoveSelected);
        UpCommand = new RelayCommand(() => Move(-1));
        DownCommand = new RelayCommand(() => Move(+1));

        Levels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LevelCount));
    }

    public ObservableCollection<LevelItem> Levels { get; } = new();

    private LevelItem? _selectedLevel;
    public LevelItem? SelectedLevel { get => _selectedLevel; set => SetProperty(ref _selectedLevel, value); }

    public IReadOnlyList<int> LevelCountOptions { get; } = new[] { 2, 3, 4, 5 };

    /// <summary>단계 수. 콤보로 바꾸면 목록이 그만큼 늘거나 준다 (기존 이름 보존).</summary>
    public int LevelCount
    {
        get => Levels.Count;
        set
        {
            while (Levels.Count < value)
                Levels.Add(new LevelItem { Label = $"단계{Levels.Count + 1}" });
            while (Levels.Count > value && Levels.Count > 0)
                Levels.RemoveAt(Levels.Count - 1);
            OnPropertyChanged();
        }
    }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand DownCommand { get; }

    private void RemoveSelected()
    {
        if (SelectedLevel is not null) Levels.Remove(SelectedLevel);
        else if (Levels.Count > 0) Levels.RemoveAt(Levels.Count - 1);
    }

    private void Move(int delta)
    {
        if (SelectedLevel is null) return;
        int i = Levels.IndexOf(SelectedLevel);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Levels.Count) return;
        Levels.Move(i, j);
    }

    /// <summary>검증 후 저장 + 활성화. 실패 시 오류 메시지 반환 (null = 성공).</summary>
    public string? TrySave()
    {
        var labels = Levels.Select(l => l.Label.Trim()).ToList();
        if (labels.Count < 2)
            return "단계는 최소 2개 이상이어야 합니다.";
        if (labels.Any(string.IsNullOrEmpty))
            return "이름이 비어 있는 단계가 있습니다.";
        if (labels.Distinct().Count() != labels.Count)
            return "단계 이름이 중복됩니다.";

        var scale = new GradeScale(ScaleName, Levels.Select(l => l.ToLevel()).ToList());
        _store.Upsert(scale);
        _store.Active = scale;
        _store.Save();
        return null;
    }
}
