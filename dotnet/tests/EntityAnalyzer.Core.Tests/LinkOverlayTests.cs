using EntityAnalyzer.Core.Codex;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Группа 3 (item 16): оверлей ручной разметки поверх графа — в связи
/// попадают только пары, обе карточки которых видны среди узлов.
/// </summary>
public class LinkOverlayTests
{
    private static readonly RelationType Enemy = new("enemy", "враждует с", "враждует с");

    private static readonly Dictionary<string, string> Names = new()
    {
        ["n1"] = "Наг",
        ["k1"] = "Ктулху",
    };

    [Fact]
    public void Links_with_both_nodes_are_included_with_label()
    {
        var links = new[] { new CardLink("n1", "k1", "enemy", "вражда") };
        var nodes = new HashSet<string> { "Наг", "Ктулху", "Йог-Сотот" };

        var data = LinkOverlay.Build(links, Names, [Enemy], nodes);

        var link = Assert.Single(data);
        Assert.Equal("Наг", link.From);
        Assert.Equal("Ктулху", link.To);
        Assert.Equal("враждует с", link.Label);
        Assert.Equal("вражда", link.Note);
    }

    [Fact]
    public void Links_outside_current_graph_are_skipped()
    {
        var links = new[] { new CardLink("n1", "k1", "enemy") };
        var nodes = new HashSet<string> { "Наг" };

        Assert.Empty(LinkOverlay.Build(links, Names, [Enemy], nodes));
    }

    [Fact]
    public void Dangling_card_ids_are_skipped()
    {
        var links = new[]
        {
            new CardLink("n1", "ghost", "enemy"),
            new CardLink("ghost", "k1", "enemy"),
        };
        var nodes = new HashSet<string> { "Наг", "Ктулху" };

        Assert.Empty(LinkOverlay.Build(links, Names, [Enemy], nodes));
    }

    [Fact]
    public void Unknown_relation_gets_default_label()
    {
        var links = new[]
        {
            new CardLink("n1", "k1", "no-such-type"),
            new CardLink("n1", "k1", ""),
        };
        var nodes = new HashSet<string> { "Наг", "Ктулху" };

        var data = LinkOverlay.Build(links, Names, [Enemy], nodes);

        Assert.Equal(2, data.Count);
        Assert.All(data, l => Assert.Equal("связь", l.Label));
    }

    [Fact]
    public void Empty_inputs_yield_nothing()
    {
        Assert.Empty(LinkOverlay.Build(
            [], new Dictionary<string, string>(), [], new HashSet<string>()));
    }
}
