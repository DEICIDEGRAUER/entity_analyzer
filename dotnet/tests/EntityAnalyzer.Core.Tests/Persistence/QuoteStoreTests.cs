using System.IO;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Цитатник (item 26): append-only JSONL, последняя запись по ключу —
/// действующая, снятие звёздочки не теряет историю, битые строки пропускаются.
/// Каждый тест — собственный временный каталог.
/// </summary>
public class QuoteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-quotes-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "quotes.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static QuoteRecord Sample(string key, string entity = "Йеб",
        string context = "Йеб шептал в подземелье.") =>
        new(DateTimeOffset.Now, key, true, "hash1", 42, "Йеб", entity,
            "a.txt", @"C:\corpus\a.txt", 7, context);

    [Fact]
    public void Star_survives_reload()
    {
        var s1 = new QuoteStore(StorePath);
        s1.SetStar(Sample("k1"));

        var s2 = new QuoteStore(StorePath);
        Assert.True(s2.IsStarred("k1"));
        Assert.Equal(1, s2.StarredCount);
        var all = s2.AllStarred();
        Assert.Single(all);
        Assert.Equal("Йеб", all[0].Entity);
        Assert.Equal("Йеб шептал в подземелье.", all[0].Context);
        Assert.Equal(7, all[0].Line);
    }

    [Fact]
    public void Unstar_removes_quote_and_is_idempotent()
    {
        var store = new QuoteStore(StorePath);
        store.SetStar(Sample("k1"));
        store.Unstar("k1");

        Assert.False(store.IsStarred("k1"));
        Assert.Equal(0, store.StarredCount);
        Assert.Empty(store.AllStarred());

        store.Unstar("k1"); // повторное снятие — не ошибка и не меняет состояние
        Assert.Equal(0, store.StarredCount);
        Assert.NotNull(store.Find("k1"));
        Assert.False(store.Find("k1")!.Starred);
    }

    [Fact]
    public void Last_record_wins_per_key()
    {
        var store = new QuoteStore(StorePath);
        store.SetStar(Sample("k1"));
        store.Unstar("k1");
        store.SetStar(Sample("k1", context: "Йеб отвечал из тьмы."));

        Assert.True(store.IsStarred("k1"));
        Assert.Equal(1, store.StarredCount);
        Assert.Equal("Йеб отвечал из тьмы.", store.AllStarred()[0].Context);
    }

    [Fact]
    public void Clear_all_removes_every_star()
    {
        var store = new QuoteStore(StorePath);
        store.SetStar(Sample("k1"));
        store.SetStar(Sample("k2"));

        Assert.Equal(2, store.ClearAll());
        Assert.Equal(0, store.StarredCount);
        Assert.Empty(store.AllStarred());

        // После очистки книга снова работоспособна.
        store.SetStar(Sample("k3"));
        Assert.Equal(1, store.StarredCount);
    }

    [Fact]
    public void Corrupt_lines_are_skipped()
    {
        var store = new QuoteStore(StorePath);
        store.SetStar(Sample("k1"));
        File.AppendAllText(StorePath, "{это не json}" + Environment.NewLine);

        var reopened = new QuoteStore(StorePath);
        Assert.Equal(1, reopened.StarredCount);
        Assert.True(reopened.IsStarred("k1"));
    }

    [Fact]
    public void Missing_file_means_empty_book()
    {
        var store = new QuoteStore(StorePath);
        Assert.Equal(0, store.StarredCount);
        Assert.Empty(store.AllStarred());
        Assert.Null(store.Find("нет-такого-ключа"));
    }
}
