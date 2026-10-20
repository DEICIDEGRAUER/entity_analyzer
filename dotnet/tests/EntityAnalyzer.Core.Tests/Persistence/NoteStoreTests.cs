using System.IO;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Дневник исследователя (item 22/30): SQLite-хранилище заметок с привязками
/// и метками времени; недоступный файл БД деградирует до пустого дневника.
/// Каждый тест — собственный временный каталог.
/// </summary>
public class NoteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-diary-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "diary.db");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Add_and_load_roundtrip()
    {
        using var store = new NoteStore(DbPath);
        var note = store.Add("Сюжет", "Йеб — страж подземелья.", NoteBindingKind.Entity, "Йеб", "Йеб");

        Assert.NotNull(note);
        Assert.True(note!.Id > 0);
        Assert.True((DateTimeOffset.Now - note.CreatedAt).Duration() < TimeSpan.FromMinutes(1));
        Assert.Equal("сущность «Йеб»", note.BindingText);

        var all = store.LoadAll();
        Assert.Single(all);
        Assert.Equal("Сюжет", all[0].Title);
        Assert.Equal("Йеб — страж подземелья.", all[0].Body);
        Assert.Equal(NoteBindingKind.Entity, all[0].Binding);
        Assert.Equal("Йеб", all[0].BindingRef);
    }

    [Fact]
    public void Update_changes_fields_and_survives_reload()
    {
        using var store = new NoteStore(DbPath);
        var note = store.Add("Черновик", "старый текст", NoteBindingKind.None, "", "");
        Assert.NotNull(note);

        var changed = note! with
        {
            Title = "Итог",
            Body = "новый текст",
            Binding = NoteBindingKind.File,
            BindingRef = @"C:\corpus\a.txt",
            BindingLabel = "a.txt",
        };
        Assert.True(store.Update(changed));

        var reloaded = store.LoadAll();
        Assert.Single(reloaded);
        Assert.Equal("Итог", reloaded[0].Title);
        Assert.Equal("новый текст", reloaded[0].Body);
        Assert.Equal(NoteBindingKind.File, reloaded[0].Binding);
        Assert.Equal("файл «a.txt»", reloaded[0].BindingText);
    }

    [Fact]
    public void Delete_removes_note()
    {
        using var store = new NoteStore(DbPath);
        var note = store.Add("Временная", "", NoteBindingKind.None, "", "")!;

        Assert.True(store.Delete(note.Id));
        Assert.Empty(store.LoadAll());
        Assert.False(store.Delete(note.Id)); // повторное удаление — false, без исключения
    }

    [Fact]
    public void Notes_survive_store_recreation()
    {
        var first = new NoteStore(DbPath);
        first.Add("Заметка 1", "тело", NoteBindingKind.Quote, "hash|7|Йеб", "цитата");
        first.Dispose();

        using var second = new NoteStore(DbPath);
        var all = second.LoadAll();
        Assert.Single(all);
        Assert.Equal("Заметка 1", all[0].Title);
        Assert.Equal(NoteBindingKind.Quote, all[0].Binding);
    }

    [Fact]
    public void All_binding_kinds_roundtrip()
    {
        using var store = new NoteStore(DbPath);
        foreach (var kind in new[]
                 {
                     NoteBindingKind.None, NoteBindingKind.File, NoteBindingKind.Entity,
                     NoteBindingKind.Pattern, NoteBindingKind.Quote,
                 })
        {
            var n = store.Add($"note-{kind}", "тело", kind, "ref-" + kind, "label-" + kind);
            Assert.NotNull(n);
        }

        var all = store.LoadAll();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, store.Count);
        foreach (var kind in all.Select(n => n.Binding).Distinct())
            Assert.True(Enum.IsDefined(kind), "неизвестный вид привязки после roundtrip");
    }

    [Fact]
    public void Unavailable_path_degrades_to_empty_diary()
    {
        // Файл-«блокатор» на месте каталога: БД открыть нельзя, но приложение
        // обязано переживать это без исключений.
        var blocker = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "not a directory");

        using var store = new NoteStore(Path.Combine(blocker, "diary.db"));
        Assert.Empty(store.LoadAll());
        Assert.Null(store.Add("t", "b", NoteBindingKind.None, "", ""));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Notes_are_ordered_by_update_desc()
    {
        using var store = new NoteStore(DbPath);
        var older = store.Add("Старая", "", NoteBindingKind.None, "", "")!;
        var newer = store.Add("Новая", "", NoteBindingKind.None, "", "")!;
        Assert.True(store.Update(older with { Body = "обновлено" }));

        var all = store.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(older.Id, all[0].Id); // обновлённая — первой
        Assert.Equal(newer.Id, all[1].Id);
    }
}
