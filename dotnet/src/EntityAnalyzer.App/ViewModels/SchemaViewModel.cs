using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Codex;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Constructor of categories and relation types. Changes propagate to all cards instantly.</summary>
public sealed class SchemaViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public System.Collections.ObjectModel.ObservableCollection<CardType> Types => _main.Codex.Types;
    public System.Collections.ObjectModel.ObservableCollection<RelationType> Relations => _main.Codex.Relations;

    private CardType? _selectedType;
    public CardType? SelectedType
    {
        get => _selectedType;
        set
        {
            if (!Set(ref _selectedType, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(TypeCardCount));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => SelectedType is not null;
    public int TypeCardCount => SelectedType is null ? 0 : _main.Codex.Cards.Count(c => c.TypeId == SelectedType.Id);

    private RelationType? _selectedRelation;
    public RelationType? SelectedRelation
    {
        get => _selectedRelation;
        set { if (Set(ref _selectedRelation, value)) OnPropertyChanged(nameof(HasRelationSelection)); }
    }
    public bool HasRelationSelection => SelectedRelation is not null;

    public RelayCommand AddType { get; }
    public RelayCommand DeleteType { get; }
    public RelayCommand AddField { get; }
    public RelayCommand DeleteField { get; }
    public RelayCommand MoveFieldUp { get; }
    public RelayCommand MoveFieldDown { get; }
    public RelayCommand AddRelation { get; }
    public RelayCommand DeleteRelation { get; }

    private string _newTypeName = "";
    public string NewTypeName { get => _newTypeName; set => Set(ref _newTypeName, value); }

    private string _newFieldName = "";
    public string NewFieldName { get => _newFieldName; set => Set(ref _newFieldName, value); }

    private FieldType _newFieldType = FieldType.Text;
    public FieldType NewFieldType { get => _newFieldType; set => Set(ref _newFieldType, value); }

    public IReadOnlyList<FieldType> AllFieldTypes { get; } = [.. Enum.GetValues<FieldType>()];

    private string _newRelationLabel = "";
    public string NewRelationLabel { get => _newRelationLabel; set => Set(ref _newRelationLabel, value); }

    public SchemaViewModel(MainViewModel main)
    {
        _main = main;

        AddType = new RelayCommand(_ =>
        {
            var name = NewTypeName.Trim();
            if (name.Length == 0) return;
            var type = new CardType("t_" + Guid.NewGuid().ToString("N")[..8], name, "◈", "#A5A5A5");
            Types.Add(type);
            NewTypeName = "";
            OnPropertyChanged(nameof(Types));
            SelectedType = type;
            SaveAndRefresh($"категория «{name}» создана");
        }, _ => NewTypeName.Trim().Length > 0);

        DeleteType = new RelayCommand(_ =>
        {
            if (SelectedType is null) return;
            if (_main.Codex.Cards.Any(c => c.TypeId == SelectedType.Id))
            {
                _main.Dashboard.Log($"⚠ Категория «{SelectedType.Name}» не пуста — сначала перенесите её карточки.");
                return;
            }
            var name = SelectedType.Name;
            Types.Remove(SelectedType);
            OnPropertyChanged(nameof(Types));
            SelectedType = null;
            SaveAndRefresh($"категория «{name}» удалена");
        }, _ => HasSelection);

        AddField = new RelayCommand(_ =>
        {
            if (SelectedType is null || string.IsNullOrWhiteSpace(NewFieldName)) return;
            SelectedType.Fields.Add(new FieldSchema
            {
                Id = "f_" + Guid.NewGuid().ToString("N")[..8],
                Name = NewFieldName.Trim(),
                Type = NewFieldType,
                Order = SelectedType.Fields.Count,
            });
            NewFieldName = "";
            SaveAndRefresh($"поле «{NewFieldName}» добавлено в «{SelectedType.Name}»");
        }, _ => HasSelection && !string.IsNullOrWhiteSpace(NewFieldName));

        DeleteField = new RelayCommand(param =>
        {
            if (SelectedType is null || param is not FieldSchema f) return;
            SelectedType.Fields.Remove(f);
            foreach (var card in _main.Codex.Cards.Where(c => c.TypeId == SelectedType.Id))
                card.Fields.Remove(f.Id);
            SaveAndRefresh($"поле «{f.Name}» удалено вместе со значениями");
        });

        MoveFieldUp = new RelayCommand(param =>
        {
            if (SelectedType is null || param is not FieldSchema f) return;
            var i = SelectedType.Fields.IndexOf(f);
            if (i <= 0) return;
            SelectedType.Fields.RemoveAt(i);
            SelectedType.Fields.Insert(i - 1, f);
            Reorder(SelectedType);
            SaveAndRefresh("порядок полей изменён");
        });

        MoveFieldDown = new RelayCommand(param =>
        {
            if (SelectedType is null || param is not FieldSchema f) return;
            var i = SelectedType.Fields.IndexOf(f);
            if (i < 0 || i >= SelectedType.Fields.Count - 1) return;
            SelectedType.Fields.RemoveAt(i);
            SelectedType.Fields.Insert(i + 1, f);
            Reorder(SelectedType);
            SaveAndRefresh("порядок полей изменён");
        });

        AddRelation = new RelayCommand(_ =>
        {
            var label = NewRelationLabel.Trim();
            if (label.Length == 0) return;
            Relations.Add(new RelationType("r_" + Guid.NewGuid().ToString("N")[..8], label, label));
            NewRelationLabel = "";
            OnPropertyChanged(nameof(Relations));
            SaveAndRefresh($"тип связи «{label}» создан");
        }, _ => NewRelationLabel.Trim().Length > 0);

        DeleteRelation = new RelayCommand(_ =>
        {
            if (SelectedRelation is null) return;
            var id = SelectedRelation.Id;
            var name = SelectedRelation.ForwardLabel;
            Relations.Remove(SelectedRelation);
            for (int i = _main.Codex.Links.Count - 1; i >= 0; i--)
                if (_main.Codex.Links[i].RelationId == id)
                    _main.Codex.Links.RemoveAt(i);
            OnPropertyChanged(nameof(Relations));
            SelectedRelation = null;
            SaveAndRefresh($"тип связи «{name}» удалён вместе с его связями");
        }, _ => HasRelationSelection);
    }

    private static void Reorder(CardType type)
    {
        for (int i = 0; i < type.Fields.Count; i++) type.Fields[i].Order = i;
    }

    private void SaveAndRefresh(string logLine)
    {
        UserCodexStore.Save(_main.Codex);
        // QA/UX #45: правки категорий/полей/связей видны сразу — обновляем
        // страницу «Кодекс» (поля выбранной карточки, фильтры, цвета ветвей).
        _main.CodexVm.OnSchemaChanged();
        _main.Dashboard.Log("▦ " + logLine);
    }
}
