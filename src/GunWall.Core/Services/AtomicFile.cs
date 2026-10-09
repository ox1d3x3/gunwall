using System.IO;

namespace GunWall.Services;

/// <summary>
/// Replaces a file by writing a temporary sibling first, validating it, and only
/// then moving it over the destination.
///
/// This exists because both database downloads wrote straight to the live path.
/// GeoIP opened the destination with <c>File.Create</c>, which TRUNCATES before a
/// single byte has arrived, so a connection dropped at 80% left a partial country
/// table where a working one had been - and it loaded without complaint. The OUI
/// registry wrote its destination in one call, which is better but still not safe
/// against a crash or a full disk mid-write.
///
/// That risk was bounded while the downloads only ran on a button press with
/// someone watching. Putting them on a daily schedule removes the person who
/// notices, so the write has to be safe before the schedule exists.
///
/// A validated temp file is also the only place a content check can go. A captive
/// portal answering 200 with an HTML login page is a successful download by every
/// measure except the one that matters.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Writes through <paramref name="write"/> to a temporary file, runs
    /// <paramref name="validate"/> on the result, and moves it over
    /// <paramref name="destPath"/> only if that passes.
    ///
    /// <paramref name="validate"/> returns null when the content is acceptable,
    /// or a human-readable reason why it is not. A reason aborts the replacement
    /// and leaves whatever was already at the destination untouched.
    /// </summary>
    public static async Task<long> ReplaceAsync(
        string destPath,
        Func<Stream, CancellationToken, Task> write,
        Func<string, long, string?> validate,
        CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(destPath) ?? ".";
        Directory.CreateDirectory(dir);

        // Beside the destination, not in %TEMP%: File.Move is only atomic within
        // a volume, and a temp folder can be on a different one.
        string tmp = destPath + ".incoming";
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* replaced below */ }

        try
        {
            long length;
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                 FileShare.None))
            {
                await write(fs, ct);
                await fs.FlushAsync(ct);
                length = fs.Length;
            }

            string? reason = validate(tmp, length);
            if (reason is not null)
                throw new InvalidDataException(reason);

            File.Move(tmp, destPath, overwrite: true);
            return length;
        }
        catch
        {
            // The destination is untouched on every failure path, which is the
            // entire point. Clear the partial file so a later attempt is not
            // confused by it and the disk is not slowly filled by failures.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* nothing further to do */ }
            throw;
        }
    }

    /// <summary>
    /// Rejects a file that is implausibly small, then hands the first line to
    /// <paramref name="firstLineOk"/>.
    ///
    /// Both databases are line-oriented text of at least several megabytes, so
    /// a few kilobytes means an error page, a truncated transfer or an empty
    /// response - none of which should replace working data.
    /// </summary>
    public static string? PlausibleTextTable(string path, long length, long minBytes,
                                             Func<string, bool> firstLineOk)
    {
        if (length < minBytes)
            return $"only {length:N0} bytes were received, and at least {minBytes:N0} "
                 + "were expected - this is an error page or a truncated transfer, "
                 + "not a database";

        try
        {
            using var reader = new StreamReader(path);
            string? first = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(first))
                return "the file begins with no readable line";
            if (!firstLineOk(first))
                return "the first line is not in the expected format, so this is "
                     + "not the database that was requested";
        }
        catch (Exception ex)
        {
            return "the downloaded file could not be read back: " + ex.Message;
        }

        return null;
    }
}
