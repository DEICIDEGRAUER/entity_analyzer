using EntityAnalyzer.Embedders.Onnx.Internal;
using Xunit;

namespace EntityAnalyzer.Embedders.Onnx.Tests;

public class E5TokenizerTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    [InlineData(33599, 33600)]
    public void SentencePiece_ids_are_mapped_to_xlm_roberta_ids(long rawId, long expectedId)
    {
        Assert.Equal(expectedId, E5Tokenizer.MapRawId(rawId));
    }

    [TokenizerFact]
    public void SentencePiece_model_emits_xlm_roberta_bos_eos_and_expected_hello_ids()
    {
        var tokenizer = E5Tokenizer.Load(OnnxTestPaths.TokenizerPath!);

        Assert.Equal(0, tokenizer.BeginningOfSentenceId);
        Assert.Equal(2, tokenizer.EndOfSentenceId);
        Assert.Equal(new long[] { 0, 33600, 31, 2 }, tokenizer.Encode("hello"));
    }

    [TokenizerFact]
    public void Role_prefix_is_tokenized_in_the_same_sentencepiece_stream()
    {
        var tokenizer = E5Tokenizer.Load(OnnxTestPaths.TokenizerPath!);

        Assert.Equal(
            new long[] { 0, 41, 1294, 12, 33600, 31, 2 },
            tokenizer.Encode("query: hello"));
        Assert.Equal(
            new long[] { 0, 46692, 12, 33600, 31, 2 },
            tokenizer.Encode("passage: hello"));
    }
}
