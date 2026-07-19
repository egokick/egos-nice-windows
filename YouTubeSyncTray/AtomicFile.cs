using System.Text;
using System.Text.Json;

namespace YouTubeSyncTray;

internal static class AtomicFile
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(
        string path,
        string contents,
        Encoding? encoding = null,
        bool retainJsonBackup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Could not determine the directory for '{fullPath}'.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupTempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.bak.tmp");
        var backupPath = fullPath + ".bak";

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, encoding ?? Utf8WithoutBom))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (retainJsonBackup && IsValidJsonFile(fullPath))
            {
                File.Copy(fullPath, backupTempPath, overwrite: false);
                File.Move(backupTempPath, backupPath, overwrite: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
            TryDelete(backupTempPath);
        }
    }

    private static bool IsValidJsonFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind is not JsonValueKind.Undefined;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadJson<T>(string path, out T value, JsonSerializerOptions? options = null)
    {
        foreach (var candidatePath in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidatePath))
                {
                    continue;
                }

                var parsed = JsonSerializer.Deserialize<T>(File.ReadAllText(candidatePath), options);
                if (parsed is not null)
                {
                    value = parsed;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Try the retained last-known-good version.
            }
            catch (IOException)
            {
                // Try the retained last-known-good version.
            }
            catch (UnauthorizedAccessException)
            {
                // Try the retained last-known-good version.
            }
        }

        value = default!;
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A unique stale temp file is harmless and can be removed manually or on cleanup.
        }
    }
}
