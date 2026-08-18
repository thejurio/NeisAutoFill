using System.Collections.ObjectModel;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App.ViewModels;

/// <summary>매핑 화면에 보여 줄 나이스 항목 하나. 교사 계정은 필요한 만큼만 드러낸다(기술설계 §11).</summary>
public sealed class OptionChoice
{
    public OptionChoice(NeisTimetableOption option) => Option = option;

    public NeisTimetableOption Option { get; }

    /// <summary>"과학 · 교사A(acc…a)" — 같은 이름의 교사를 구별할 만큼만 계정을 보여 준다.</summary>
    public string Display => Option.TeacherName.Length == 0
        ? Option.Subject
        : $"{Option.Subject} · {Option.TeacherName}{MaskedAccount}";

    private string MaskedAccount => Option.TeacherAccount.Length switch
    {
        0 => "",
        <= 4 => $"({Option.TeacherAccount})",
        _ => $"({Option.TeacherAccount[..2]}…{Option.TeacherAccount[^1]})",
    };

    public override string ToString() => Display;
}

/// <summary>매핑 범위 선택 항목 — 전체 기본값 / 요일 / 요일+교시 / 특정 날짜.</summary>
public sealed class ScopeChoice
{
    public ScopeChoice(string label, Func<TimetableCell, MappingScope> make) => (Label, Make) = (label, make);

    public string Label { get; }
    public Func<TimetableCell, MappingScope> Make { get; }
    public override string ToString() => Label;
}

/// <summary>
/// 원본 표기 한 줄. 왼쪽에 원본 정보, 오른쪽에 나이스 후보를 놓는다 (기술설계 §11).
/// </summary>
public sealed class MappingRow : ObservableObject
{
    public const string SkipLabel = "입력 안 함";

    public MappingRow(MappingSuggestion suggestion, IReadOnlyList<TimetableCell> usedCells)
    {
        Suggestion = suggestion;
        UsedCells = usedCells;

        Candidates = new ObservableCollection<OptionChoice>(
            suggestion.Candidates.Select(o => new OptionChoice(o)));

        // 후보가 하나로 확실할 때만 미리 골라 둔다 — 나머지는 비워 두어 "미해결"이 드러나게 한다
        if (suggestion.CanAutoConfirm) _selected = Candidates[0];
    }

    public MappingSuggestion Suggestion { get; }

    /// <summary>이 표기가 쓰인 셀들 — 사용 횟수·요일·교시를 보여 주고 범위 규칙을 만들 때 쓴다.</summary>
    public IReadOnlyList<TimetableCell> UsedCells { get; }

    public string Token => Suggestion.Token.Raw;
    public string Standard => Suggestion.Token.Standard;
    public int UseCount => UsedCells.Count;

    /// <summary>"월 1·3교시 / 수 2교시" 처럼 어디에 쓰였는지.</summary>
    public string UsedWhere
    {
        get
        {
            var parts = UsedCells
                .GroupBy(c => c.DayOfWeek)
                .OrderBy(g => ((int)g.Key + 6) % 7)
                .Select(g => $"{Korean(g.Key)} {string.Join("·", g.Select(c => c.Period).Distinct().OrderBy(p => p))}교시");
            return string.Join(" / ", parts);
        }
    }

    public bool IsCreative => Suggestion.Token.IsCreativeUnresolved;

    /// <summary>왜 이 후보가 추천됐는지 — 추천과 확정을 구분해 보여 주기 위한 문구.</summary>
    public string Hint => Suggestion.Describe();

    public ObservableCollection<OptionChoice> Candidates { get; }

