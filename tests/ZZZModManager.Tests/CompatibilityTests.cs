using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using ZZZModManager.Services;

namespace ZZZModManager.Tests;

public sealed class CompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zzz-mm-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public CompatibilityTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
    }

    [Fact]
    public async Task ValidatorRepairsKnownVelinaPatternsWithoutInventingFiles()
    {
        var source = CreateMod("Velina Prayer Fix by LunarBun");
        var importer = new ModImporter(_paths);
        var session = await importer.StageAsync(source);
        var candidate = Assert.Single(session.Candidates);
        var report = new ModValidator(_paths).ValidateAndRepair(candidate);

        Assert.Equal(ImportStatus.NeedsDependency, report.Status);
        Assert.Contains(report.Fixes, fix => fix.RuleId == "normalize-trailing-cycle-value");
        Assert.Contains(report.Fixes, fix => fix.RuleId == "remove-unused-missing-resource");
        Assert.Contains(report.Fixes, fix => fix.RuleId == "rabbitfx-remove-stale-engine-buffer");
        Assert.DoesNotContain(report.Issues, issue => issue.Code == "MISSING_USED_FILE");
        Assert.Contains("hash = bd043a8e", File.ReadAllText(Path.Combine(candidate.StagedPath, "Velina.ini")));
        importer.Cleanup(session);
    }

    [Fact]
    public async Task ValidatorRemovesMissingBindingsFromLegacySeedHairBSection()
    {
        var source = Path.Combine(_root, "Hatsune Seedku");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Seed.ini"), """
[TextureOverrideSeedHairB]
hash = 6cb35165
match_first_index = 10632
ib = ResourceSeedHairBIB
Resource\ZZMI\Diffuse = ref ResourceSeedHairBDiffuse
Resource\ZZMI\NormalMap = ref ResourceSeedHairBNormalMap
Resource\ZZMI\LightMap = ref ResourceSeedHairBLightMap
Resource\ZZMI\MaterialMap = ref ResourceSeedHairBMaterialMap
Resource\ZZMI\WengineFX = ref ResourceSeedHairBWengineFX
run = CommandList\ZZMI\SetTextures

[ResourceSeedHairBIB]
type = Buffer
filename = SeedHairB.ib

[ResourceSeedHairBDiffuse]
filename = SeedHairBDiffuse.dds

[ResourceSeedHairBNormalMap]
filename = SeedHairBNormalMap.dds

[ResourceSeedHairBLightMap]
filename = SeedHairBLightMap.dds

[ResourceSeedHairBMaterialMap]
filename = SeedHairBMaterialMap.dds

[ResourceSeedHairBWengineFX]
filename = SeedHairBWengineFX.dds
""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "SeedHairB.ib"), [1, 2, 3]);

        var importer = new ModImporter(_paths);
        var session = await importer.StageAsync(source);
        try
        {
            var candidate = Assert.Single(session.Candidates);
            var report = new ModValidator(_paths).ValidateAndRepair(candidate);

            Assert.Equal(ImportStatus.ReadyWithFixes, report.Status);
            Assert.DoesNotContain(report.Issues, issue => issue.Code == "MISSING_USED_FILE");
            Assert.Equal(5, report.Fixes.Count(fix => fix.RuleId == "remove-inactive-missing-resource-binding"));
            var repaired = File.ReadAllText(Path.Combine(candidate.StagedPath, "Seed.ini"));
            Assert.Contains("ib = ResourceSeedHairBIB", repaired, StringComparison.Ordinal);
            Assert.DoesNotContain("ResourceSeedHairBDiffuse", repaired, StringComparison.Ordinal);
            Assert.DoesNotContain("filename = SeedHairBDiffuse.dds", repaired, StringComparison.Ordinal);
        }
        finally
        {
            importer.Cleanup(session);
        }
    }

    [Fact]
    public async Task ValidatorRemovesOrphanedModManagerGuards()
    {
        var source = Path.Combine(_root, "Legacy Managed Mod", "Yixuan");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "makeup.ini"), """
[Constants]
global $managed_slot_id = 2
global $active = 0

[KeyMakeup]
condition = ($active == 1) && $managed_slot_id == $\modmanageragl\group_5\active_slot
key = p

[Present]
if $managed_slot_id == $\modmanageragl\group_5\active_slot
    post $active = 0
endif
""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "placeholder.dds"), [1]);

        var importer = new ModImporter(_paths);
        var session = await importer.StageAsync(Path.Combine(_root, "Legacy Managed Mod"));
        var candidate = Assert.Single(session.Candidates);
        var report = new ModValidator(_paths).ValidateAndRepair(candidate);

        Assert.Contains(report.Fixes, fix => fix.RuleId == "remove-legacy-modmanager-guard");
        var repaired = File.ReadAllText(Path.Combine(candidate.StagedPath, "makeup.ini"));
        Assert.DoesNotContain("modmanageragl", repaired, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("condition = ($active == 1)", repaired, StringComparison.Ordinal);
        Assert.Contains("if 1", repaired, StringComparison.Ordinal);
        importer.Cleanup(session);
    }

    [Fact]
    public async Task ArchiveImportFindsNestedRootAndPreservesSource()
    {
        var source = CreateMod("Archive Mod");
        var archive = Path.Combine(_root, "download.zip");
        ZipFile.CreateFromDirectory(source, archive);
        var before = FileSystemSafety.ComputeDirectoryFingerprint(source);

        var session = await new ModImporter(_paths).StageAsync(archive);
        var candidate = Assert.Single(session.Candidates);
        Assert.Equal("download.zip", Path.GetFileName(session.SourcePath));
        Assert.Equal(before, FileSystemSafety.ComputeDirectoryFingerprint(source));
        Assert.Equal("Velina", Path.GetFileName(candidate.StagedPath));
    }

    [Theory]
    [InlineData("simple-mod.7z")]
    [InlineData("simple-mod.rar")]
    public async Task RealSevenZipAndRarFixturesAreImported(string fixtureName)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(fixture), $"缺少测试夹具：{fixture}");

        var session = await new ModImporter(_paths).StageAsync(fixture);
        try
        {
            var candidate = Assert.Single(session.Candidates);
            Assert.Equal("Wrapped Mod", Path.GetFileName(candidate.StagedPath));
            Assert.True(File.Exists(Path.Combine(candidate.StagedPath, "Buffers", "fixture.ib")));
        }
        finally
        {
            new ModImporter(_paths).Cleanup(session);
        }
    }

    [Fact]
    public async Task LibraryDisablesConflictingModBeforeEnablingSelectedMod()
    {
        var importer = new ModImporter(_paths);
        var first = await importer.StageAsync(CreateMod("First"));
        var second = await importer.StageAsync(CreateMod("Second"));
        var validator = new ModValidator(_paths);
        var firstCandidate = Assert.Single(first.Candidates);
        var secondCandidate = Assert.Single(second.Candidates);
        var firstReport = validator.ValidateAndRepair(firstCandidate);
        var secondReport = validator.ValidateAndRepair(secondCandidate);
        var library = new ModLibrary(_paths, new JsonFileStore(), new ConflictDetector());
        var firstManifest = library.Install(firstCandidate, firstReport);
        var secondManifest = library.Install(secondCandidate, secondReport);

        library.SetEnabled(firstManifest.Id, true);
        var conflicts = library.FindConflicts(secondManifest);
        Assert.Single(conflicts);
        library.SetEnabled(firstManifest.Id, false);
        library.SetEnabled(secondManifest.Id, true);
        Assert.True(library.GetAll().Single(mod => mod.Id == secondManifest.Id).Enabled);
    }

    [Fact]
    public void LibraryDetectsSameArchiveOptionalComponentsAsOneSplitPackage()
    {
        var library = new ModLibrary(_paths, new JsonFileStore(), new ConflictDetector());
        var firstRoot = Path.Combine(_root, "Misc");
        var secondRoot = Path.Combine(_root, "SoundWave");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(firstRoot, "misc.ini"), "[TextureOverrideMisc]\nhash = aabbccdd", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(firstRoot, "misc.dds"), [1]);
        File.WriteAllText(Path.Combine(secondRoot, "sound.ini"), "[TextureOverrideSound]\nhash = 11223344", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(secondRoot, "sound.dds"), [2]);

        library.Install(new ImportCandidate
        {
            DisplayName = "hatsune_seedku_08a53 - Misc",
            StagedPath = firstRoot,
            SourcePath = "D:\\Downloads\\hatsune_seedku_08a53.zip",
            SourceSha256 = "same-source"
        }, new ImportReport { Status = ImportStatus.Ready });
        library.Install(new ImportCandidate
        {
            DisplayName = "hatsune_seedku_08a53 - SoundWave",
            StagedPath = secondRoot,
            SourcePath = "D:\\Downloads\\hatsune_seedku_08a53.zip",
            SourceSha256 = "same-source"
        }, new ImportReport { Status = ImportStatus.Ready });

        var package = Assert.Single(library.FindSplitPackages());
        Assert.Equal("D:\\Downloads\\hatsune_seedku_08a53.zip", package.SourcePath);
        Assert.Equal(2, package.Mods.Count);
    }

    [Fact]
    public void SleepyCodecRoundTripsUtf8Json()
    {
        var magic = new byte[] { 85, 110, 209, 150, 116, 209, 131, 206, 149, 110, 103, 105, 110, 208, 181, 46, 71, 208, 176, 109, 101, 206, 159, 98, 106, 101, 209, 129, 116 };
        const string json = "{\"中文\":\"高精度\",\"value\":13162}";
        var encoded = SleepyCodec.Encode(json, magic);
        Assert.Equal(json, SleepyCodec.Decode(encoded, magic));
    }

    [Fact]
    public async Task ValidatorBlocksMissingFileThatIsActuallyReferenced()
    {
        var source = Path.Combine(_root, "Broken Mod");
        var mod = Path.Combine(source, "Broken");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "broken.ini"), """
