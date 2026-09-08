using System.IO;
using SuperCommander.Models;
using SuperCommander.Services;

namespace SuperCommander.Tests;

/// <summary>
/// Assertions over the synchroniser. Same rules as the archive half: a real
/// filesystem, no test framework, one line printed per assertion.
/// </summary>
internal static partial class Program
{
    private static void SyncCompares(string root)
    {
        Section("Synchronise sees what differs");

        var (left, right) = SyncPair(root, "compare");
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        Write(left, "both.txt", "same", stamp);
        Write(right, "both.txt", "same", stamp);

        Write(left, "newer.txt", "fresh", stamp.AddHours(1));
        Write(right, "newer.txt", "stale", stamp);

        Write(left, "older.txt", "stale", stamp);
        Write(right, "older.txt", "fresh", stamp.AddHours(1));

        Write(left, "only-left.txt", "mine", stamp);
        Write(right, "only-right.txt", "yours", stamp);

        Write(left, "resized.txt", "short", stamp);
        Write(right, "resized.txt", "a good deal longer", stamp);

        Write(left, "docs/deep.txt", "nested", stamp);

        // FAT rounds to two seconds, so a one second gap is still the same moment.
        Write(left, "skew.txt", "same", stamp);
        Write(right, "skew.txt", "same", stamp.AddSeconds(1));

        var entries = SyncService.Compare(new SyncOptions { Left = left, Right = right })
            .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        Check(entries.Count == 8, $"every file on either side gets a row (got {entries.Count})");
        Check(entries["both.txt"].State == SyncState.Same, "identical files are equal");
        Check(entries["skew.txt"].State == SyncState.Same, "a one second difference is inside the tolerance");
        Check(entries["newer.txt"].State == SyncState.LeftNewer, "the newer left file is seen as newer");
        Check(entries["older.txt"].State == SyncState.RightNewer, "and the newer right file the other way");
        Check(entries["only-left.txt"].State == SyncState.LeftOnly, "a file the right lacks is left-only");
        Check(entries["only-right.txt"].State == SyncState.RightOnly, "and the reverse is right-only");
        Check(entries["resized.txt"].State == SyncState.Different,
            "same stamp with a different size is a difference, not a direction");
        Check(entries[Path.Combine("docs", "deep.txt")].State == SyncState.LeftOnly,
            "subfolders are walked and keyed by their relative path");

        Check(entries["only-left.txt"].Action == SyncAction.ToRight, "left-only defaults to copying right");
        Check(entries["only-right.txt"].Action == SyncAction.ToLeft, "right-only defaults to copying left");
        Check(entries["newer.txt"].Action == SyncAction.ToRight, "the newer file wins by default");
        Check(entries["older.txt"].Action == SyncAction.ToLeft, "in either direction");
        Check(entries["both.txt"].Action == SyncAction.None, "an equal pair is left alone");
        Check(entries["resized.txt"].Action == SyncAction.None,
            "and an undecidable pair waits for the user rather than guessing");

        var left_only = entries["only-left.txt"];
        Check(left_only.RightPath == Path.Combine(right, "only-left.txt"),
            "a missing side still knows where the copy would land");
        Check(left_only.TransferSize == 4 && left_only.SourcePath == left_only.LeftPath,
            "and which bytes the action would move");
    }

    private static void SyncFilters(string root)
    {
        Section("Synchronise honours the mask, the depth and hidden files");

        var (left, right) = SyncPair(root, "filter");
        var stamp = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        Write(left, "keep.txt", "text", stamp);
        Write(left, "image.bin", "binary", stamp);
        Write(left, "docs/nested.txt", "nested", stamp);

        var hidden = Write(left, "hidden.txt", "hidden", stamp);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var masked = SyncService.Compare(new SyncOptions { Left = left, Right = right, FileMask = "*.txt" })
            .Select(e => e.RelativePath).ToList();

        Check(!masked.Contains("image.bin"), "the mask keeps other extensions out");
        Check(masked.Contains("keep.txt"), "while matching files stay");
        Check(!masked.Contains("hidden.txt"), "a hidden file is skipped unless it is asked for");

        var withHidden = SyncService.Compare(new SyncOptions
        {
            Left = left, Right = right, FileMask = "*.txt", IncludeHidden = true
        }).Select(e => e.RelativePath).ToList();

        Check(withHidden.Contains("hidden.txt"), "and appears when it is");

        var shallow = SyncService.Compare(new SyncOptions
        {
            Left = left, Right = right, Subdirectories = false
        }).Select(e => e.RelativePath).ToList();

        Check(!shallow.Any(p => p.Contains(Path.DirectorySeparatorChar)),
            "with subfolders off nothing below the root is compared");
        Check(shallow.Contains("image.bin"), "but the top level still is");
    }

