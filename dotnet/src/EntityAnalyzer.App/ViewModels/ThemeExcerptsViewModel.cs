using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Themes;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// R3-1 (item 52): «Сборщик отрывков» на «Обзоре» — отрывки по теме
/// (тематические словари с морфологией) и сущности из готовой сессии
/// анализа (корпус повторно не сканируется). Свои слова темы сохраняются
/// в user_themes.json; экспорт — MD/CSV/JSON с файлом и позицией.
/// </summary>
public sealed class ThemeExcerptsViewModel : ObservableObject
{
    public const string AllEntities = "Все сущности";

    /// <summary>Строка списка отрывков: контекст + слова темы для подсветки.</summary>
    public sealed class ExcerptRow
    {
        public required ThemeExcerpt Excerpt { get; init; }
        public string FileName => Excerpt.FileName;
        public string Entity => Excerpt.Entity;
        public string Context => Excerpt.Context;
        public string MarksText => string.Join("|", Excerpt.Words);
        public string FullPath => Excerpt.FullPath;
        public string PositionText => $"строка {Excerpt.Line}";
    }

    private readonly MainViewModel _main;
    private readonly AppSettings _settings;

    public ObservableCollection<ThemeDictionary> Themes { get; } = [];
    public ObservableCollection<string> Entities { get; } = [AllEntities];
    public ObservableCollection<string> UserWords { get; } = [];
    public ObservableCollection<ExcerptRow> Items { get; } = [];

    public RelayCommand CollectCommand { get; }
    public RelayCommand AddWordCommand { get; }
    public RelayCommand RemoveWordCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    private List<ThemeExcerpt> _collected = [];

    public ThemeExcerptsViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;

        CollectCommand = new RelayCommand(_ => Collect());
        AddWordCommand = new RelayCommand(_ => AddWord());
        RemoveWordCommand = new RelayCommand(p => RemoveWord(p as string), p => p is string);
        ExportCommand = new RelayCommand(p => Export(p as string));
        OpenFileCommand = new RelayCommand(p => OpenFile(p as ExcerptRow), p => p is ExcerptRow);