[TextureOverrideBroken]
hash = deadbeef
ib = ResourceBrokenIB

[ResourceBrokenIB]
type = Buffer
filename = MissingUsed.ib
""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(mod, "placeholder.buf"), [1]);
        var session = await new ModImporter(_paths).StageAsync(source);
        var report = new ModValidator(_paths).ValidateAndRepair(Assert.Single(session.Candidates));
        Assert.Equal(ImportStatus.Blocked, report.Status);
        Assert.Contains(report.Issues, issue => issue.Code == "MISSING_USED_FILE");
    }

    [Fact]
    public void ValidatorDistinguishesDeclaredAndUndefinedCommandLists()
    {
        var source = Path.Combine(_root, "Command List Mod");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "commands.ini"), """
namespace = Example

[CommandListLocal]
$value = 1

[TextureOverrideBody]
hash = 1234abcd
run = CommandListLocal
run = CommandList\Example\Local
run = CommandListMissing
run = CommandList\Other\Run
""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "body.buf"), [1]);
        var candidate = new ImportCandidate { StagedPath = source, SourcePath = source };

        var report = new ModValidator(_paths).ValidateAndRepair(candidate);

        var undefined = report.Issues.Where(issue => issue.Code == "UNDEFINED_COMMAND_LIST").ToList();
        Assert.Equal(2, undefined.Count);
        Assert.Contains(undefined, issue => issue.Message.Contains("CommandListMissing", StringComparison.Ordinal));
        Assert.Contains(undefined, issue => issue.Message.Contains(@"CommandList\Other\Run", StringComparison.Ordinal));
        Assert.DoesNotContain(undefined, issue => issue.Message.Contains("CommandListLocal", StringComparison.Ordinal));
        Assert.DoesNotContain(undefined, issue => issue.Message.Contains(@"CommandList\Example\Local", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorBlocksUnbalancedConditionalBlocks()
    {
        var source = Path.Combine(_root, "Broken Conditional Mod");
        Directory.CreateDirectory(Path.Combine(source, "Nested"));
        File.WriteAllText(Path.Combine(source, "Nested", "broken.ini"), """
