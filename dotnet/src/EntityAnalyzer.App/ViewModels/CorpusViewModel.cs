using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Themes;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// Группа 3: раздел «Корпус» — аналитика поверх текстов последнего прогона.
/// Первая вкладка: поиск по фрагменту (item 31); сюда же подключаются
/// указатель по годам, интертекстуальные связи и темы (items 28/29/53).
/// </summary>
public sealed class CorpusViewModel : ObservableObject
{
    public FragmentSearchViewModel Fragment { get; }
    public YearsViewModel Years { get; }
    public IntertextViewModel Intertext { get; }
    public ThemesViewModel Themes { get; }

    public CorpusViewModel(MainViewModel main)
    {
        Fragment = new FragmentSearchViewModel(main);
        Years = new YearsViewModel(main);
        Intertext = new IntertextViewModel(main);
        Themes = new ThemesViewModel(main);
    }

    public void Refresh()
    {
        Fragment.Refresh();
        Years.Refresh();
        Intertext.Refresh();
        Themes.Refresh();
    }

    /// <summary>Прочитать нормализованные тексты всех файлов последнего прогона.</summary>
    internal static List<CorpusText> ReadCorpus(AnalysisSession session)
    {
        var list = new List<CorpusText>();
        foreach (var file in session.Files)
        {
            var text = FilePassportBuilder.TryReadNormalizedText(file.FullPath);
            if (!string.IsNullOrWhiteSpace(text))
                list.Add(new CorpusText(file.FileName, file.FullPath, text));
        }
        return list;
    }
}

