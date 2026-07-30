using System.Text;
using System.Text.Json;

public sealed class FinanceDataException : Exception
{
    public FinanceDataException(string message)
        : base(message)
    {
    }

    public FinanceDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class FinanceDataFile
{
    public static T? ReadOptionalJson<T>(string path, JsonSerializerOptions options)
    {
        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ReadException(path, exception);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, options)
                ?? throw new FinanceDataException(
                    $"Finance data file '{Path.GetFileName(path)}' contains a null JSON document.");
        }
        catch (FinanceDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw ReadException(path, exception);
        }
    }

    public static IReadOnlyList<T> ReadJsonLines<T>(string path, JsonSerializerOptions options)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<T>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<T>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ReadException(path, exception);
        }

        var results = new List<T>();
        var lineNumber = 0;
        try
        {
            foreach (var line in lines)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var value = JsonSerializer.Deserialize<T>(line, options)
                        ?? throw new FinanceDataException(
                            $"Finance data file '{Path.GetFileName(path)}' contains null JSON on line {lineNumber}.");
                    results.Add(value);
                }
                catch (FinanceDataException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException)
                {
                    throw new FinanceDataException(
                        $"Finance data file '{Path.GetFileName(path)}' is invalid on line {lineNumber}. "
                        + "The file was left unchanged.",
                        exception);
                }
            }
        }
        catch (FinanceDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ReadException(path, exception);
        }

        return results;
    }

    public static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        _ = ReadOptionalJson<T>(path, options);
        var json = JsonSerializer.Serialize(value, options);
        await WriteTextAtomicAsync(path, json, cancellationToken);
    }

    public static void WriteJsonAtomic<T>(string path, T value, JsonSerializerOptions options)
    {
        _ = ReadOptionalJson<T>(path, options);
        var json = JsonSerializer.Serialize(value, options);
        WriteTextAtomic(path, json);
    }

    public static async Task AppendJsonLineAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        _ = ReadJsonLines<T>(path, options);
        var line = JsonSerializer.Serialize(value, options) + Environment.NewLine;
        try
        {
            await File.AppendAllTextAsync(path, line, Encoding.UTF8, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw WriteException(path, exception);
        }
    }

    public static async Task WriteTextAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var tempPath = BuildTemporaryPath(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, cancellationToken);
            File.Move(tempPath, path, true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporaryFile(tempPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporaryFile(tempPath);
            throw WriteException(path, exception);
        }
    }

    public static void WriteTextAtomic(string path, string content)
    {
        var tempPath = BuildTemporaryPath(path);
        try
        {
            File.WriteAllText(tempPath, content, Encoding.UTF8);
            File.Move(tempPath, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporaryFile(tempPath);
            throw WriteException(path, exception);
        }
    }

    private static string BuildTemporaryPath(string path) =>
        path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static FinanceDataException ReadException(string path, Exception exception) =>
        new(
            $"Finance data file '{Path.GetFileName(path)}' could not be read safely. "
            + "The file was left unchanged.",
            exception);

    private static FinanceDataException WriteException(string path, Exception exception) =>
        new(
            $"Finance data file '{Path.GetFileName(path)}' could not be saved safely. "
            + "The existing file was left unchanged.",
            exception);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The temporary file contains only the attempted replacement data.
        }
    }
}
