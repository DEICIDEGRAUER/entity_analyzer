using EntityAnalyzer.Core.Codex;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-26 «Библиотека кодексов и паттернов»: встроенные тематические паки
/// (офлайн-first) + безопасный импорт в пользовательский кодекс.
/// </summary>
[Collection("LogSequential")]
public class CodexPacksTests
{
    [Fact]
    public void Builtin_contains_at_least_two_packs_with_unique_ids()
    {
        var packs = CodexPacks.Builtin();
        Assert.True(packs.Count >= 2, "ожидаются минимум два встроенных пака");
        Assert.Equal(packs.Count, packs.Select(p => p.Id).Distinct().Count());
        Assert.All(packs, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
        });
    }

    [Fact]
    public void Every_builtin_pack_loads_into_valid_document()
    {
        foreach (var pack in CodexPacks.Builtin())
        {
            var doc = CodexPacks.Load(pack.Id);
            Assert.True(doc.Cards.Count >= 5, $"{pack.Id}: ожидается ≥5 карточек");
            var typeIds = doc.Types.Select(t => t.Id).ToHashSet();
            Assert.All(doc.Cards, c => Assert.True(typeIds.Contains(c.TypeId),
                $"{pack.Id}: карточка '{c.Name}' ссылается на неизвестный тип '{c.TypeId}'"));
            // Имена карточек уникальны внутри пака.
            Assert.Equal(doc.Cards.Count, doc.Cards.Select(c => c.Name).Distinct().Count());
        }
    }

    [Fact]
    public void Unknown_pack_throws_clear_exception()
    {
        Assert.Throws<InvalidOperationException>(() => CodexPacks.Load("no-such-pack"));
    }

    [Fact]
    public void MergeFrom_adds_new_cards_and_skips_duplicates()
    {
        var target = new CodexDocument();
        target.Cards.Add(new Card { Id = "c_1", TypeId = "deity", Name = "Перун", Enabled = true });

        var pack = new CodexDocument();
        pack.Cards.Add(new Card { Id = "p_1", TypeId = "deity", Name = "Велес", Enabled = true });
        pack.Cards.Add(new Card { Id = "p_2", TypeId = "deity", Name = "перун", Enabled = true }); // дубликат (регистр)
        pack.Cards.Add(new Card { Id = "p_3", TypeId = "deity", Name = "Мокошь", Enabled = true });

        var added = target.MergeFrom(pack);
        Assert.Equal(2, added);
        Assert.Contains(target.Cards, c => c.Name == "Велес");
        Assert.Contains(target.Cards, c => c.Name == "Мокошь");
        // «перун» не задублирован: была «Перун», добавились двое новых.
        Assert.Equal(3, target.Cards.Count);
    }

    [Fact]
    public void MergeFrom_imports_missing_types()
    {
        var target = new CodexDocument();
        var pack = new CodexDocument();
        pack.Types.Add(new CardType { Id = "ritual", Name = "Ритуал" });
        pack.Cards.Add(new Card { Id = "p_1", TypeId = "ritual", Name = "Похороны Даждьбога", Enabled = true });

        target.MergeFrom(pack);
        Assert.Contains(target.Types, t => t.Id == "ritual");
    }
}
