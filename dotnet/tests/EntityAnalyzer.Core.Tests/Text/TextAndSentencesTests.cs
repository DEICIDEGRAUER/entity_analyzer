using EntityAnalyzer.Core.Text;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void Removes_pdf_cid_artifacts()
    {
        var result = TextNormalizer.Normalize("(cid:45)Наг (cid:7)и Йеб");
        Assert.Equal("Наг и Йеб", result);
    }

    [Fact]
    public void Unifies_dashes_and_quotes()
    {
        var result = TextNormalizer.Normalize("«слово» – „цитата\" − конец");
        Assert.DoesNotContain('«', result);
        Assert.DoesNotContain('»', result);
        Assert.DoesNotContain('„', result);
        Assert.Contains('"', result);
    }

    [Fact]
    public void Collapses_space_and_tab_runs()
    {
        var result = TextNormalizer.Normalize("а\t\tб     в   г");
        Assert.Equal("а б в г", result);
    }

    [Fact]
    public void Preserves_newline_count_for_line_numbers()
    {
        var original = "строка один\nстрока два\n\nстрока четыре";
        var result = TextNormalizer.Normalize(original);
        Assert.Equal(original.Count(c => c == '\n'), result.Count(c => c == '\n'));
    }

    [Fact]
    public void Performs_nfkc_compatibility_folding()
    {
        // Fullwidth Latin 'Ａ' (U+FF21) must fold to 'A' under NFKC.
        var result = TextNormalizer.Normalize("\uFF21\uFF22");
        Assert.Equal("AB", result);
    }

    [Fact]
    public void Trims_outer_whitespace()
    {
        Assert.Equal("текст", TextNormalizer.Normalize("   текст   "));
    }
}

public class SentenceSplitterTests
{
    private static List<SentenceSpan> Spans(string t) => SentenceSplitter.SplitWithSpans(t);

    [Fact]
    public void Splits_on_sentence_terminators()
    {
        var s = SentenceSplitter.Split("Первое предложение. Второе предложение!");
        Assert.Equal(2, s.Count);
        Assert.Equal("Первое предложение.", s[0]);
        Assert.Equal("Второе предложение!", s[1]);
    }

    [Fact]
    public void Splits_on_ellipsis()
    {
        var s = SentenceSplitter.Split("Он ушёл… Тишина.");
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Closing_quote_after_terminator_stays_in_sentence()
    {
        var s = SentenceSplitter.Split("«Стой!» — сказал он. Потом ушёл.");
        Assert.Equal(2, s.Count);
        Assert.StartsWith("«Стой!»", s[0]);
        Assert.EndsWith("сказал он.", s[0]);
    }

    [Fact]
    public void Does_not_split_inside_decimal_numbers()
    {
        // Improvement over the Python engine, which split "3.14" into fragments.
        var s = SentenceSplitter.Split("Число 3.14 равно пи с точностью.");
        Assert.Single(s);
    }

    [Fact]
    public void Short_terminated_fragment_is_kept_as_own_sentence()
    {
        // Improvement over the Python engine, which silently dropped short fragments.
        var s = SentenceSplitter.Split("А. Это уже полноценное предложение.");
        Assert.Equal(2, s.Count);
        Assert.Equal("А.", s[0]);
    }

    [Fact]
    public void Trailing_fragment_without_terminator_becomes_final_sentence()
    {
        var s = SentenceSplitter.Split("Первое. И обрывок без точки");
        Assert.Equal(2, s.Count);
        Assert.Equal("И обрывок без точки", s[1]);
    }

    [Fact]
    public void Spans_are_monotonic_and_map_to_original_text()
    {
        const string text = "Раз, два.\r\nТри! Четыре…";
        var spans = Spans(text);
        Assert.True(spans.Count >= 3);

        for (var i = 0; i < spans.Count; i++)
        {
            var sp = spans[i];
            Assert.True(sp.Start < sp.End);
            // Границы спана уже обрезаны по пробелам — срез совпадает со своей обрезкой.
            var slice = text.Substring(sp.Start, sp.End - sp.Start);
            Assert.Equal(slice.Trim(), slice);
            if (i > 0) Assert.True(sp.Start >= spans[i - 1].End);
        }
        // First sentence starts at the very beginning of the text.
        Assert.Equal(0, spans[0].Start);
    }

    [Fact]
    public void Empty_and_whitespace_input_yield_no_sentences()
    {
        Assert.Empty(Spans(""));
        Assert.Empty(Spans("   \n\t "));
    }

    [Fact]
    public void Text_without_terminators_is_single_sentence()
    {
        var s = SentenceSplitter.Split("просто текст без точки");
        Assert.Single(s);
        Assert.Equal("просто текст без точки", s[0]);
    }
}
