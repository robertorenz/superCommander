using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using SuperCommander.Services;
using SuperCommander.Services.Archives;

namespace SuperCommander.Tests;

/// <summary>
/// Assertions over the archive layer, run against a real filesystem rather than
/// mocks. Deliberately not a test framework - the app has no package references
/// and neither does this, so "dotnet run" is the whole story.
/// </summary>
internal static partial class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "SuperCommander.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            PathContainment(root);
            FilterMatching();
            SourceEnumeration(root);

            RoundTrip(root, new ZipArchiveProvider(), ".zip");
            RoundTrip(root, new TarArchiveProvider(), ".tar");
            RoundTrip(root, new TarArchiveProvider(), ".tar.gz");
            RoundTrip(root, new TarArchiveProvider(), ".tgz");

            SingleFileGz(root);
            FormatDispatch();
            BrowsingTree(root);
            ViewerReads(root);
            SlipRefused(root);
            PackFrontDoor(root);

            SyncCompares(root);
            SyncFilters(root);
            SyncContent(root);
            SyncApplies(root);
            DialogLoads(root);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("   FAIL unhandled: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* temp */ }
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- assertions

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("-- " + name);
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine("   ok   " + what); }
        else { _failed++; Console.WriteLine("   FAIL " + what); }
    }

    private static void Refuses(Action action, string what)
    {
        try
        {
            action();
            Check(false, what + " (nothing was thrown)");
        }
        catch (IOException)
        {
            Check(true, what);
        }
        catch (Exception ex)
        {
            Check(false, what + " (threw " + ex.GetType().Name + ")");
        }
    }

    // ----------------------------------------------------------- test fixture

    /// <summary>A small tree with one payload big enough to be worth hashing.</summary>
    private static string BuildTree(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "docs", "deep"));

        File.WriteAllText(Path.Combine(dir, "top.txt"), "top level\n");
        File.WriteAllText(Path.Combine(dir, "docs", "readme.md"), "# readme\n");
        File.WriteAllText(Path.Combine(dir, "docs", "deep", "nested.txt"), "nested\n");

        var payload = new byte[2 * 1024 * 1024];
        new Random(20260908).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(dir, "payload.bin"), payload);

        return dir;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static FileOperationResult Create(IArchiveProvider provider, string tree, string archive) =>
        provider.CreateAsync(Directory.GetFileSystemEntries(tree), archive, tree, null, CancellationToken.None)
            .GetAwaiter().GetResult();

    // ------------------------------------------------------------------ tests

    private static void PathContainment(string root)
    {
        Section("an entry path cannot escape the target folder");

        var target = Path.GetFullPath(Path.Combine(root, "combine"));

        Check(ArchivePath.SafeCombine(target, "docs/readme.md")
                  .Equals(Path.Combine(target, "docs", "readme.md"), StringComparison.OrdinalIgnoreCase),
            "a normal entry lands under the target");

        Refuses(() => ArchivePath.SafeCombine(target, "../escaped.txt"), "a leading ../ is refused");
        Refuses(() => ArchivePath.SafeCombine(target, "docs/../../escaped.txt"), "a buried ../ is refused");
        Refuses(() => ArchivePath.SafeCombine(target, @"..\escaped.txt"), "a backslash version is refused");

        Check(ArchivePath.SafeCombine(target, "/etc/passwd").StartsWith(target, StringComparison.OrdinalIgnoreCase),
            "a rooted entry is contained rather than honoured");
        Check(ArchivePath.SafeCombine(target, @"C:\Windows\System32\evil.dll")
                  .StartsWith(target, StringComparison.OrdinalIgnoreCase),
            "a drive-qualified entry is contained too");
    }

    private static void FilterMatching()
    {
        Section("the selective-extract filter");

        Check(ArchivePath.Matches(ArchivePath.BuildFilter(null), "anything.txt"),
            "no selection means everything");
        Check(ArchivePath.Matches(ArchivePath.BuildFilter(Array.Empty<string>()), "anything.txt"),
            "an empty selection means everything");

        var only = ArchivePath.BuildFilter(new[] { "docs", "top.txt" });

        Check(ArchivePath.Matches(only, "top.txt"), "an exact file is selected");
        Check(ArchivePath.Matches(only, "docs/readme.md"), "a selected folder pulls in its children");
        Check(ArchivePath.Matches(only, "docs/deep/nested.txt"), "and its grandchildren");
        Check(!ArchivePath.Matches(only, "docsx/other.txt"), "a folder name does not leak into a sibling");
        Check(!ArchivePath.Matches(only, "payload.bin"), "an unselected file stays out");
    }

    private static void SourceEnumeration(string root)
    {
        Section("flattening the sources to pack");

        var tree = BuildTree(root, "enumerate");
        var pairs = ArchivePath.Enumerate(Directory.GetFileSystemEntries(tree), tree, CancellationToken.None);

        Check(pairs.Count == 4, $"every file is found (expected 4, got {pairs.Count})");
        Check(pairs.All(p => !p.Entry.Contains('\\')), "entry names use forward slashes");
        Check(pairs.Any(p => p.Entry == "docs/deep/nested.txt"), "a nested file keeps its relative path");
    }

    private static void RoundTrip(string root, IArchiveProvider provider, string extension)
    {
        Section($"{provider.Name}: {extension} round-trip");

        var suffix = extension.Replace('.', '_');
        var tree = BuildTree(root, "src" + suffix);
        var archive = Path.Combine(root, "bundle" + extension);

        var created = Create(provider, tree, archive);
        Check(created.Failed == 0 && File.Exists(archive), "the archive is written");
        Check(provider.CanHandle(archive), "the provider claims what it just made");

        var listed = provider.List(archive).Where(e => !e.IsDirectory).ToList();
        Check(listed.Count == 4, $"four entries are listed (got {listed.Count})");
        Check(listed.Any(e => e.FullName == "docs/deep/nested.txt"), "the nested path survives the trip");

        var target = Path.Combine(root, "out" + suffix);
        var extracted = provider.ExtractAsync(archive, null, target, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(extracted.Failed == 0 && extracted.Succeeded == 4,
            $"four entries are extracted (got {extracted.Succeeded}, {extracted.Failed} failed)");
        Check(Hash(Path.Combine(tree, "payload.bin")) == Hash(Path.Combine(target, "payload.bin")),
            "the 2 MB payload comes back byte-identical");
        Check(File.Exists(Path.Combine(target, "docs", "deep", "nested.txt")), "the tree is rebuilt");

        var partial = Path.Combine(root, "part" + suffix);
        provider.ExtractAsync(archive, new[] { "docs" }, partial, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(File.Exists(Path.Combine(partial, "docs", "readme.md")), "selecting a folder takes its contents");
        Check(!File.Exists(Path.Combine(partial, "payload.bin")), "and takes nothing else");
    }

    private static void SingleFileGz(string root)
    {
        Section("tar: a bare .gz holds one stream with no name of its own");

        var provider = new TarArchiveProvider();
        var source = Path.Combine(root, "solo-source.txt");
        File.WriteAllText(source, "one stream, no name of its own\n");

        var archive = Path.Combine(root, "solo.gz");
        using (var input = File.OpenRead(source))
        using (var output = File.Create(archive))
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            input.CopyTo(gz);

        Check(provider.CanHandle(archive), "the provider claims .gz");
        Check(!provider.CanCreate(archive), "but refuses to create one");

        var entries = provider.List(archive);
        Check(entries.Count == 1, $"exactly one entry (got {entries.Count})");
        Check(entries.Count == 1 && entries[0].FullName == "solo", "named after the archive, by convention");

        var target = Path.Combine(root, "solo-out");
        var result = provider.ExtractAsync(archive, null, target, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(result.Failed == 0, "it extracts");
        Check(File.Exists(Path.Combine(target, "solo"))
              && File.ReadAllText(Path.Combine(target, "solo")) == File.ReadAllText(source),
            "with the original bytes");
    }

    private static void FormatDispatch()
    {
        Section("which provider answers for which name");

        Check(ArchiveService.Find("a.zip") is ZipArchiveProvider, ".zip goes to the zip provider");
        Check(ArchiveService.Find("a.tar.gz") is TarArchiveProvider, ".tar.gz goes to the tar provider");
        Check(ArchiveService.Find("a.tgz") is TarArchiveProvider, ".tgz goes to the tar provider");
        Check(ArchiveService.Find("a.7z") is SevenZipProvider, ".7z goes to the 7-Zip provider");
        Check(ArchiveService.Find("a.rar") is SevenZipProvider, ".rar goes to the 7-Zip provider");

        // Regression guard: widening IsSupported beyond .zip once made Enter on
        // an installer browse it instead of running it.
        Check(!ArchiveService.IsSupported("setup.msi"), "an .msi is not an archive");
        Check(!ArchiveService.IsSupported("manual.chm"), "a .chm is not an archive");
        Check(!ArchiveService.IsSupported("tool.exe"), "an .exe is not an archive");

        // Regression guard: the pack dialog appends .zip only when nothing
        // recognises the name, so a typed .tar.gz has to survive.
        Check(!ArchiveService.IsSupported("backup"), "a bare name is not an archive, so .zip gets appended");
        Check(ArchiveService.IsSupported("backup.tar.gz"), "a typed .tar.gz is kept as typed");

        Check(ArchiveService.CanCreate("a.zip"), ".zip can be created");
        Check(ArchiveService.CanCreate("a.tar"), ".tar can be created");
        Check(ArchiveService.CanCreate("a.tar.gz"), ".tar.gz can be created");
        Check(!ArchiveService.CanCreate("a.gz"), "a bare .gz cannot");
        Check(!ArchiveService.CanCreate("a.rar"), "and RAR stays read-only");

        Check(ArchiveService.CreatableExtensions.Contains(".zip")
              && ArchiveService.CreatableExtensions.Contains(".tar.gz"),
            "the pack hint lists what is really available");

        if (new SevenZipProvider().IsAvailable)
        {
            Check(ArchiveService.IsReadable("a.7z"), "7-Zip is installed, so .7z is readable");
            Check(ArchiveService.UnavailableReason("a.7z") is null, "and there is nothing to explain");
        }
        else
        {
            Check(!ArchiveService.IsReadable("a.7z"), "with no 7-Zip, .7z is recognised but not readable");
            Check(ArchiveService.UnavailableReason("a.7z")?.Contains("7-Zip") == true,
                "and the reason names what to install");
            Check(ArchiveService.UnavailableReason("a.zip") is null, "while .zip never needs anything");
        }
    }

    private static void BrowsingTree(string root)
    {
        Section("a flat entry list browses as a folder tree");

        var tree = BuildTree(root, "browse");
        var archive = Path.Combine(root, "browse.zip");
        Create(new ZipArchiveProvider(), tree, archive);

        var top = ArchiveService.ListEntries(archive, string.Empty, out var error);
        Check(error is null, "the root lists without error");
        Check(top.Count > 0 && top[0].IsParent, "the first row walks back out");

        var names = top.Where(i => !i.IsParent).Select(i => i.Name).ToList();
        Check(names.Contains("top.txt") && names.Contains("payload.bin"), "top-level files are present");
        Check(names.Contains("docs"), "the folder implied by nested entries is present");
        Check(names.Count(n => n == "docs") == 1, "and is surfaced exactly once");
        Check(!names.Contains("readme.md"), "without its children leaking into the root");

        var inner = ArchiveService.ListEntries(archive, "docs", out _);
        var innerNames = inner.Where(i => !i.IsParent).Select(i => i.Name).ToList();

        Check(innerNames.Contains("readme.md") && innerNames.Contains("deep"),
            "descending shows the children of that folder");
        Check(!innerNames.Contains("top.txt"), "and nothing from above it");
    }

    private static void ViewerReads(string root)
    {
        Section("F3 can read a single entry");

        var tree = BuildTree(root, "view");
        var expected = File.ReadAllBytes(Path.Combine(tree, "docs", "readme.md"));

        var zip = Path.Combine(root, "view.zip");
        Create(new ZipArchiveProvider(), tree, zip);
        Check(ArchiveService.ReadEntry(zip, "docs/readme.md").AsSpan().SequenceEqual(expected),
            "streamed straight out of a .zip");

        var targz = Path.Combine(root, "view.tar.gz");
        Create(new TarArchiveProvider(), tree, targz);
        Check(ArchiveService.ReadEntry(targz, "docs/readme.md").AsSpan().SequenceEqual(expected),
            "and via a temporary extraction for .tar.gz");

        Check(ArchiveService.ReadEntry(zip, "docs/missing.md") is null, "a missing entry reads as null");
    }

    private static void SlipRefused(string root)
    {
        Section("a hostile archive cannot write outside the target");

        var archive = Path.Combine(root, "hostile.zip");
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open()))
                writer.Write("owned");
            using (var writer = new StreamWriter(zip.CreateEntry("safe.txt").Open()))
                writer.Write("fine");
        }

        var target = Path.Combine(root, "slip", "target");
        var result = ArchiveService.UnpackAsync(archive, target, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(!File.Exists(Path.Combine(root, "slip", "escaped.txt")), "nothing is written above the target");
        Check(result.Failed == 1, $"the escaping entry is reported as a failure (got {result.Failed})");
        Check(File.Exists(Path.Combine(target, "safe.txt")), "while the honest entry still extracts");
    }

    private static void PackFrontDoor(string root)
    {
        Section("PackAsync takes the format from the name");

        var tree = BuildTree(root, "pack");
        var sources = Directory.GetFileSystemEntries(tree);

        var targz = Path.Combine(root, "packed.tar.gz");
        var made = ArchiveService.PackAsync(sources, targz, tree, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(made.Failed == 0 && File.Exists(targz), "a .tar.gz name really produces a tar.gz");
        Check(new TarArchiveProvider().List(targz).Count(e => !e.IsDirectory) == 4, "with every file in it");

        var rar = Path.Combine(root, "packed.rar");
        var refused = ArchiveService.PackAsync(sources, rar, tree, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(refused.Failed == 1 && !File.Exists(rar), "a read-only format is refused, not silently zipped");
        Check(refused.Errors.Count == 1 && refused.Errors[0].Contains("not supported"),
            "and the refusal explains itself");
    }
}