        RefreshThemes();
    }

    private ThemeDictionary? _selectedTheme;
    public ThemeDictionary? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value)) return;
            RefreshUserWords();
        }
    }

    private string _selectedEntity = AllEntities;
    public string SelectedEntity
    {
        get => _selectedEntity;
        set => Set(ref _selectedEntity, value);
    }

    private string _newWord = "";
    public string NewWord
    {
        get => _newWord;
        set => Set(ref _newWord, value);
    }

    private string _status = "Выберите тему и сущность, затем «Собрать отрывки».";
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private string _wordsInfo = "";
    public string WordsInfo
    {
        get => _wordsInfo;
        private set => Set(ref _wordsInfo, value);
    }

    public bool HasSession => _main.Session is not null;

    /// <summary>Перечитать словари (встроенные + user_themes.json), сохранив выбор.</summary>
    public void RefreshThemes()
    {
        var current = _selectedTheme?.Name;
        Themes.Clear();
        foreach (var theme in ThemeCatalog.Merge(ThemeStore.Default.Load()))
            Themes.Add(theme);
        SelectedTheme = Themes.FirstOrDefault(t => string.Equals(t.Name, current, StringComparison.OrdinalIgnoreCase))
                        ?? Themes.FirstOrDefault();
    }

    /// <summary>Список сущностей для фильтра — из текущей сессии.</summary>
    public void RefreshEntities()
    {
        var current = _selectedEntity;
        Entities.Clear();
        Entities.Add(AllEntities);
        if (_main.Session is { } session)
        {
            foreach (var name in session.Files
                         .SelectMany(f => f.Mentions)
                         .Select(m => m.Entity)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(n => n, StringComparer.Ordinal))
                Entities.Add(name);
        }
        _selectedEntity = Entities.Contains(current) ? current : AllEntities;
        OnPropertyChanged(nameof(SelectedEntity));
        OnPropertyChanged(nameof(HasSession));
    }

    /// <summary>Новая сессия анализа: освежить сущности и сбросить старые отрывки.</summary>
    public void OnSessionLoaded()
    {
        RefreshEntities();
        Items.Clear();
        _collected = [];
        Status = "Сессия обновлена — выберите тему и сущность.";
    }

    private void Collect()
    {
        if (_main.Session is not { } session)
        {
            Status = "Нет сессии анализа: сначала запустите анализ на «Обзоре».";
            return;
        }
        if (_selectedTheme is not { } theme) return;

        var entity = _selectedEntity == AllEntities ? null : _selectedEntity;
        _collected = ThemeExcerptBuilder.Collect(session, theme, entity);
        Items.Clear();
        foreach (var e in _collected.Take(200))
            Items.Add(new ExcerptRow { Excerpt = e });

        var suffix = EntitySuffix(entity);
        if (_collected.Count == 0)
        {
            Status = $"В теме «{theme.Name}» отрывков не найдено{suffix}.";
            return;
        }
        Status = $"Отрывков: {_collected.Count} · файлов: {ThemeExcerptBuilder.FileCount(_collected)}" +
                 $" — тема «{theme.Name}»{suffix}" +
                 (_collected.Count > Items.Count ? $" · показаны первые {Items.Count}" : "");
    }

    private void AddWord()
    {
        if (_selectedTheme is not { } theme) return;
        var word = NewWord.Trim();
        if (word.Length == 0) return;

        if (ThemeStore.Default.AddWord(theme.Name, word))
        {
            NewWord = "";
            RefreshThemes();
            Status = $"Слово «{word}» добавлено в тему «{theme.Name}» — сохранено в user_themes.json.";
        }
        else
        {
            Status = $"Слово «{word}» уже есть в теме «{theme.Name}».";
        }
    }

    private void RemoveWord(string? word)
    {
        if (_selectedTheme is not { } theme || string.IsNullOrWhiteSpace(word)) return;
        if (ThemeStore.Default.RemoveWord(theme.Name, word))
        {
            RefreshThemes();
            Status = $"Слово «{word}» убрано из темы «{theme.Name}».";
        }
    }

    private void RefreshUserWords()
    {
        UserWords.Clear();
        if (_selectedTheme is { } theme)
        {
            foreach (var w in ThemeStore.Default.Words(theme.Name))
                UserWords.Add(w);
            var mine = UserWords.Count == 0 ? "нет" : UserWords.Count.ToString();
            WordsInfo = $"Слов в теме: {theme.Words.Count} · своих: {mine}";
        }
        else
        {
            WordsInfo = "";
        }
    }

    private void Export(string? format)
    {
        if (_selectedTheme is not { } theme || _collected.Count == 0)
        {
            Status = "Сначала соберите отрывки.";
            return;
        }

        var ext = (format ?? "md").ToLowerInvariant() switch
        {
            "csv" => "csv",
            "json" => "json",
            _ => "md",
        };
        var entity = _selectedEntity == AllEntities ? null : _selectedEntity;
        var dir = string.IsNullOrWhiteSpace(_settings.OutputDir) ? "output" : _settings.OutputDir;
        var name = "excerpts_" + SafePart(theme.Name)
                 + (entity is null ? "" : "_" + SafePart(entity)) + "." + ext;
        var path = Path.GetFullPath(Path.Combine(dir, name));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            switch (ext)
            {
                case "csv": ThemeExcerptExport.WriteCsv(_collected, theme.Name, entity, path); break;
                case "json": ThemeExcerptExport.WriteJson(_collected, theme.Name, entity, path); break;
                default: ThemeExcerptExport.WriteMarkdown(_collected, theme.Name, entity, path); break;
            }
            Status = $"Экспорт записан: {path}";
            _main.Dashboard.Log($"✓ Отрывки темы «{theme.Name}»{EntitySuffix(entity)}: {path}");
        }
        catch (Exception ex)
        {
            Status = $"Не удалось записать экспорт: {ex.Message}";
        }
    }

    private void OpenFile(ExcerptRow? row)
    {
        if (row is null) return;
        if (!File.Exists(row.FullPath))
        {
            Status = "Файл не найден на диске.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
            Status = $"Открыт файл: {row.FullPath}";
        }
        catch (Exception ex)
        {
            Status = $"Не удалось открыть файл: {ex.Message}";
        }
    }

    private static string EntitySuffix(string? entity) => entity is null ? "" : $", сущность «{entity}»";

    private static string SafePart(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var result = sb.ToString().Trim(' ', '.');
        return result.Length == 0 ? "theme" : result;
    }
}
