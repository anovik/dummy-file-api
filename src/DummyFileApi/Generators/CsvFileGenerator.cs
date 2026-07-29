using System.Text;

namespace DummyFileApi.Generators;

public sealed class CsvFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;

    // CRLF per RFC 4180 — fixed deliberately, since the line-ending choice
    // changes every row's byte length.
    private const string NewLine = "\r\n";

    // Three columns is a cosmetic choice, not a format constraint: RFC 4180
    // allows a single column or even no header, but such output would contain
    // no commas and be indistinguishable from plain text.
    private const string Header = "Id,Name,Value" + NewLine;

    // Padding for the last row's Value field: plain ASCII with no commas,
    // quotes, or newlines, so the field never needs CSV escaping.
    private const string Padding = "the-quick-brown-fox-jumps-over-the-lazy-dog-";

    public string TypeKey => "csv";
    public string MimeType => "text/csv";
    public string FileExtension => "csv";

    // Header plus the shortest possible data row ("1,Item-1,\r\n" — an empty
    // Value field is valid CSV).
    public long MinSizeBytes => Header.Length + RowPrefix(1).Length + NewLine.Length;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

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

        await WriteAsciiAsync(Header);

        var remaining = targetSizeBytes - Header.Length;

        // seed picks the starting row Id (rows count up from there). Falls
        // back to 1 for a non-positive seed (Id must count up, not down) or
        // when that Id's row wouldn't even fit once — e.g. a huge seed with a
        // near-minimum size — so any size >= MinSizeBytes always succeeds,
        // matching the guarantee MinSizeBytes advertises.
        var row = seed is int s && s >= 1 ? (long)s : 1L;
        if (remaining < RowPrefix(row).Length + NewLine.Length)
        {
            row = 1L;
        }

        while (true)
        {
            var fullRow = $"{RowPrefix(row)}Value-{row}{NewLine}";
            var nextRowMinLength = RowPrefix(row + 1).Length + NewLine.Length;

            // Keep writing full rows only while the remainder can still fit at
            // least a minimal final row after this one.
            if (remaining >= fullRow.Length + nextRowMinLength)
            {
                await WriteAsciiAsync(fullRow);
                remaining -= fullRow.Length;
                row++;
                continue;
            }

            // Last row: size its Value field so the total lands exactly on target.
            var prefix = RowPrefix(row);
            var padLength = (int)(remaining - prefix.Length - NewLine.Length);
            var pad = new char[padLength];
            for (var i = 0; i < padLength; i++)
            {
                pad[i] = Padding[i % Padding.Length];
            }

            await WriteAsciiAsync($"{prefix}{new string(pad)}{NewLine}");
            break;
        }

        if (buffered > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken);
        }
    }

    private static string RowPrefix(long row) => $"{row},Item-{row},";
}
