using System.Threading.Channels;

namespace EntityAnalyzer.Core.IO;

/// <summary>Чанк предложений с номером начальной строки файла (1-индексированный).</summary>
public sealed record StreamedChunk(IReadOnlyList<string> Sentences, int StartLine);

/// <summary>
/// R1-5 «Потоковый ридер больших файлов»: файл читается построчно через
/// StreamReader (в памяти только текущая строка), предложения режутся на
/// границах «. ! ?», чанки отдаются потребителю через Channel&lt;T&gt;.
/// Порядок сохраняется; отмена токеном останавливает конвейер; файл целиком
/// никогда не загружается в память — вместо отсечки по размеру.
/// </summary>
public static class StreamedSentenceReader
{
    /// <summary>Читает файл чанками предложений (IAsyncEnumerable поверх Channel).</summary>
    public static async IAsyncEnumerable<StreamedChunk> ReadChunksAsync(
        string path, int sentencesPerChunk,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<StreamedChunk>(capacity: 4);

        // Производитель: чтение файла и нарезка на чанки.
        var producer = Task.Run(async () =>
        {
            try
            {
                var current = new List<string>(sentencesPerChunk);
                var sentence = new System.Text.StringBuilder();
                var lineNumber = 1; // 1-индексированный номер текущей строки
                var chunkStartLine = 1;

                using var reader = new StreamReader(path);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    foreach (var ch in line)
                    {
                        sentence.Append(ch);
                        if (ch is '.' or '!' or '?')
                        {
                            var s = sentence.ToString().Trim();
                            if (s.Length > 0) current.Add(s);
                            sentence.Clear();
                            if (current.Count >= sentencesPerChunk)
                            {
                                await channel.Writer.WriteAsync(new StreamedChunk(current, chunkStartLine), cancellationToken);
                                current = new List<string>(sentencesPerChunk);
                                chunkStartLine = lineNumber + 1;
                            }
                        }
                    }
                    sentence.Append(' ');
                    lineNumber++;
                }

                var tail = sentence.ToString().Trim();
                if (tail.Length > 0) current.Add(tail);
                if (current.Count > 0) await channel.Writer.WriteAsync(new StreamedChunk(current, chunkStartLine), cancellationToken);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Потребитель: отдаём чанки по мере готовности; отмена — штатное
        // завершение перечисления, а не исключение наружу.
        while (true)
        {
            StreamedChunk chunk;
            try
            {
                if (!await channel.Reader.WaitToReadAsync(cancellationToken)) break;
                if (!channel.Reader.TryRead(out var ch)) continue;
                chunk = ch;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            yield return chunk;
        }

        await producer; // пробрасываем исключение производителя, если было
    }
}