    private static void SyncContent(string root)
    {
        Section("Comparing by content beats the timestamp");

        var (left, right) = SyncPair(root, "content");
        var stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        // Same bytes, stamps an hour apart: a re-copied file, not a changed one.
        Write(left, "touched.txt", "identical bytes", stamp.AddHours(1));
        Write(right, "touched.txt", "identical bytes", stamp);

        // Same length, same stamp, different bytes: only reading catches it.
        Write(left, "silent.txt", "aaaaaaaa", stamp);
        Write(right, "silent.txt", "bbbbbbbb", stamp);

        var byDate = SyncService.Compare(new SyncOptions { Left = left, Right = right })
            .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        Check(byDate["touched.txt"].State == SyncState.LeftNewer, "on dates alone a re-copied file looks newer");
        Check(byDate["silent.txt"].State == SyncState.Same, "and a silent edit of the same length looks equal");

        var byContent = SyncService.Compare(new SyncOptions
        {
            Left = left, Right = right, CompareContent = true
        }).ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        Check(byContent["touched.txt"].State == SyncState.Same,
            "reading them through proves the re-copied file is unchanged");
        Check(byContent["silent.txt"].State == SyncState.Different, "and finds the silent edit");
        Check(byContent["silent.txt"].Action == SyncAction.None,
            "which is reported rather than resolved by guessing");

        var bySize = SyncService.Compare(new SyncOptions
        {
            Left = left, Right = right, IgnoreDate = true
        }).ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        Check(bySize["touched.txt"].State == SyncState.Same, "ignoring dates, matching sizes are equal");
        Check(bySize["silent.txt"].State == SyncState.Same, "even when the bytes differ, unless content is on");
    }

    private static void SyncApplies(string root)
    {
        Section("Synchronising copies and deletes exactly what was marked");

        var (left, right) = SyncPair(root, "apply");
        var stamp = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        Write(left, "docs/new.txt", "brand new", stamp);          // folder missing on the right
        Write(left, "changed.txt", "the new text", stamp.AddHours(1));
        Write(right, "changed.txt", "the old text", stamp);
        Write(left, "keep.txt", "left version", stamp.AddHours(1));
        Write(right, "keep.txt", "right version", stamp);
        Write(right, "stale.txt", "no longer wanted", stamp);

        var entries = SyncService.Compare(new SyncOptions { Left = left, Right = right })
            .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        entries["keep.txt"].Action = SyncAction.None;             // the user vetoes this one
        entries["stale.txt"].Action = SyncAction.DeleteRight;

        var result = SyncService.Synchronize(entries.Values.ToList());

        Check(result.Succeeded == 3 && result.Failed == 0,
            $"three actions ran and none failed (got {result.Succeeded}/{result.Failed})");

        var copied = Path.Combine(right, "docs", "new.txt");
        Check(File.Exists(copied), "a copy creates the folder it needs on the way");
        Check(File.ReadAllText(copied) == "brand new", "with the right bytes");
        Check(File.GetLastWriteTimeUtc(copied) == stamp, "and the source timestamp");

        Check(File.ReadAllText(Path.Combine(right, "changed.txt")) == "the new text",
            "the newer file overwrote the older one");
        Check(File.ReadAllText(Path.Combine(right, "keep.txt")) == "right version",
            "a row set to skip is not touched");
        Check(!File.Exists(Path.Combine(right, "stale.txt")), "and a deletion really removes the file");

        var after = SyncService.Compare(new SyncOptions { Left = left, Right = right })
            .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        Check(after[Path.Combine("docs", "new.txt")].State == SyncState.Same, "a second pass sees the copy as equal");
        Check(after["changed.txt"].State == SyncState.Same, "and the overwrite as equal");
        Check(after["keep.txt"].State == SyncState.LeftNewer, "while the skipped row still differs");
        Check(!after.ContainsKey("stale.txt"), "and the deleted file is gone from both sides");

        // Nothing marked means nothing happens, rather than an empty-list crash.
        var idle = SyncService.Synchronize(after.Values.Select(e => { e.Action = SyncAction.None; return e; }).ToList());
        Check(idle.Succeeded == 0 && idle.Failed == 0 && !idle.Cancelled, "an empty plan is a no-op");
    }

    // ----------------------------------------------------------- sync fixture

    private static (string Left, string Right) SyncPair(string root, string name)
    {
        var left = Path.Combine(root, "sync", name, "left");
        var right = Path.Combine(root, "sync", name, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        return (left, right);
    }

    /// <summary>Writes one file with an exact timestamp - the comparison turns on it.</summary>
    private static string Write(string root, string relative, string text, DateTime modifiedUtc)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }
}
