using System.Globalization;
using System.Text;

namespace DummyFileApi.Generators;

/// <summary>
/// Emits <c>{"meta":{"count":N},"records":[{"id":K,"value":"..."}, ...]}</c>.
/// Full records with short filler values are written until less than one more
/// fits, then the final record's <c>value</c> string is sized to land on the
/// exact byte count — the last-field trick <see cref="CsvFileGenerator"/> uses.
/// <c>count</c> is the record total, and its own printed digits are part of the
/// file, so the record count is settled by a short fixed-point pass first.
/// </summary>
public sealed class JsonFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;

    private const string MetaPrefix = "{\"meta\":{\"count\":";
    private const string RecordsInfix = "},\"records\":[";
    private const string Suffix = "]}";
    private const string RecordSuffix = "\"}";

    // {"id": ... ,"value":" ... "}  — everything in a record except the Id digits
    // and the value text.
    private const int RecordFixedChars = 6 + 10 + 2;

    // Padding for the final record's value: plain ASCII with no '"' or '\', so
    // the string never needs JSON escaping and its byte length equals its length.
    private const string Padding = "the-quick-brown-fox-jumps-over-the-lazy-dog-";

    public string TypeKey => "json";
    public string MimeType => "application/json";
    public string FileExtension => "json";

    // Scaffold with a single-digit count and one empty-value record at Id 1.
    public long MinSizeBytes =>
        MetaPrefix.Length + 1 + RecordsInfix.Length
        + RecordPrefix(1).Length + RecordSuffix.Length + Suffix.Length;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        // seed picks the starting record Id, records counting up from there.
        // Falls back to 1 if non-positive, or if its digits wouldn't fit the
        // requested size — so any size >= MinSizeBytes always succeeds.
        var start = seed is int s && s >= 1 ? (long)s : 1L;

        // count's printed digits are part of the file, so the record total and
        // the width reserved for it must agree. Start narrow and widen until the
        // simulated count fits; any leftover width the final record absorbs.
        var countWidth = 1;
        long recordCount;
        while (true)
        {
            recordCount = CountRecords(targetSizeBytes, start, countWidth);
            var actualWidth = DigitCount(recordCount);
            if (actualWidth <= countWidth)
            {
                break;
            }

            countWidth = actualWidth;
        }

        var countSlack = countWidth - DigitCount(recordCount);

        var buffer = new byte[MaxChunkSize];
        var buffered = 0;

        async ValueTask WriteAsciiAsync(string text)
        {
            if (buffered + text.Length > buffer.Length)
            {
                await output.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken);
                buffered = 0;
            }

            buffered += Encoding.ASCII.GetBytes(text, 0, text.Length, buffer, buffered);
        }

        await WriteAsciiAsync(MetaPrefix);
        await WriteAsciiAsync(recordCount.ToString(CultureInfo.InvariantCulture));
        await WriteAsciiAsync(RecordsInfix);

        var first = true;
        foreach (var record in RecordPlans(targetSizeBytes, start, countWidth, countSlack))
        {
            var value = record.PadLength < 0
                ? "item-" + record.Id.ToString(CultureInfo.InvariantCulture)
                : MakePadding(record.PadLength);

            await WriteAsciiAsync((first ? string.Empty : ",") + RecordPrefix(record.Id) + value + RecordSuffix);
            first = false;
        }

        await WriteAsciiAsync(Suffix);

        if (buffered > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken);
        }
    }

    private static long CountRecords(long targetSizeBytes, long start, int countWidth)
    {
        long count = 0;
        foreach (var _ in RecordPlans(targetSizeBytes, start, countWidth, countSlack: 0))
        {
            count++;
        }

        return count;
    }

    // PadLength < 0 marks a full record (value = "item-{Id}"); otherwise it is
    // the exact length of the final record's filler value.
    private readonly record struct RecordPlan(long Id, int PadLength);

    private static IEnumerable<RecordPlan> RecordPlans(long targetSizeBytes, long start, int countWidth, int countSlack)
    {
        var budget = targetSizeBytes - MetaPrefix.Length - countWidth - RecordsInfix.Length - Suffix.Length;

        var id = start;
        if (budget < MinRecordLength(id))
        {
            id = 1;
        }

        var remaining = budget;
        var first = true;
        while (true)
        {
            var withSeparator = (first ? 0 : 1) + FullRecordLength(id);
            var nextFinalMin = 1 + MinRecordLength(id + 1);

            if (remaining >= withSeparator + nextFinalMin)
            {
                yield return new RecordPlan(id, PadLength: -1);
                remaining -= withSeparator;
                id++;
                first = false;
                continue;
            }

            var padLength = (int)(remaining - (first ? 0 : 1) - MinRecordLength(id) + countSlack);
            yield return new RecordPlan(id, padLength);
            break;
        }
    }

    private static string RecordPrefix(long id) =>
        "{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"value\":\"";

    private static int MinRecordLength(long id) => RecordFixedChars + DigitCount(id);

    // value = "item-" (5) + the Id digits again.
    private static int FullRecordLength(long id) => RecordFixedChars + (2 * DigitCount(id)) + 5;

    private static int DigitCount(long value)
    {
        var digits = 1;
        for (var v = value; v >= 10; v /= 10)
        {
            digits++;
        }

        return digits;
    }

    private static string MakePadding(int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var pad = new char[length];
        for (var i = 0; i < length; i++)
        {
            pad[i] = Padding[i % Padding.Length];
        }

        return new string(pad);
    }
}