    private OptionChoice? _selected;
    public OptionChoice? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                IsUserConfirmed = true;              // 사용자가 직접 고른 순간부터 '확정'
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsResolved));
            }
        }
    }

    private bool _skip;
    /// <summary>이 표기는 넣지 않는다 — 미해결과 구분되는 명시적 결정.</summary>
    public bool Skip
    {
        get => _skip;
        set
        {
            if (SetProperty(ref _skip, value))
            {
                if (value) IsUserConfirmed = true;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsResolved));
            }
        }
    }

    private bool _isUserConfirmed;
    public bool IsUserConfirmed
    {
        get => _isUserConfirmed;
        private set { if (SetProperty(ref _isUserConfirmed, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    /// <summary>이 줄이 정해졌는지 — 실행 버튼 활성 판단에 쓴다.</summary>
    public bool IsResolved => Skip || Selected is not null;

    public string StatusText => Skip ? "입력 안 함"
        : Selected is null ? "미해결"
        : IsUserConfirmed ? "확정" : "추천됨";

    private static string Korean(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "월", DayOfWeek.Tuesday => "화", DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목", DayOfWeek.Friday => "금", DayOfWeek.Saturday => "토", _ => "일",
    };
}

/// <summary>
/// 과목·교사·창체 매핑 화면 (기술설계 §11, 로드맵 T5).
///
/// 평가 입력의 MatchPreview 를 그대로 쓰지 않는다 — 시간표는 한 원본 표기가 여러 교사에게
/// 갈릴 수 있어 <b>일대다 규칙 편집</b>이 필요하다. "미선택이면 같은 이름으로 진행" 폴백도 쓰지 않는다.
/// </summary>
public sealed class TimetableMappingViewModel : ObservableObject
{
    private readonly TimetableCatalog _catalog;

    public TimetableMappingViewModel(
        IReadOnlyList<TimetableSourceLesson> lessons,
        TimetableCatalog catalog,
        IReadOnlyList<TimetableMappingRule>? existingRules = null,
        bool catalogChanged = false)
    {
        _catalog = catalog;
        CatalogChangedWarning = catalogChanged
            ? "나이스의 과목·교사 목록이 지난번과 달라졌습니다. 저장된 매핑을 다시 확인해 주세요."
            : "";

        var tokens = lessons.Select(l => TimetableTokenNormalizer.Normalize(l.SourceToken)).ToList();
        var suggestions = TimetableMappingSuggester.SuggestAll(tokens, catalog);

        foreach (var s in suggestions)
        {
            var used = lessons
                .Where(l => TimetableTextNormalizer.Normalize(l.SourceToken)
                            == TimetableTextNormalizer.Normalize(s.Token.Raw))
                .Select(l => l.Cell).ToList();

            var row = new MappingRow(s, used);
            ApplyExisting(row, existingRules);
            row.PropertyChanged += (_, _) => Refresh();
            Rows.Add(row);
        }

        AddDayRuleCommand = new RelayCommand<MappingRow>(r => { if (r is not null) AddScopedRule(r, byPeriod: false); });
        AddDayPeriodRuleCommand = new RelayCommand<MappingRow>(r => { if (r is not null) AddScopedRule(r, byPeriod: true); });
        RemoveExtraRuleCommand = new RelayCommand<TimetableMappingRule>(r => { if (r is not null) { ExtraRules.Remove(r); Refresh(); } });
        Refresh();
    }

    public ObservableCollection<MappingRow> Rows { get; } = new();

    /// <summary>기본값 외에 사용자가 따로 만든 요일·교시·날짜 규칙들.</summary>
    public ObservableCollection<TimetableMappingRule> ExtraRules { get; } = new();

    public string CatalogChangedWarning { get; }
    public bool HasCatalogWarning => CatalogChangedWarning.Length > 0;

    public ICommand AddDayRuleCommand { get; }
    public ICommand AddDayPeriodRuleCommand { get; }
    public ICommand RemoveExtraRuleCommand { get; }

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private bool _canApply;
    /// <summary>모든 줄이 정해졌을 때만 적용할 수 있다 — 미해결을 남긴 채 진행하지 않는다(기술설계 §11).</summary>
    public bool CanApply { get => _canApply; private set => SetProperty(ref _canApply, value); }

    /// <summary>최종 규칙 목록. 기본값 규칙 + 사용자가 추가한 범위 규칙.</summary>
    public IReadOnlyList<TimetableMappingRule> BuildRules()
    {
        var rules = new List<TimetableMappingRule>();

        foreach (var row in Rows)
        {
            if (row.Skip)
                rules.Add(TimetableMappingRule.Skip(row.Token, MappingScope.Default));
            else if (row.Selected is not null)
                rules.Add(new TimetableMappingRule(
                    row.Token, row.Selected.Option.StableKey, MappingScope.Default,
                    row.IsUserConfirmed, _catalog.Fingerprint));
        }

        rules.AddRange(ExtraRules);
        return rules;
    }

    /// <summary>
    /// 지금 선택으로 특정 요일(+교시) 예외 규칙을 만든다.
    /// 같은 범위에 이미 규칙이 있으면 덮어써 <b>같은 우선순위 충돌</b>이 생기지 않게 한다.
    /// </summary>
    private void AddScopedRule(MappingRow row, bool byPeriod)
    {
        if (row.Selected is null) return;

        // 이 표기가 쓰인 (요일, 교시) 조합마다 규칙을 만든다 — 화면에서 요일을 고르는 UI 는 다음 단계
        foreach (var group in row.UsedCells.GroupBy(c => byPeriod ? (c.DayOfWeek, c.Period) : (c.DayOfWeek, 0)))
        {
            var scope = byPeriod
                ? MappingScope.ForDayPeriod(group.Key.Item1, group.Key.Item2)
                : MappingScope.ForDay(group.Key.Item1);

            var rule = new TimetableMappingRule(
                row.Token, row.Selected.Option.StableKey, scope, true, _catalog.Fingerprint);

            // 같은 표기·같은 범위의 기존 규칙 제거 후 추가 (중복 규칙 차단)
            var dup = ExtraRules.Where(r =>
                TimetableTextNormalizer.Normalize(r.SourceToken) == TimetableTextNormalizer.Normalize(row.Token)
                && r.Scope == scope).ToList();
            foreach (var d in dup) ExtraRules.Remove(d);

            ExtraRules.Add(rule);
        }
        Refresh();
    }

    /// <summary>저장된 규칙이 있으면 초기 선택으로 반영한다. 대상이 사라진 규칙은 무시한다.</summary>
    private void ApplyExisting(MappingRow row, IReadOnlyList<TimetableMappingRule>? existing)
    {
        if (existing is null) return;

        var token = TimetableTextNormalizer.Normalize(row.Token);
        var mine = existing.Where(r => TimetableTextNormalizer.Normalize(r.SourceToken) == token).ToList();
        if (mine.Count == 0) return;

        var def = mine.FirstOrDefault(r => r.Scope.Kind == MappingScopeKind.Default);
        if (def is not null)
        {
            if (def.IsSkip) row.Skip = true;
            else
            {
                var hit = row.Candidates.FirstOrDefault(c => c.Option.StableKey == def.TargetStableKey);
                if (hit is not null) row.Selected = hit;
            }
        }

        // 기본값이 아닌 범위 규칙은 그대로 이어받는다
        foreach (var r in mine.Where(r => r.Scope.Kind != MappingScopeKind.Default))
            ExtraRules.Add(r);
    }

    private void Refresh()
    {
        var unresolved = Rows.Count(r => !r.IsResolved);
        var confirmed = Rows.Count(r => r.IsUserConfirmed);
        var skipped = Rows.Count(r => r.Skip);

        Summary = $"표기 {Rows.Count}종 · 확정 {confirmed} · 입력 안 함 {skipped} · " +
                  $"미해결 {unresolved} · 예외 규칙 {ExtraRules.Count}개";
        CanApply = unresolved == 0 && Rows.Count > 0;
    }
}
