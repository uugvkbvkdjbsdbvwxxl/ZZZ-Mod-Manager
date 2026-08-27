using System.Text;
using Xunit;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using ZZZModManager.Services;

namespace ZZZModManager.Tests;

public sealed class ModVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zzz-mm-version-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public ModVersionTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
    }

    [Fact]
    public void DifferencePreviewClassifiesAddedModifiedRemovedAndUnchangedFiles()
    {
        var installed = CreateDirectory("installed", new Dictionary<string, string>
        {
            ["same.ini"] = "same",
            ["changed.buf"] = "old",
            ["removed.dds"] = "removed"
        });
        var candidate = CreateDirectory("candidate", new Dictionary<string, string>
        {
            ["same.ini"] = "same",
            ["changed.buf"] = "new",
            ["added.dds"] = "added"
        });

        var preview = new ModVersionService(_paths, new JsonFileStore()).Compare(installed, candidate);

        Assert.Equal(1, preview.AddedCount);
        Assert.Equal(1, preview.ModifiedCount);
        Assert.Equal(1, preview.RemovedCount);
        Assert.Equal(1, preview.UnchangedCount);
        Assert.Equal(ModFileDifferenceKind.Modified, preview.Files.Single(file => file.RelativePath == "changed.buf").Kind);
    }

    [Fact]
    public void UpdateCreatesCompleteBackupAndPreservesEnabledState()
    {
        var library = new ModLibrary(_paths, new JsonFileStore(), new ConflictDetector());
        var installedSource = CreateDirectory("source-v1", new Dictionary<string, string>
        {
            ["mod.ini"] = "[TextureOverride]\nhash = 11111111",
            ["old.buf"] = "version-one"
        });
        var manifest = library.Install(Candidate("Fixture", installedSource, "hash-v1"), Report("11111111"));
        library.SetEnabled(manifest.Id, true);
        var installedPath = library.GetAbsolutePath(manifest);
        var candidateSource = CreateDirectory("source-v2", new Dictionary<string, string>
        {
            ["mod.ini"] = "[TextureOverride]\nhash = 22222222",
            ["new.buf"] = "version-two"
        });

        library.Update(manifest.Id, Candidate("Fixture", candidateSource, "hash-v2"), Report("22222222"));

        Assert.True(manifest.Enabled);
        Assert.Equal(installedPath, library.GetAbsolutePath(manifest));
        Assert.Equal(2, manifest.VersionRevision);
        Assert.Equal("hash-v2", manifest.SourceSha256);
        Assert.True(File.Exists(Path.Combine(installedPath, "new.buf")));
        Assert.False(File.Exists(Path.Combine(installedPath, "old.buf")));
        var backup = Assert.Single(library.GetVersionBackups(manifest.Id));
        Assert.Equal(3, backup.FileCount);
        var resolved = new ModVersionService(_paths, new JsonFileStore()).ResolveBackup(manifest.Id, backup.BackupId);
        Assert.Equal("version-one", File.ReadAllText(Path.Combine(resolved.ContentDirectory, "old.buf")));
    }

    [Fact]
    public void RollbackRestoresSelectedContentAndKeepsCurrentEnableState()
    {
        var library = new ModLibrary(_paths, new JsonFileStore(), new ConflictDetector());
        var v1 = CreateDirectory("rollback-v1", new Dictionary<string, string>
        {
            ["mod.ini"] = "[TextureOverride]\nhash = aaaaaaaa",
            ["body.buf"] = "one"
        });
        var manifest = library.Install(Candidate("Rollback", v1, "source-v1"), Report("aaaaaaaa"));
        var v2 = CreateDirectory("rollback-v2", new Dictionary<string, string>
        {
            ["mod.ini"] = "[TextureOverride]\nhash = bbbbbbbb",
            ["body.buf"] = "two"
        });
        library.Update(manifest.Id, Candidate("Rollback", v2, "source-v2"), Report("bbbbbbbb"));
        library.SetEnabled(manifest.Id, true);
        var selected = Assert.Single(library.GetVersionBackups(manifest.Id));

        library.Rollback(manifest.Id, selected.BackupId);

        Assert.True(manifest.Enabled);
        Assert.Equal(1, manifest.VersionRevision);
        Assert.Equal("source-v1", manifest.SourceSha256);
        Assert.Equal("one", File.ReadAllText(Path.Combine(library.GetAbsolutePath(manifest), "body.buf")));
        Assert.Equal(2, library.GetVersionBackups(manifest.Id).Count);
    }

    [Fact]
    public void UpdateRestoresDirectoryAndManifestWhenLibrarySaveFails()
    {
        var store = new LibraryFailingStore();
        var library = new ModLibrary(_paths, store, new ConflictDetector());
        var v1 = CreateDirectory("failure-v1", new Dictionary<string, string>
        {
            ["mod.ini"] = "[TextureOverride]\nhash = 11111111",
            ["body.buf"] = "original"
        });
        var manifest = library.Install(Candidate("Failure", v1, "source-original"), Report("11111111"));
        var installedPath = library.GetAbsolutePath(manifest);
        var v2 = CreateDirectory("failure-v2", new Dictionary<string, string>
        {
            ["mod.ini"] = "[TextureOverride]\nhash = 22222222",
            ["body.buf"] = "replacement"
        });
        store.FailNextLibrarySave = true;

        Assert.Throws<IOException>(() =>
            library.Update(manifest.Id, Candidate("Failure", v2, "source-replacement"), Report("22222222")));

        Assert.Equal("source-original", manifest.SourceSha256);
        Assert.Equal(1, manifest.VersionRevision);
        Assert.Equal("original", File.ReadAllText(Path.Combine(installedPath, "body.buf")));
        Assert.Empty(Directory.EnumerateDirectories(_paths.ModsRoot, "DISABLED_UPDATING_*"));
        Assert.Empty(Directory.EnumerateDirectories(_paths.ModsRoot, "DISABLED_REPLACED_*"));
    }

    private string CreateDirectory(string name, IReadOnlyDictionary<string, string> files)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        foreach (var pair in files)
        {
            var path = Path.Combine(directory, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pair.Value, Encoding.UTF8);
        }

        return directory;
    }

    private static ImportCandidate Candidate(string name, string directory, string hash) => new()
    {
        DisplayName = name,
        StagedPath = directory,
        SourcePath = directory,
        SourceSha256 = hash
    };

    private static ImportReport Report(string hash) => new()
    {
        Status = ImportStatus.Ready,
        Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hash }
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class LibraryFailingStore : JsonFileStore
    {
        public bool FailNextLibrarySave { get; set; }

        public override void Save<T>(string path, T value)
        {
            if (FailNextLibrarySave && string.Equals(Path.GetFileName(path), "library.json", StringComparison.OrdinalIgnoreCase))
            {
                FailNextLibrarySave = false;
                throw new IOException("simulated library save failure");
            }

            base.Save(path, value);
        }
    }
}