/// <summary>
/// Item 31: поиск по фрагменту по всем файлам — точная подстрока, падежные
/// формы (морфология), варианты имён из каталога (синонимы) и приблизительное
/// написание (fuzzy/парафраз). Тексты читаются при поиске в фоне, результаты —
/// файл, строка, вид, фрагмент.
/// </summary>
public sealed class FragmentSearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ObservableCollection<FragmentHitRow> Results { get; } = [];

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    private bool _useMorphology = true;
    public bool UseMorphology { get => _useMorphology; set => Set(ref _useMorphology, value); }

    private bool _useFuzzy = true;
    public bool UseFuzzy { get => _useFuzzy; set => Set(ref _useFuzzy, value); }

    private bool _useSynonyms = true;
    public bool UseSynonyms { get => _useSynonyms; set => Set(ref _useSynonyms, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _status = "Введите фразу и нажмите «Искать» — поиск идёт по всем файлам последнего анализа.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool HasSession => _main.Session is not null;
    public bool HasResults => Results.Count > 0;

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    public FragmentSearchViewModel(MainViewModel main)
    {
        _main = main;
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        OpenFileCommand = new RelayCommand(p => OpenFile(p as FragmentHitRow), p => p is FragmentHitRow);
    }

    /// <summary>Обновление при входе в раздел.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(HasSession));
        if (_main.Session is null && Results.Count == 0)
            Status = "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к поиску.";
    }

    private async Task SearchAsync()
    {
        var query = Query.Trim();
        if (query.Length == 0)
        {
            Status = "Введите фразу для поиска.";
            return;
        }
        if (_main.Session is not { } session || session.Files.Count == 0)
        {
            Status = "Нет данных анализа — выполните анализ на «Обзоре».";
            return;
        }

        IsBusy = true;
        Status = "Ищу…";
        var morphology = UseMorphology;
        var fuzzy = UseFuzzy;
        var synonyms = UseSynonyms ? BuildSynonyms(_main.Catalog) : null;
        try
        {
            var sw = Stopwatch.StartNew();
            var files = await Task.Run(() => CorpusViewModel.ReadCorpus(session));
            var found = await Task.Run(() =>
                FragmentSearcher.Search(query, files, morphology, fuzzy, synonyms: synonyms));
            sw.Stop();

            Results.Clear();
            foreach (var hit in found)
                Results.Add(new FragmentHitRow(hit));
            OnPropertyChanged(nameof(HasResults));

            if (found.Count == 0)
            {
                Status = $"Ничего не найдено за {sw.ElapsedMilliseconds} мс.";
            }
            else
            {
                var fileCount = found.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                Status = $"Найдено {found.Count} совпадений в {fileCount} файлах за {sw.ElapsedMilliseconds} мс.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Ошибка поиска: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Карта «написание → другие написания его сущности» из каталога: варианты
    /// имён работают как синонимы морфологического уровня (например, «Ктулху»
    /// и альтернативные написания). Берутся только однословные варианты —
    /// фраза сопоставляется по словам.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? BuildSynonyms(EntityCatalog catalog)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var variants in catalog.VariantsByEntity.Values)
        {
            var words = variants
                .Select(v => (v ?? "").Trim().ToLowerInvariant())
                .Where(v => v.Length >= 3 && !v.Any(char.IsWhiteSpace))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (words.Count < 2) continue;
            foreach (var word in words)
            {
                if (!map.TryGetValue(word, out var list))
                    map[word] = list = [];
                foreach (var other in words)
                    if (!string.Equals(other, word, StringComparison.Ordinal) &&
                        !list.Contains(other, StringComparer.Ordinal))
                        list.Add(other);
            }
        }
        return map.Count == 0
            ? null
            : map.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private void OpenFile(FragmentHitRow? row)
    {
        if (row is null) return;
        if (!File.Exists(row.Path))
        {
            Status = "Файл не найден на диске.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
            Status = $"Открыт файл: {row.Path}";
        }
        catch (Exception ex)
        {
            Status = $"Не удалось открыть файл: {ex.Message}";
        }
    }
}

/// <summary>Строка результата поиска по фрагменту.</summary>
public sealed class FragmentHitRow(FragmentHit hit)
{
    public string File { get; } = hit.File;
    public string Path { get; } = hit.Path;
    public string LineText { get; } = $"стр. {hit.Line}";
    public string KindText { get; } =
        hit.Kind == "приблизительное" ? $"{hit.Kind} {hit.Score}%" : hit.Kind;
    public string Fragment { get; } = hit.Fragment;
}

/// <summary>
/// Item 28: «Годы» — указатель упоминаний по годам. По кнопке читает тексты
/// всех файлов и строит хронологическую шкалу (от старого к новому); слева
/// годы, справа — файлы выбранного года со счётчиком и примером.
/// </summary>
public sealed class YearsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ObservableCollection<YearEntryRow> Years { get; } = [];
    public ObservableCollection<YearFileRow> Files { get; } = [];

    private YearEntryRow? _selectedYear;
    public YearEntryRow? SelectedYear
    {
        get => _selectedYear;
        set
        {
            if (!Set(ref _selectedYear, value)) return;
            Files.Clear();
            if (value is not null)
                foreach (var f in value.Entry.Files)
                    Files.Add(new YearFileRow(f));
            OnPropertyChanged(nameof(SelectedSummary));
        }
    }

    private string _status = "Нажмите «Построить указатель» — годы ищутся в текстах всех файлов последнего прогона.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool HasYears => Years.Count > 0;
    public string SelectedSummary => _selectedYear is null
        ? ""
        : $"упоминаний: {_selectedYear.Entry.Total} · файлов: {_selectedYear.Entry.Files.Count}";

    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    public YearsViewModel(MainViewModel main)
    {
        _main = main;
        BuildCommand = new AsyncRelayCommand(BuildAsync);
        OpenFileCommand = new RelayCommand(p => OpenFile(p as YearFileRow), p => p is YearFileRow);
    }

    /// <summary>Обновление при входе в раздел (файлы не читаются).</summary>
    public void Refresh()
    {
        if (_main.Session is null && Years.Count == 0)
            Status = "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к указателю.";
    }

    private async Task BuildAsync()
    {
        if (_main.Session is not { } session || session.Files.Count == 0)
        {
            Status = "Нет данных анализа — выполните анализ на «Обзоре».";
            return;
        }

        Status = "Строю указатель…";
        try
        {
            var sw = Stopwatch.StartNew();
            var files = await Task.Run(() => CorpusViewModel.ReadCorpus(session));
            var timeline = await Task.Run(() => YearIndexer.Timeline(YearIndexer.Extract(files)));
            sw.Stop();

            Years.Clear();
            foreach (var entry in timeline)
                Years.Add(new YearEntryRow(entry));
            SelectedYear = Years.FirstOrDefault();
            OnPropertyChanged(nameof(HasYears));

            var totalHits = timeline.Sum(e => e.Total);
            var fileCount = timeline.SelectMany(e => e.Files).Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Status = timeline.Count == 0
                ? $"Годы в текстах не найдены за {sw.ElapsedMilliseconds} мс."
                : $"Указатель построен: {timeline.Count} г., {totalHits} упоминаний в {fileCount} файлах за {sw.ElapsedMilliseconds} мс.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка построения указателя: {ex.Message}";
        }
    }

    private void OpenFile(YearFileRow? row)
    {
        if (row is null) return;
        if (!File.Exists(row.Path))
        {
            Status = "Файл не найден на диске.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
            Status = $"Открыт файл: {row.Path}";
        }
        catch (Exception ex)
        {
            Status = $"Не удалось открыть файл: {ex.Message}";
        }
    }
}

/// <summary>Строка списка годов указателя.</summary>
public sealed class YearEntryRow(YearEntry entry)
{
    public YearEntry Entry { get; } = entry;
    public string YearText { get; } = entry.Year.ToString();
    public string SummaryText { get; } = $"упоминаний: {entry.Total} · файлов: {entry.Files.Count}";
}

/// <summary>Строка файла в выбранном году.</summary>
public sealed class YearFileRow(YearFileHits hit)
{
    public string File { get; } = hit.File;
    public string Path { get; } = hit.Path;
    public string CountText { get; } = $"{hit.Count}×";
    public string LineText { get; } = $"стр. {hit.Line}";
    public string Snippet { get; } = hit.Snippet;
}

/// <summary>
/// Item 29: «Интертекст» — общие фрагменты между файлами (4-граммы
/// «дословно» и 3-граммы «близко» вне дословных совпадений). По кнопке
/// читает тексты и строит пары; слева пары, справа — совпадения выбранной.
/// </summary>
public sealed class IntertextViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ObservableCollection<IntertextPairRow> Pairs { get; } = [];
    public ObservableCollection<IntertextMatchRow> Matches { get; } = [];

    private IntertextPairRow? _selectedPair;
    public IntertextPairRow? SelectedPair
    {
        get => _selectedPair;
        set
        {
            if (!Set(ref _selectedPair, value)) return;
            Matches.Clear();
            if (value is not null)
                foreach (var m in value.Pair.Matches)
                    Matches.Add(new IntertextMatchRow(m));
            OnPropertyChanged(nameof(SelectedSummary));
        }
    }

    private string _status = "Нажмите «Найти связи» — общие n-граммы слов ищутся по всем файлам последнего прогона.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool HasPairs => Pairs.Count > 0;
    public string SelectedSummary => _selectedPair is null
        ? ""
        : $"{_selectedPair.Pair.FileA} ↔ {_selectedPair.Pair.FileB} · общих фрагментов: {_selectedPair.Pair.SharedCount}";

    public AsyncRelayCommand BuildCommand { get; }

    public IntertextViewModel(MainViewModel main)
    {
        _main = main;
        BuildCommand = new AsyncRelayCommand(BuildAsync);
    }

    /// <summary>Обновление при входе в раздел (файлы не читаются).</summary>
    public void Refresh()
    {
        if (_main.Session is null && Pairs.Count == 0)
            Status = "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к поиску связей.";
    }

    private async Task BuildAsync()
    {
        if (_main.Session is not { } session || session.Files.Count == 0)
        {
            Status = "Нет данных анализа — выполните анализ на «Обзоре».";
            return;
        }

        Status = "Ищу связи…";
        try
        {
            var sw = Stopwatch.StartNew();
            var files = await Task.Run(() => CorpusViewModel.ReadCorpus(session));
            var pairs = await Task.Run(() => IntertextFinder.Find(files));
            sw.Stop();

            Pairs.Clear();
            foreach (var pair in pairs)
                Pairs.Add(new IntertextPairRow(pair));
            SelectedPair = Pairs.FirstOrDefault();
            OnPropertyChanged(nameof(HasPairs));

            Status = pairs.Count == 0
                ? $"Общие фрагменты не найдены за {sw.ElapsedMilliseconds} мс."
                : $"Пар с общими фрагментами: {pairs.Count}, за {sw.ElapsedMilliseconds} мс.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка поиска связей: {ex.Message}";
        }
    }
}

/// <summary>Строка пары файлов с общими фрагментами.</summary>
public sealed class IntertextPairRow(IntertextPair pair)
{
    public IntertextPair Pair { get; } = pair;
    public string Title { get; } = $"{pair.FileA} ↔ {pair.FileB}";
    public string CountText { get; } = $"общих фрагментов: {pair.SharedCount}";
    public string ToolTipText { get; } = pair.PathA + "\n" + pair.PathB;
}

/// <summary>Строка совпавшего фрагмента.</summary>
public sealed class IntertextMatchRow(IntertextMatch match)
{
    public string Kind { get; } = match.Kind;
    public string LinesText { get; } = $"стр. {match.LineA} / {match.LineB}";
    public string FragmentA { get; } = match.FragmentA;
    public string FragmentB { get; } = match.FragmentB;
}

/// <summary>
/// R3-1 (item 53): «Темы» — систематизация текстов по тематическим словам.
/// По кнопке каждый файл оценивается по всем словарям (встроенные + свои,
/// например «Космология» из user_themes.json) и получает главную тему;
/// слева темы, справа файлы выбранной темы с оценками и словами-маркерами.
/// </summary>
public sealed class ThemesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ObservableCollection<ThemeGroupRow> Groups { get; } = [];
    public ObservableCollection<ThemeFileRow> Files { get; } = [];

    private ThemeGroupRow? _selectedGroup;
    public ThemeGroupRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!Set(ref _selectedGroup, value)) return;
            Files.Clear();
            if (value is not null)
                foreach (var f in value.Files)
                    Files.Add(new ThemeFileRow(f));
            OnPropertyChanged(nameof(SelectedSummary));
        }
    }

    private string _status = "Нажмите «Систематизировать» — файлы разложатся по темам словарей, включая свои (например, «Космология»).";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool HasGroups => Groups.Count > 0;
    public string SelectedSummary => _selectedGroup is null
        ? ""
        : $"Файлов: {_selectedGroup.Files.Count} — главная тема «{_selectedGroup.Theme}»";

    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    public ThemesViewModel(MainViewModel main)
    {
        _main = main;
        BuildCommand = new AsyncRelayCommand(BuildAsync);
        OpenFileCommand = new RelayCommand(p => OpenFile(p as ThemeFileRow), p => p is ThemeFileRow);
    }

    /// <summary>Обновление при входе в раздел (словари читаются заново при построении).</summary>
    public void Refresh()
    {
        if (_main.Session is null && Groups.Count == 0)
            Status = "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к темам.";
    }

    private async Task BuildAsync()
    {
        if (_main.Session is not { } session || session.Files.Count == 0)
        {
            Status = "Нет данных анализа — выполните анализ на «Обзоре».";
            return;
        }

        Status = "Систематизирую тексты…";
        try
        {
            var sw = Stopwatch.StartNew();
            var themes = ThemeCatalog.Merge(ThemeStore.Default.Load());
            var all = await Task.Run(() => ThemeSystematizer.Build(session, themes));
            var groups = ThemeSystematizer.Group(all);
            sw.Stop();

            Groups.Clear();
            foreach (var (theme, files) in groups)
                Groups.Add(new ThemeGroupRow(theme, files));
            SelectedGroup = Groups.FirstOrDefault();
            OnPropertyChanged(nameof(HasGroups));

            var titled = all.Count(f => f.Dominant.Length > 0);
            Status = all.Count == 0
                ? "Файлов в сессии нет."
                : $"Главная тема определена у {titled} из {all.Count} файлов · тем: {groups.Count} — за {sw.ElapsedMilliseconds} мс.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка систематизации: {ex.Message}";
        }
    }

    private void OpenFile(ThemeFileRow? row)
    {
        if (row is null) return;
        if (!File.Exists(row.Path))
        {
            Status = "Файл не найден на диске.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
            Status = $"Открыт файл: {row.Path}";
        }
        catch (Exception ex)
        {
            Status = $"Не удалось открыть файл: {ex.Message}";
        }
    }
}

/// <summary>Строка группы тем: тема и файлы, у которых она главная.</summary>
public sealed class ThemeGroupRow(string theme, List<FileThemes> files)
{
    public string Theme { get; } = theme;
    public List<FileThemes> Files { get; } = files;
    public string CountText { get; } = $"файлов: {files.Count}";
}

/// <summary>Строка файла в теме: оценки всех тем и слова-маркеры главной.</summary>
public sealed class ThemeFileRow(FileThemes source)
{
    public string File { get; } = source.File.FileName;
    public string Path { get; } = source.File.FullPath;
    public string ScoresText { get; } = string.Join(" · ", source.Scores.Select(s => $"{s.Theme} {s.Hits}"));
    public string WordsText { get; } = source.Scores.Count == 0
        ? "слова темы не найдены"
        : "слова: " + string.Join(", ", source.Scores[0].Words);
    public string MentionsText { get; } = $"упоминаний: {source.File.Mentions.Count}";
}
