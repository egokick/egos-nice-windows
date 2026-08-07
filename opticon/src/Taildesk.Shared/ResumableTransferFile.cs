namespace Taildesk.Shared;

public static class ResumableTransferFile
{
    public static long GetValidatedLength(string path, long totalLength)
    {
        var length = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (length < 0 || length > totalLength)
            throw new InvalidDataException("The resumable transfer partial has an invalid length.");
        return length;
    }

    public static async Task AppendToLengthAsync(
        string path,
        Stream source,
        long offset,
        long totalLength,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        var existingLength = GetValidatedLength(path, totalLength);
        if (offset != existingLength)
            throw new IOException($"The resumable transfer offset changed; the receiver has {existingLength} bytes. Resume the transfer again.");

        await using var output = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        output.Position = offset;
        var buffer = new byte[1024 * 1024];
        var written = offset;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            written += read;
            if (written > totalLength || written > maximumLength)
                throw new IOException("The transfer exceeded its declared or configured size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (written != totalLength)
            throw new IOException("The transfer ended before its declared size was received.");
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }
}