[TextureOverrideBody]
hash = 1234abcd
if $enabled
handling = skip
""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "body.buf"), [1]);
        var candidate = new ImportCandidate { StagedPath = source, SourcePath = source };

        var report = new ModValidator(_paths).ValidateAndRepair(candidate);

        var issue = Assert.Single(report.Issues, item => item.Code == "UNTERMINATED_IF");
        Assert.Equal(ImportStatus.Blocked, report.Status);
        Assert.Equal(Path.Combine("Nested", "broken.ini"), issue.File);
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public async Task ValidatorDoesNotMarkRabbitFxPackageAsDependingOnItself()
    {
        var source = Path.Combine(_root, "RabbitFX Package", "RabbitFX");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "RabbitFX.ini"), "namespace = RabbitFX\n[CommandListRun]\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "placeholder.buf"), [1]);

        var session = await new ModImporter(_paths).StageAsync(Path.Combine(_root, "RabbitFX Package"));
        var report = new ModValidator(_paths).ValidateAndRepair(Assert.Single(session.Candidates));

        Assert.DoesNotContain("RabbitFX", report.Dependencies);
        Assert.DoesNotContain(report.Issues, issue => issue.Code == "MISSING_DEPENDENCY");
    }

    [Fact]
    public async Task ArchiveTraversalIsRejected()
    {
        var archive = Path.Combine(_root, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.ini");
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("[Bad]");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new ModImporter(_paths).StageAsync(archive));
    }

    [Fact]
    public async Task FolderImportFindsMultipleModsWithAssetsInSubdirectories()
    {
        var source = Path.Combine(_root, "Multi Mod Pack");
        var first = Path.Combine(source, "First Mod");
        var second = Path.Combine(source, "Second Mod");
        Directory.CreateDirectory(Path.Combine(first, "Buffers"));
        Directory.CreateDirectory(Path.Combine(second, "Textures"));
        File.WriteAllText(Path.Combine(first, "first.ini"), "[ResourceBody]\nfilename = Buffers/body.buf", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(first, "Buffers", "body.buf"), [1]);
        File.WriteAllText(Path.Combine(second, "second.ini"), "[ResourceDiffuse]\nfilename = Textures/body.dds", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(second, "Textures", "body.dds"), [2]);
        var before = FileSystemSafety.ComputeDirectoryFingerprint(source);

        var session = await new ModImporter(_paths).StageAsync(source);

        Assert.Equal(2, session.Candidates.Count);
        Assert.Contains(session.Candidates, candidate => Path.GetFileName(candidate.StagedPath) == "First Mod");
        Assert.Contains(session.Candidates, candidate => Path.GetFileName(candidate.StagedPath) == "Second Mod");
        Assert.Equal(before, FileSystemSafety.ComputeDirectoryFingerprint(source));
    }

    [Fact]
    public async Task FolderImportKeepsRootIniWithOptionalFeatureFoldersAsOneMod()
    {
        var source = Path.Combine(_root, "Hatsune Seedku");
        var misc = Path.Combine(source, "Misc");
        var soundWave = Path.Combine(source, "SoundWave");
        Directory.CreateDirectory(misc);
        Directory.CreateDirectory(soundWave);
        File.WriteAllText(Path.Combine(source, "Seed.ini"), "[TextureOverrideSeed]\nhash = deadbeef\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "SeedBodyA.ib"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(misc, "SeedScooter.ini"), "[TextureOverrideSeedScooter]\nhash = aabbccdd\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(misc, "SeedScooter.dds"), [4]);
        File.WriteAllText(Path.Combine(soundWave, "SeedSr.ini"), "[TextureOverrideSeedSr]\nhash = 11223344\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(soundWave, "SeedSrBody.ib"), [5]);

        var session = await new ModImporter(_paths).StageAsync(source);
        try
        {
            var candidate = Assert.Single(session.Candidates);
            Assert.Equal(string.Empty, candidate.RelativeRoot);
            Assert.Equal("Hatsune Seedku", candidate.DisplayName);
            Assert.True(File.Exists(Path.Combine(candidate.StagedPath, "Seed.ini")));
            Assert.True(File.Exists(Path.Combine(candidate.StagedPath, "Misc", "SeedScooter.ini")));
            Assert.True(File.Exists(Path.Combine(candidate.StagedPath, "SoundWave", "SeedSr.ini")));
        }
        finally
        {
            new ModImporter(_paths).Cleanup(session);
        }
    }

    [Fact]
    public async Task ArchiveDuplicateTargetPathIsRejected()
    {
        var archive = Path.Combine(_root, "duplicate.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            await WriteZipEntry(zip, "Mod/body.buf", "first");
            await WriteZipEntry(zip, "mod/BODY.BUF", "second");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ModImporter(_paths).StageAsync(archive));

        Assert.Contains("重复目标路径", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledArchiveImportCleansItsStagingTransaction()
    {
        var archive = Path.Combine(_root, "cancelled.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            await WriteZipEntry(zip, "Mod/mod.ini", "[TextureOverride]\nhash = deadbeef");
            await WriteZipEntry(zip, "Mod/body.buf", "payload");
        }

        var before = Directory.EnumerateDirectories(_paths.StagingRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ModImporter(_paths).StageAsync(archive, cancellation.Token));

        var after = Directory.EnumerateDirectories(_paths.StagingRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(before.SetEquals(after));
    }

    [Fact]
    public void RuntimeIntegrityAllowsManagerOwnedD3dxConfiguration()
    {
        var source = Path.Combine(_root, "runtime-source");
        Directory.CreateDirectory(Path.Combine(source, "Core", "ZZMI"));
        File.WriteAllBytes(Path.Combine(source, "d3d11.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(source, "d3dcompiler_47.dll"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(source, "3dmloader.dll"), [7, 8, 9]);
        File.WriteAllText(Path.Combine(source, "d3dx.ini"), "include_recursive = Mods", Encoding.UTF8);
        File.WriteAllText(Path.Combine(source, "Core", "ZZMI", "main.ini"), "namespace = ZZMIv1", Encoding.UTF8);

        var manager = new RuntimeManager(_paths, new JsonFileStore());
        manager.InstallFromFolder(source);
        Assert.True(manager.Validate().IsValid);
        var configured = File.ReadAllText(Path.Combine(_paths.RuntimeRoot, "d3dx.ini"));
        manager.RepairConfiguration();
        Assert.True(manager.Validate().IsValid);
        var repaired = File.ReadAllText(Path.Combine(_paths.RuntimeRoot, "d3dx.ini"));
        Assert.Equal(configured, repaired);
        Assert.Contains("..\\..\\Mods", repaired);
        Assert.Contains("include = Core\\ZZMI\\ZZZModManager.ini", repaired, StringComparison.OrdinalIgnoreCase);
        var managerInput = File.ReadAllText(
            Path.Combine(_paths.RuntimeRoot, "Core", "ZZMI", "ZZZModManager.ini"));
        Assert.Contains("key = no_modifiers F10", managerInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"reload_fixes = {ManagerGameBindings.ReloadIniBinding}", repaired, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"reload_config = {ManagerGameBindings.ReloadIniBinding}", repaired, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reload_fixes = no_modifiers VK_F10", repaired, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reload_config = no_modifiers VK_F10", repaired, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skip_early_includes_load = 0", repaired, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("config_initialization_delay = -1", repaired, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeInstallRollsBackNewRuntimeAndManifestWhenSaveFails()
    {
        var originalSource = CreateRuntimeSource("Original Runtime", 1);
        var baseline = new RuntimeManager(_paths, new JsonFileStore());
        baseline.InstallFromFolder(originalSource);
        var originalHash = FileSystemSafety.ComputeFileSha256(Path.Combine(_paths.RuntimeRoot, "d3d11.dll"));
        var originalManifest = File.ReadAllText(_paths.RuntimeManifestFile);

        var replacementSource = CreateRuntimeSource("Replacement Runtime", 2);
        var store = new FailingJsonFileStore { FailNextSave = true };
        var manager = new RuntimeManager(_paths, store);

        Assert.Throws<IOException>(() => manager.InstallFromFolder(replacementSource));

        Assert.Equal(originalHash, FileSystemSafety.ComputeFileSha256(Path.Combine(_paths.RuntimeRoot, "d3d11.dll")));
        Assert.Equal(originalManifest, File.ReadAllText(_paths.RuntimeManifestFile));
        Assert.True(manager.Validate().IsValid, manager.Validate().Message);
        Assert.Empty(Directory.EnumerateDirectories(_paths.Root, "runtime-install-*"));
        Assert.Empty(Directory.EnumerateFiles(_paths.Root, "runtime-manifest-backup-*.json"));
    }

    [Fact]
    public void RuntimeValidationRejectsMissingOrEmptyManifest()
    {
        var source = CreateRuntimeSource("Manifest Runtime", 3);
        var manager = new RuntimeManager(_paths, new JsonFileStore());
        manager.InstallFromFolder(source);

        File.Delete(_paths.RuntimeManifestFile);
        var missing = manager.Validate();
        Assert.False(missing.IsValid);
        Assert.Contains("runtime-manifest", missing.Message, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(_paths.RuntimeManifestFile, "{}");
        var empty = manager.Validate();
        Assert.False(empty.IsValid);
        Assert.Contains("runtime-manifest", empty.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameSettingsUsesUniqueBackupNamesForConsecutiveConfigurations()
    {
        var game = Path.Combine(_root, "game", "ZenlessZoneZero.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        File.WriteAllBytes(game, [1]);
        var settings = new GameSettingsManager();

        settings.Configure(game, _paths.BackupsRoot);
        settings.Configure(game, _paths.BackupsRoot);
        settings.Configure(game, _paths.BackupsRoot);

        var backups = Directory.EnumerateFiles(_paths.BackupsRoot, "GENERAL_DATA-*.bin").ToList();
        Assert.Equal(2, backups.Count);
        Assert.Equal(2, backups.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void HotkeyReaderListsAccessoryTogglesAndHelpOverlay()
    {
        var mod = Path.Combine(_root, "Hotkey Mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "Velina.ini"), """
[KeyEarring]
key = 0
type = cycle
$earring = 0,1

[KeyStockings]
condition = $active == 1
key = no_ctrl no_alt VK_DOWN
type = cycle
$stockings = 0,1
""", Encoding.UTF8);
        File.WriteAllText(Path.Combine(mod, "draw_image.ini"), """
[KeyHelp]
key = H
type = cycle
$help = 0,1
""", Encoding.UTF8);

        var hotkeys = ModHotkeyReader.Read(mod);

        Assert.Equal(3, hotkeys.Count);
        Assert.Contains(hotkeys, hotkey => hotkey.DisplayName == "Earring" && hotkey.Keys.Contains("0"));
        Assert.Contains(hotkeys, hotkey => hotkey.DisplayName == "Stockings" && hotkey.Keys.Contains("no_ctrl no_alt VK_DOWN"));
        Assert.Contains(hotkeys, hotkey => hotkey.DisplayName == "Help" && hotkey.Keys.Contains("H"));
    }

    [Fact]
    public void LiveSwitchServiceAddsManagerOwnedGateIdempotently()
    {
        var root = Path.Combine(_paths.ModsRoot, "Live Mod");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "mod.ini"), """
[Constants]
global $active = 0

[KeyAccessory]
condition = $active == 1
key = P
type = cycle
$accessory = 0,1

[TextureOverrideBody]
hash = deadbeef
handling = skip
vb0 = ResourceBody

[ResourceBody]
filename = body.buf
""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(root, "body.buf"), [1, 2, 3]);

        var manifest = new ModManifest
        {
            Id = "Live_Mod",
            DisplayName = "Live Mod",
            InstalledDirectory = "Live Mod",
            Enabled = true,
            LiveSwitchKey = "F13",
            LiveSwitchVariable = "zzzmgr_enabled_deadbeef",
            LiveSwitchSlot = 0
        };
        var service = new LiveModSwitchService(_paths);

        service.Prepare(manifest);
        service.Prepare(manifest);

        var ini = File.ReadAllText(Path.Combine(root, "mod.ini"));
        var qualified = $"$\\ZZZModManager\\{manifest.LiveSwitchVariable}\\enabled";
        Assert.Single(Regex.Matches(ini, "ZZZMOD-LIVE-GUARD-BEGIN"));
        Assert.Contains($"condition = ($active == 1) && {qualified}", ini, StringComparison.Ordinal);
        Assert.Contains($"if {qualified}", ini, StringComparison.Ordinal);
        Assert.True(ini.IndexOf("handling = skip", StringComparison.Ordinal) > ini.IndexOf($"if {qualified}", StringComparison.Ordinal));
        var control = File.ReadAllText(Path.Combine(root, "zzzmod-live.ini"));
        Assert.Contains("namespace = ZZZModManager\\zzzmgr_enabled_deadbeef", control, StringComparison.Ordinal);
        Assert.Contains("key = ctrl alt no_shift VK_F13", control, StringComparison.Ordinal);
        Assert.Contains("key = ctrl alt shift VK_F13", control, StringComparison.Ordinal);
        Assert.Contains("$enabled = 0", control, StringComparison.Ordinal);
        Assert.Contains("$enabled = 1", control, StringComparison.Ordinal);
        Assert.DoesNotContain("type = cycle", control, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ModHotkeyReader.Read(root), hotkey => hotkey.DisplayName.Contains("ZZZModToggle", StringComparison.OrdinalIgnoreCase));

        // Migrate copies prepared by the first live-switch implementation,
        // where handling=skip was left outside the guard.
        File.WriteAllText(Path.Combine(root, "mod.ini"), ini.Replace(
            $"if {qualified}\nhandling = skip",
            $"handling = skip\nif {qualified}",
            StringComparison.Ordinal), Encoding.UTF8);
        service.Prepare(manifest);
        ini = File.ReadAllText(Path.Combine(root, "mod.ini"));
        Assert.True(ini.IndexOf("handling = skip", StringComparison.Ordinal) > ini.IndexOf($"if {qualified}", StringComparison.Ordinal));

        service.SetDefault(manifest, false);
        Assert.Contains("global $enabled = 0", File.ReadAllText(Path.Combine(root, "zzzmod-live.ini")), StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterGroupDetectorRecognizesVelinaFromIniAndKeepsUnknownModsSeparate()
    {
        var velina = Path.Combine(_root, "velina");
        Directory.CreateDirectory(velina);
        File.WriteAllText(Path.Combine(velina, "character.ini"), "[TextureOverrideVelina]\nhash = bd043a8e", Encoding.UTF8);
        var velinaManifest = new ModManifest { DisplayName = "Prayer Outfit", InstalledDirectory = "Velina" };
        Assert.Equal("维琳娜 / Velina", CharacterGroupDetector.Detect(velinaManifest, velina));

        var first = new ModManifest { DisplayName = "作者特别外观", InstalledDirectory = "First" };
        var second = new ModManifest { DisplayName = "另一个作者特别外观", InstalledDirectory = "Second" };
        Assert.NotEqual(
            CharacterGroupDetector.Detect(first, Path.Combine(_root, "missing-first")),
            CharacterGroupDetector.Detect(second, Path.Combine(_root, "missing-second")));
    }

    [Fact]
    public void CharacterGroupDetectorDiscoversUnknownRoleFromTextureOverrideAndKeepsKeyStable()
    {
        var firstPath = Path.Combine(_root, "remielle-first");
        var secondPath = Path.Combine(_root, "remielle-second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        const string ini = "[TextureOverrideRemielleBody]\nhash = deadbeef";
        File.WriteAllText(Path.Combine(firstPath, "remielle.ini"), ini, Encoding.UTF8);
        File.WriteAllText(Path.Combine(secondPath, "remielle.ini"), ini, Encoding.UTF8);

        var first = CharacterGroupDetector.DetectInfo(
            new ModManifest { Id = "remielle-a", DisplayName = "Remielle Outfit A", InstalledDirectory = "remielle-first" },
            firstPath);
        var second = CharacterGroupDetector.DetectInfo(
            new ModManifest { Id = "remielle-b", DisplayName = "Remielle Outfit B", InstalledDirectory = "remielle-second" },
            secondPath);

        Assert.Equal(CharacterGroupKind.Discovered, first.Kind);
        Assert.Equal(first.Key, second.Key);
        Assert.Contains("Remielle", first.DisplayName, StringComparison.OrdinalIgnoreCase);

        var chinese = CharacterGroupDetector.DetectInfo(
            new ModManifest
            {
                Id = "xide",
                DisplayName = "席德流萤2.0 - Mod_席德",
                InstalledDirectory = "xide"
            },
            Path.Combine(_root, "missing-xide"));
        Assert.Equal(CharacterGroupKind.Character, chinese.Kind);
        Assert.Equal("seth", chinese.Key);

        var installedChinesePath = Path.Combine(new AppPaths().ModsRoot, "DISABLED_席德流萤2.0_-_Mod_席德");
        if (Directory.Exists(installedChinesePath))
        {
            var installed = CharacterGroupDetector.DetectInfo(
                new ModManifest
                {
                    Id = "installed-xide",
                    DisplayName = "席德流萤2.0 - Mod_席德",
                    InstalledDirectory = "DISABLED_席德流萤2.0_-_Mod_席德"
                },
                installedChinesePath);
            Assert.Equal(CharacterGroupKind.Character, installed.Kind);
            Assert.Equal("seth", installed.Key);
        }
    }

    [Fact]
    public void CharacterGroupDetectorLeavesUtilityAndNormalFixModsOutOfRoleGroups()
    {
        var path = Path.Combine(_root, "utility");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "normal.ini"), "[TextureOverride_NormalMap2048]\nhash = deadbeef", Encoding.UTF8);

        var group = CharacterGroupDetector.DetectInfo(
            new ModManifest
            {
                Id = "normal-fix",
                DisplayName = "1.3 法线修复",
                InstalledDirectory = "utility"
            },
            path);

        Assert.Equal(CharacterGroupKind.Unknown, group.Kind);
    }

    [Fact]
    public async Task CustomCharacterGroupIsStableAndPersistsAcrossLibraryReload()
    {
        var library = new ModLibrary(_paths, new JsonFileStore(), new ConflictDetector());
        var custom = CharacterGroupDetector.CreateCustomGroup("Remielle 自定义");
        library.RegisterCustomCharacterGroup(custom);

        var source = CreateMod("Custom Group Mod");
        var candidate = Assert.Single((await new ModImporter(_paths).StageAsync(source)).Candidates);
        var manifest = library.Install(candidate, new ImportReport { Status = ImportStatus.Ready });
        library.SetCharacterGroupOverride(manifest.Id, custom.Key);

        Assert.Contains(library.GetAvailableCharacterGroups(), group => group.Key == custom.Key);
        Assert.Equal(custom.Key, library.DetectCharacterGroup(manifest).Key);

        var reloaded = new ModLibrary(_paths, new JsonFileStore(), new ConflictDetector());
        Assert.Contains(reloaded.GetAvailableCharacterGroups(), group =>
            group.Key == custom.Key && group.Kind == CharacterGroupKind.Custom);
        Assert.Equal(custom.Key, reloaded.DetectCharacterGroup(reloaded.GetAll().Single()).Key);
        Assert.Equal(3, new JsonFileStore().Load(_paths.LibraryFile, () => new LibraryState()).SchemaVersion);
    }

    [Fact]
    public void DependencyResolverReportsOnlyDependenciesMissingFromEnabledLibrary()
    {
        var rabbit = new ModManifest
        {
            Id = "RabbitFX-v7.7",
            DisplayName = "RabbitFX-v7.7",
            InstalledDirectory = "RabbitFX-v7.7",
            Enabled = true
        };
        var velina = new ModManifest
        {
            Id = "Velina",
            DisplayName = "Velina",
            InstalledDirectory = "Velina",
            Enabled = true,
            Dependencies = ["RabbitFX"]
        };
        var resolver = new DependencyResolver(_paths);

        Assert.Empty(resolver.GetMissingDependencies(velina, [rabbit, velina]));
        rabbit.Enabled = false;
        Assert.Equal(["RabbitFX"], resolver.GetMissingDependencies(velina, [rabbit, velina]));
    }

    [Fact]
    public void DependencyResolverRejectsAnExplicitlyOutdatedRabbitFxProvider()
    {
        var rabbit = new ModManifest
        {
            Id = "RabbitFX-v7.6",
            DisplayName = "RabbitFX-v7.6",
            InstalledDirectory = "RabbitFX-v7.6",
            Enabled = true
        };
        var velina = new ModManifest
        {
            Id = "Velina",
            DisplayName = "Velina",
            InstalledDirectory = "Velina",
            Enabled = true,
            Dependencies = ["RabbitFX>=7.7"]
        };
        var resolver = new DependencyResolver(_paths);

        Assert.Equal(["RabbitFX >= 7.7（已安装 7.6）"], resolver.GetMissingDependencies(velina, [rabbit, velina]));
        var currentRabbit = new ModManifest
        {
            Id = "RabbitFX-v7.7",
            DisplayName = "RabbitFX-v7.7",
            InstalledDirectory = "RabbitFX-v7.7",
            Enabled = true
        };
        Assert.Empty(resolver.GetMissingDependencies(velina, [currentRabbit, velina]));
    }

    [Fact]
    public void LoggerPreservesWarningAndErrorLevelsAfterReload()
    {
        var logger = new AppLogger(_paths);
        logger.Warning("依赖版本过低");
        logger.Error("安装事务失败");

        var reloaded = new AppLogger(_paths);

        Assert.Collection(
            reloaded.Entries,
            warning =>
            {
                Assert.Equal(AppLogLevel.Warning, warning.Level);
                Assert.Equal("依赖版本过低", warning.Message);
            },
            error =>
            {
                Assert.Equal(AppLogLevel.Error, error.Level);
                Assert.Equal("安装事务失败", error.Message);
            });
    }

    [Fact]
    public void LoggerLoadsLegacyLinesAsInformation()
    {
        File.WriteAllText(Path.Combine(_paths.LogsRoot, "manager.log"), "[12:34:56] 旧格式日志" + Environment.NewLine, Encoding.UTF8);

        var logger = new AppLogger(_paths);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(AppLogLevel.Info, entry.Level);
        Assert.Equal("旧格式日志", entry.Message);
    }

    [Fact]
    public void LoggerSupportsConcurrentBackgroundWritersAndSnapshotReaders()
    {
        var logger = new AppLogger(_paths);

        Parallel.For(0, 200, index =>
        {
            logger.Info($"并发日志 {index}");
            _ = logger.Entries.Count;
        });

        var reloaded = new AppLogger(_paths);
        Assert.Equal(200, reloaded.Entries.Count);
        Assert.All(reloaded.Entries, entry => Assert.Equal(AppLogLevel.Info, entry.Level));
        Assert.Equal(200, reloaded.Entries.Select(entry => entry.Message).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LoggerTrimsPersistedHistoryOnStartup()
    {
        var path = Path.Combine(_paths.LogsRoot, "manager.log");
        var lines = Enumerable.Range(0, AppLogger.MaximumEntries + 25)
            .Select(index => $"[12:34:56] [Info] history {index}")
            .ToArray();
        File.WriteAllLines(path, lines, Encoding.UTF8);

        var logger = new AppLogger(_paths);

        Assert.Equal(AppLogger.MaximumEntries, logger.Entries.Count);
        Assert.Equal("history 25", logger.Entries[0].Message);
        Assert.Equal(AppLogger.MaximumEntries, File.ReadAllLines(path, Encoding.UTF8).Length);
    }

    [Fact]
    public void InstallRollsBackDirectoryAndMemoryWhenManifestSaveFails()
    {
        var source = Path.Combine(_root, "Install Rollback Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "mod.ini"), "[TextureOverride]\nhash = deadbeef", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "body.buf"), [1, 2, 3]);
        var store = new FailingJsonFileStore();
        var library = new ModLibrary(_paths, store, new ConflictDetector());
        store.FailNextSave = true;
        var candidate = new ImportCandidate
        {
            DisplayName = "Rollback Mod",
            StagedPath = source,
            SourcePath = source,
            SourceSha256 = "source"
        };
        var report = new ImportReport { Status = ImportStatus.Ready };

        Assert.Throws<IOException>(() => library.Install(candidate, report));

        Assert.Empty(library.GetAll());
        Assert.Empty(Directory.EnumerateDirectories(_paths.ModsRoot));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public void StateChangeRollsBackDirectoryAndManifestWhenSaveFails()
    {
        var source = Path.Combine(_root, "State Rollback Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "mod.ini"), "[TextureOverride]\nhash = deadbeef", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "body.buf"), [1, 2, 3]);
        var store = new FailingJsonFileStore();
        var library = new ModLibrary(_paths, store, new ConflictDetector());
        var manifest = library.Install(
            new ImportCandidate { DisplayName = "State Rollback", StagedPath = source, SourcePath = source },
            new ImportReport { Status = ImportStatus.Ready });
        var disabledPath = library.GetAbsolutePath(manifest);
        store.FailNextSave = true;

        Assert.Throws<IOException>(() => library.SetEnabled(manifest.Id, true));

        Assert.False(manifest.Enabled);
        Assert.StartsWith("DISABLED_", manifest.InstalledDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(disabledPath));
        Assert.False(Directory.Exists(Path.Combine(_paths.ModsRoot, manifest.Id)));
    }

    [Fact]
    public void DeleteRollsBackDirectoryAndManifestWhenSaveFails()
    {
        var source = Path.Combine(_root, "Delete Rollback Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "mod.ini"), "[TextureOverride]\nhash = deadbeef", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "body.buf"), [1, 2, 3]);
        var store = new FailingJsonFileStore();
        var library = new ModLibrary(_paths, store, new ConflictDetector());
        var manifest = library.Install(
            new ImportCandidate { DisplayName = "Delete Rollback", StagedPath = source, SourcePath = source },
            new ImportReport { Status = ImportStatus.Ready });
        var installedPath = library.GetAbsolutePath(manifest);
        store.FailNextSave = true;

        Assert.Throws<IOException>(() => library.Delete(manifest.Id));

        Assert.Contains(library.GetAll(), item => item.Id == manifest.Id);
        Assert.True(Directory.Exists(installedPath));
        Assert.Empty(Directory.EnumerateDirectories(_paths.ModsRoot, "DISABLED_DELETING_*"));
        var reloaded = new ModLibrary(_paths, new JsonFileStore(), new ConflictDetector());
        Assert.Contains(reloaded.GetAll(), item => item.Id == manifest.Id);
        Assert.True(Directory.Exists(reloaded.GetAbsolutePath(manifest)));
    }

    [Fact]
    public void PreviewLoaderBoundsTallImagesAndRejectsDamagedFiles()
    {
        var tallPath = Path.Combine(_root, "preview.png");
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(
            8, 4096, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var output = File.Create(tallPath))
        {
            encoder.Save(output);
        }

        var thumbnail = PreviewImageLoader.Load(tallPath, 480);

        Assert.NotNull(thumbnail);
        Assert.True(thumbnail.PixelHeight <= 480);
        Assert.True(thumbnail.PixelWidth > 0);
        Assert.Same(thumbnail, PreviewImageLoader.Load(tallPath, 480));
        var damagedPath = Path.Combine(_root, "damaged.png");
        File.WriteAllText(damagedPath, "not an image", Encoding.UTF8);
        Assert.Null(PreviewImageLoader.Load(damagedPath, 480));
    }

    [Fact]
    public void GameModReloadServiceSkipsWhenGameIsNotRunning()
    {
        var result = new GameModReloadService().Reload(Path.Combine(_root, "missing-game.exe"));

        Assert.False(result.GameRunning);
        Assert.False(result.Succeeded);
        Assert.Contains("未设置有效的游戏路径", result.Message);
    }

    [Fact]
    public void EmbeddedRuntimeRepairsACleanInstallationOffline()
    {
        var runtime = new RuntimeManager(_paths, new JsonFileStore());

        runtime.Repair();

        var validation = runtime.Validate();
        Assert.True(validation.IsValid, validation.Message);
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeRoot, "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeRoot, "3dmloader.dll")));
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeRoot, "Core", "ZZMI", "main.ini")));
        Assert.True(File.Exists(_paths.RuntimeManifestFile));
        var d3dx = File.ReadAllText(Path.Combine(_paths.RuntimeRoot, "d3dx.ini"));
        Assert.Contains("include_recursive = ..\\..\\Mods", d3dx, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"reload_fixes = {ManagerGameBindings.ReloadIniBinding}", d3dx, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"reload_config = {ManagerGameBindings.ReloadIniBinding}", d3dx, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skip_early_includes_load = 0", d3dx, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("config_initialization_delay = -1", d3dx, StringComparison.OrdinalIgnoreCase);
    }

    private string CreateMod(string wrapper)
    {
        var root = Path.Combine(_root, wrapper);
        var velina = Path.Combine(root, "Velina");
        Directory.CreateDirectory(velina);
        File.WriteAllText(Path.Combine(velina, "Velina.ini"), """
[Constants]
$mole = 0,1,

[TextureOverrideVelina]
hash = bd043a8e
ib = ResourceVelinaIB
if $glow == 0
ps-u4 = ResourceEngineRGB
endif
run = CommandList\RabbitFX\Run

[ResourceVelinaIB]
type = Buffer
filename = Velina.ib

[ResourceUnused]
filename = MissingUnused.ib
""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(velina, "Velina.ib"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(root, "modname"), wrapper, Encoding.UTF8);
        return root;
    }

    private string CreateRuntimeSource(string name, byte marker)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(root, "Core", "ZZMI"));
        File.WriteAllBytes(Path.Combine(root, "d3d11.dll"), [marker, 1, 2]);
        File.WriteAllBytes(Path.Combine(root, "d3dcompiler_47.dll"), [marker, 3, 4]);
        File.WriteAllBytes(Path.Combine(root, "3dmloader.dll"), [marker, 5, 6]);
        File.WriteAllText(Path.Combine(root, "d3dx.ini"), "[Include]\ninclude_recursive = Mods", Encoding.UTF8);
        File.WriteAllText(Path.Combine(root, "Core", "ZZMI", "main.ini"), "Version = 1.4.3", Encoding.UTF8);
        return root;
    }

    private static async Task WriteZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        await writer.WriteAsync(content);
    }

    private sealed class FailingJsonFileStore : JsonFileStore
    {
        public bool FailNextSave { get; set; }

        public override void Save<T>(string path, T value)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("simulated manifest save failure");
            }

            base.Save(path, value);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
