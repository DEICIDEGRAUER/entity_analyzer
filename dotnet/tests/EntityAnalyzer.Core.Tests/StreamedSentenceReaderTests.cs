using System.Threading.Channels;
using EntityAnalyzer.Core.IO;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-5 «Потоковый ридер больших файлов»: чтение по предложениям через
/// Channel&lt;T&gt; — файл не держится в памяти целиком; потребитель обрабатывает
/// чанки по мере производства. Порядок сохраняется.
/// </summary>
[Collection("LogSequential")]
public class StreamedSentenceReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_stream_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "big.txt");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task All_sentences_arrive_in_order()
    {
        var path = WriteFile("Первое предложение. Второе предложение. Третье предложение.");

        var chunks = await StreamedSentenceReader.ReadChunksAsync(path, sentencesPerChunk: 2).ToListAsync();
        var all = chunks.SelectMany(c => c.Sentences).ToList();
        Assert.Equal(3, all.Count);
        Assert.StartsWith("Первое", all[0]);
        Assert.StartsWith("Второе", all[1]);
        Assert.StartsWith("Третье", all[2]);
    }

    [Fact]
    public async Task Chunk_start_lines_are_tracked()
    {
        // 4 строки по одному предложению; чанки по 2 предложения.
        var path = WriteFile("Ааа.\nБбб.\nВвв.\nГгг.");
        var chunks = await StreamedSentenceReader.ReadChunksAsync(path, 2).ToListAsync();
        Assert.Equal([1, 3], chunks.Select(c => c.StartLine)); // строки 1-индексированы
    }

    [Fact]
    public void Chunk_size_is_respected()
    {
        var path = WriteFile("Ааа. Ббб. Ввв. Ггг. Ддд.");
        var chunks = StreamedSentenceReader.ReadChunksAsync(path, sentencesPerChunk: 2).ToBlockingEnumerable().ToList();
        // 5 предложений → чанки 2+2+1.
        Assert.Equal([2, 2, 1], chunks.Select(c => c.Sentences.Count));
    }

    [Fact]
    public async Task Empty_file_gives_no_chunks()
    {
        var path = WriteFile("");
        var chunks = await StreamedSentenceReader.ReadChunksAsync(path, 10).ToListAsync();
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Cancellation_stops_reading()
    {
        var many = string.Join(" ", Enumerable.Range(0, 5000).Select(i => $"Предложение номер {i}."));
        var path = WriteFile(many);

        using var cts = new CancellationTokenSource();
        var count = 0;
        await foreach (var _ in StreamedSentenceReader.ReadChunksAsync(path, 10, cts.Token))
        {
            count++;
            if (count >= 3) await cts.CancelAsync();
        }
        Assert.True(count < 100); // остановились рано, не дочитав файл
    }

    [Fact]
    public async Task Large_file_streams_without_full_load()
    {
        // 20k предложений (~1 МБ): конвейер обязан обработать их по частям.
        var many = string.Join(" ", Enumerable.Range(0, 20000).Select(i => $"Ктулху спит в доме номер {i}."));
        var path = WriteFile(many);

        var total = 0;
        await foreach (var chunk in StreamedSentenceReader.ReadChunksAsync(path, 500))
            total += chunk.Sentences.Count;
        Assert.Equal(20000, total);
    }
}
