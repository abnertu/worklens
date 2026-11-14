namespace WorkLens.Cli;

/// <summary>
/// Writes output files atomically: a temporary file in the target directory, then a rename over the target, so an
/// interrupted run never leaves a half-written file.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var temp = TempPathFor(path);
        try
        {
            File.WriteAllBytes(temp, bytes.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw WriteFailed(path, ex);
        }
    }

    /// <summary>
    /// A unique temporary path next to <paramref name="path"/>; the caller writes it, then calls <see cref="Commit"/>.
    /// The path must be absolute (resolved with <see cref="CliHost.FullPath"/>), so the process directory never matters.
    /// </summary>
    public static string TempPathFor(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Output path must be absolute: {path}", nameof(path));
        }

        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            throw new CliException(
                ExitCodes.Configuration,
                "output.write_failed",
                $"Output directory does not exist: {directory}",
                CliException.FileDetails(path));
        }

        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>Renames a finished temporary file over the target; deletes it on failure.</summary>
    public static void Commit(string temp, string path)
    {
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw WriteFailed(path, ex);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is harmless and never read.
        }
    }

    private static CliException WriteFailed(string path, Exception ex) => new(
        ExitCodes.Configuration,
        "output.write_failed",
        $"Cannot write {path}: {ex.Message} (is the file open in another program?)",
        CliException.FileDetails(path));
}
