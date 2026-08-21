using System.Text;
using Xunit;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using ZZZModManager.Services;

namespace ZZZModManager.Tests;

public sealed class RealtimeSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zzz-mm-realtime-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public RealtimeSwitchTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
    }

    [Fact]
    public void DuplicateLegacyF13BindingsMigrateToUniqueAbsoluteSlots()
    {
        var manifests = Enumerable.Range(0, 10).Select(index => CreateDirectManifest($"Velina {index}", "F13")).ToList();
        var service = new LiveModSwitchService(_paths);

        var summary = service.PrepareAll(manifests);

        Assert.Equal(10, summary.ImmediateCount);
        Assert.Equal(10, manifests.Select(manifest => manifest.LiveSwitchSlot).Distinct().Count());
        Assert.All(manifests, manifest => Assert.Equal("10", manifest.LiveSwitchRuleVersion));
        foreach (var manifest in manifests)
        {
            var control = File.ReadAllText(Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "zzzmod-live.ini"));
            Assert.Contains($"namespace = ZZZModManager\\{manifest.LiveSwitchVariable}", control, StringComparison.Ordinal);
            Assert.Contains("Rule v10", control, StringComparison.Ordinal);
            Assert.Contains("KeyZZZModDisable", control);
            Assert.Contains("KeyZZZModEnable", control);
            Assert.DoesNotContain("type = cycle", control, StringComparison.OrdinalIgnoreCase);
        }

        var enableChords = manifests.Select(manifest => service.GetStateChord(manifest, true)).ToList();
        var disableChords = manifests.Select(manifest => service.GetStateChord(manifest, false)).ToList();
        Assert.Equal(10, enableChords.Distinct().Count());
        Assert.Equal(10, disableChords.Distinct().Count());
        Assert.All(enableChords, chord => Assert.True(chord.Modifiers.HasFlag(GameKeyModifiers.Shift)));
        Assert.All(disableChords, chord => Assert.False(chord.Modifiers.HasFlag(GameKeyModifiers.Shift)));
    }

    [Fact]
    public void DisplayBindingIdentifiesManagerInternalChannelWithoutPhysicalFKey()
    {
        var manifest = CreateDirectManifest("Velina Internal Channel");
        var service = new LiveModSwitchService(_paths);

        service.PrepareAll([manifest]);

        var display = service.GetDisplayBinding(manifest, enabled: true);
        Assert.Contains("管理器内部控制", display, StringComparison.Ordinal);
        Assert.Contains("无需物理按键", display, StringComparison.Ordinal);
        Assert.DoesNotContain("F20", display, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ctrl+Alt", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoreThanFortyEightModsAreExplicitlyMarkedForReload()
    {
        var manifests = Enumerable.Range(0, 49).Select(index => CreateDirectManifest($"Mod {index}")).ToList();
        var summary = new LiveModSwitchService(_paths).PrepareAll(manifests);

        Assert.Equal(48, summary.ImmediateCount);
        Assert.Equal(1, summary.SlotUnavailableCount);
        var overflow = Assert.Single(manifests, manifest => manifest.LiveSwitchSlot is null);
        Assert.Equal(LiveSwitchCapability.SlotUnavailable, overflow.LiveSwitchCapability);
        Assert.DoesNotContain("[KeyZZZMod", File.ReadAllText(Path.Combine(_paths.ModsRoot, overflow.InstalledDirectory, "zzzmod-live.ini")));
    }

    [Fact]
    public void SameCharacterSingleSelectDoesNotDisableFramework()
    {
        var library = NewLibrary();
        var first = InstallSimple(library, "Velina Prayer A", "11111111");
        var second = InstallSimple(library, "Velina Prayer B", "22222222");
        var framework = InstallSimple(library, "RabbitFX v7.7", "33333333");
        library.SetEnabled(first.Id, true);
        library.SetEnabled(framework.Id, true);

        var result = library.ApplyStateBatch(second.Id, true, keepLoaded: false);

        Assert.Contains(result.DisabledByCharacter, manifest => manifest.Id == first.Id);
        Assert.False(library.GetAll().Single(manifest => manifest.Id == first.Id).Enabled);
        Assert.True(library.GetAll().Single(manifest => manifest.Id == second.Id).Enabled);
        Assert.True(library.GetAll().Single(manifest => manifest.Id == framework.Id).Enabled);
    }

    [Fact]
    public void CrossCharacterHashConflictIsDisabledInSameTransaction()
    {
        var library = NewLibrary();
        var velina = InstallSimple(library, "Velina Outfit", "deadbeef");
        var alice = InstallSimple(library, "Alice Outfit", "deadbeef");
        library.SetEnabled(velina.Id, true);

        var result = library.ApplyStateBatch(alice.Id, true, keepLoaded: false);

        Assert.Contains(result.DisabledByConflict, manifest => manifest.Id == velina.Id);
        Assert.False(velina.Enabled);
        Assert.True(alice.Enabled);
    }

    [Fact]
    public void LoadedModUsesAbsoluteDisableWithoutF10()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Velina Live", "aaaa0001");
        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput { ProcessId = 42 };
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));

        var first = coordinator.ApplyState(manifest.Id, true, restoreManagerWindow: true);
        Assert.Equal(ModStateApplication.Reloaded, first.Application);
        Assert.Contains(input.Sent, ManagerGameBindings.IsReloadChord);
        input.Sent.Clear();

        var second = coordinator.ApplyState(manifest.Id, false, restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Immediate, second.Application);
        var chord = Assert.Single(input.Sent);
        Assert.False(ManagerGameBindings.IsReloadChord(chord));
        Assert.Equal(GameKeyModifiers.Control | GameKeyModifiers.Alt, chord.Modifiers);
        Assert.Contains("命令已发送", second.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("立即启用", second.Message, StringComparison.Ordinal);
        Assert.Contains("global $enabled = 0",
            File.ReadAllText(Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "zzzmod-live.ini")));
    }

    [Fact]
    public void SameCharacterSwitchPhysicallyIsolatesOldModBeforeOneSafeReload()
    {
        var library = NewLibrary();
        var active = InstallSimple(library, "Velina Active Outfit", "aaaa1001");
        var pending = InstallSimple(library, "Velina Pending Outfit", "bbbb1001");
        library.SetEnabled(active.Id, true);

        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput();
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));
        coordinator.PrepareForLaunch();

        // Simulate a second immediate mod that was preloaded into ZZMI's tree
        // while still disabled.  This is the case that used to leave both
        // same-character match selectors active during a live switch.
        Assert.True(library.PreloadForLiveSwitch([pending.Id]));
        library.SaveChanges();
        input.ProcessId = 42;

        var result = coordinator.ApplyState(pending.Id, true, restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Reloaded, result.Application);
        Assert.Equal(1, input.Sent.Count(ManagerGameBindings.IsReloadChord));
        Assert.StartsWith("DISABLED_", Path.GetFileName(active.InstalledDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(library.GetAbsolutePath(active)));
        Assert.DoesNotContain("立即", result.Message, StringComparison.Ordinal);
        Assert.Contains("安全重载命令和状态恢复命令已发送", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HashConflictSwitchAlsoUsesSafeReloadInsteadOfAbsoluteOnlyCommand()
    {
        var library = NewLibrary();
        var active = InstallSimple(library, "Velina Hash Active", "dead1001");
        var pending = InstallSimple(library, "Alice Hash Pending", "dead1001");
        library.SetEnabled(active.Id, true);

        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput();
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));
        coordinator.PrepareForLaunch();
        Assert.True(library.PreloadForLiveSwitch([pending.Id]));
        library.SaveChanges();
        input.ProcessId = 73;

        var result = coordinator.ApplyState(pending.Id, true, restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Reloaded, result.Application);
        Assert.Equal(1, input.Sent.Count(ManagerGameBindings.IsReloadChord));
        Assert.StartsWith("DISABLED_", Path.GetFileName(active.InstalledDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("安全重载命令和状态恢复命令已发送", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedImmediateCommandFallsBackToOneReload()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Alice Live", "bbbb0001");
        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput { ProcessId = 73 };
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));
        coordinator.ApplyState(manifest.Id, true, restoreManagerWindow: true);
        input.Sent.Clear();
        input.FailNextNonReload = true;

        var result = coordinator.ApplyState(manifest.Id, false, restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Reloaded, result.Application);
        Assert.Equal(1, input.Sent.Count(ManagerGameBindings.IsReloadChord));
        Assert.False(manifest.Enabled);
    }

    [Fact]
    public void RootPreviewIsDetectedCaseInsensitively()
    {
        var manifest = CreateDirectManifest("Alice Preview");
        File.WriteAllBytes(Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "PREVIEW.PNG"), [1, 2, 3]);

        new LiveModSwitchService(_paths).PrepareAll([manifest]);

        Assert.Equal("PREVIEW.PNG", manifest.PreviewFile);
        Assert.Equal("alice", CharacterGroupDetector.DetectInfo(manifest, Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory)).Key);
    }

    [Fact]
    public void ForeignManagerGateFromReimportedCopyIsRemovedBeforeCurrentGateIsAdded()
    {
        var manifest = CreateDirectManifest("Velina Reimport");
        var path = Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "mod.ini");
        File.WriteAllText(path, """
            [KeyAccessory]
            condition = ($active == 1) && $zzzmgr_enabled_foreign
            key = P

            [TextureOverrideBody]
            hash = deadbeef
            ; ZZZMOD-LIVE-GUARD-BEGIN $zzzmgr_enabled_foreign
            if $zzzmgr_enabled_foreign
            handling = skip
            vb0 = ResourceBody
            ; ZZZMOD-LIVE-GUARD-END $zzzmgr_enabled_foreign
            endif

            [ResourceBody]
            filename = body.buf
            """, Encoding.UTF8);
        var service = new LiveModSwitchService(_paths);

        service.PrepareAll([manifest]);

        var repaired = File.ReadAllText(path);
        Assert.DoesNotContain("zzzmgr_enabled_foreign", repaired, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"$\\ZZZModManager\\{manifest.LiveSwitchVariable}\\enabled", repaired, StringComparison.Ordinal);
        Assert.True(service.Audit(manifest).IsSafe);
    }

    [Fact]
    public void MatchSelectorsRemainStaticWhenTextureOverrideIsGated()
    {
        var manifest = CreateDirectManifest("Velina Static Match");
        var path = Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "mod.ini");
        File.WriteAllText(path, """
            [TextureOverrideVelinaBodyDownA]
            hash = 6b25e6d8
            match_first_index = 0
            match_index_count = 30336
            handling = skip
            ib = ResourceBody

            [ResourceBody]
            filename = body.buf
            """, Encoding.UTF8);
        var service = new LiveModSwitchService(_paths);

        service.PrepareAll([manifest]);

        var ini = File.ReadAllText(path);
        var guard = ini.IndexOf("ZZZMOD-LIVE-GUARD-BEGIN", StringComparison.Ordinal);
        Assert.True(ini.IndexOf("match_first_index = 0", StringComparison.Ordinal) < guard);
        Assert.True(ini.IndexOf("match_index_count = 30336", StringComparison.Ordinal) < guard);
        Assert.True(ini.IndexOf("handling = skip", StringComparison.Ordinal) > guard);
        Assert.True(service.Audit(manifest).IsSafe);
    }

    [Fact]
    public void RuleThreeGateMigratesMatchIndexCountBackOutsideGuard()
    {
        var manifest = CreateDirectManifest("Velina Rule Three");
        manifest.LiveSwitchVariable = "zzzmgr_enabled_velina";
        manifest.LiveSwitchSlot = 0;
        var qualified = "$\\ZZZModManager\\zzzmgr_enabled_velina\\enabled";
        var path = Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "mod.ini");
        File.WriteAllText(path, $"""
            [TextureOverrideVelinaBodyDownA]
            hash = 6b25e6d8
            match_first_index = 0
            ; ZZZMOD-LIVE-GUARD-BEGIN {qualified}
            if {qualified}
            match_index_count = 30336
            handling = skip
            ib = ResourceBody
            ; ZZZMOD-LIVE-GUARD-END {qualified}
            endif

            [ResourceBody]
            filename = body.buf
            """, Encoding.UTF8);
        var service = new LiveModSwitchService(_paths);

        service.Prepare(manifest);
        service.Prepare(manifest);

        var ini = File.ReadAllText(path);
        var guard = ini.IndexOf("ZZZMOD-LIVE-GUARD-BEGIN", StringComparison.Ordinal);
        Assert.True(ini.IndexOf("match_index_count = 30336", StringComparison.Ordinal) < guard);
        Assert.True(ini.IndexOf("handling = skip", StringComparison.Ordinal) > guard);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ini, "match_index_count = 30336"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ini, "ZZZMOD-LIVE-GUARD-BEGIN"));
    }

    [Fact]
    public void VertexLimitModIsRestartOnlyAndHasNoControlFile()
    {
        var manifest = CreateDirectManifest("Velina Vertex Limit");
        var path = Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "mod.ini");
        File.WriteAllText(path, """
            [TextureOverrideVelinaBodyDownVertexLimitRaise]
            hash = 10675a0f
            override_vertex_count = 55734
            override_byte_stride = 40
            vb0 = ResourceBody

            [ResourceBody]
            filename = body.buf
            """, Encoding.UTF8);
        var service = new LiveModSwitchService(_paths);

        service.Prepare(manifest);

        var ini = File.ReadAllText(path);
        Assert.DoesNotContain("ZZZMOD-LIVE-GUARD", ini, StringComparison.Ordinal);
        Assert.Contains("override_vertex_count = 55734", ini, StringComparison.Ordinal);
        Assert.Contains("override_byte_stride = 40", ini, StringComparison.Ordinal);
        Assert.Contains("vb0 = ResourceBody", ini, StringComparison.Ordinal);
        Assert.Equal(LiveSwitchCapability.RequiresRestart, manifest.LiveSwitchCapability);
        Assert.Null(manifest.LiveSwitchSlot);
        Assert.False(File.Exists(Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "zzzmod-live.ini")));
    }

    [Fact]
    public void MetadataOnlyVertexLimitSectionDoesNotReceiveAnEmptyGuard()
    {
        var manifest = CreateDirectManifest("Velina Static Vertex Limit");
        var path = Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "mod.ini");
        File.WriteAllText(path, """
            [TextureOverrideVelinaBodyDownVertexLimitRaise]
            hash = 10675a0f
            override_vertex_count = 55734
            override_byte_stride = 40
            """, Encoding.UTF8);
        var service = new LiveModSwitchService(_paths);

        service.Prepare(manifest);

        var ini = File.ReadAllText(path);
        Assert.DoesNotContain("ZZZMOD-LIVE-GUARD", ini, StringComparison.Ordinal);
        Assert.Contains("override_vertex_count = 55734", ini, StringComparison.Ordinal);
        Assert.Contains("override_byte_stride = 40", ini, StringComparison.Ordinal);
        Assert.Equal(LiveSwitchCapability.RequiresRestart, manifest.LiveSwitchCapability);
    }

    [Fact]
    public void RunningVertexLimitModChangeIsSavedWithoutSendingF10()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Velina Static Runtime", "10675a0f");
        WriteVertexLimitIni(library.GetAbsolutePath(manifest));
        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput { ProcessId = 42 };
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));

        var result = coordinator.ApplyState(manifest.Id, true, restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Pending, result.Application);
        Assert.True(manifest.Enabled);
        Assert.Empty(input.Sent);
        Assert.Contains("重启游戏", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupPreloadsStaticModAndSwitchesWithoutF10()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Velina Startup Live", "10675a0f");
        WriteVertexLimitIni(library.GetAbsolutePath(manifest));
        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput();
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));

        coordinator.PrepareForLaunch();

        Assert.Equal(LiveSwitchCapability.Immediate, manifest.LiveSwitchCapability);
        Assert.False(manifest.Enabled);
        Assert.False(Path.GetFileName(manifest.InstalledDirectory).StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase));
        var ini = File.ReadAllText(Path.Combine(library.GetAbsolutePath(manifest), "mod.ini"));
        Assert.Contains("override_vertex_count = 55734", ini, StringComparison.Ordinal);
        Assert.Contains("ZZZMOD-LIVE-GUARD-BEGIN", ini, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(library.GetAbsolutePath(manifest), "zzzmod-live.ini")));
        Assert.True(live.Audit(manifest).IsSafe);

        input.ProcessId = 42;
        var enabled = coordinator.ApplyState(manifest.Id, true, restoreManagerWindow: true);
        Assert.Equal(ModStateApplication.Immediate, enabled.Application);
        Assert.DoesNotContain(input.Sent, ManagerGameBindings.IsReloadChord);

        input.Sent.Clear();
        var disabled = coordinator.ApplyState(manifest.Id, false, restoreManagerWindow: true);
        Assert.Equal(ModStateApplication.Immediate, disabled.Application);
        Assert.DoesNotContain(input.Sent, ManagerGameBindings.IsReloadChord);
    }

    [Fact]
    public void PreparedStaticSameHashSwitchUsesSafeReloadWithoutRestartRequirement()
    {
        var library = NewLibrary();
        var active = InstallSimple(library, "Velina Static Active", "10675a0f");
        var disabled = InstallSimple(library, "Velina Static Disabled", "10675a0f");
        library.SetEnabled(active.Id, true);
        WriteVertexLimitIni(library.GetAbsolutePath(active), vertexCount: 55734, byteStride: 40);
        WriteVertexLimitIni(library.GetAbsolutePath(disabled), vertexCount: 60000, byteStride: 40);

        var live = new LiveModSwitchService(_paths);
        var input = new FakeGameInput();
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));

        coordinator.PrepareForLaunch();

        Assert.Equal(LiveSwitchCapability.Immediate, active.LiveSwitchCapability);
        Assert.Equal(LiveSwitchCapability.Immediate, disabled.LiveSwitchCapability);
        Assert.False(disabled.Enabled);
        Assert.False(Path.GetFileName(disabled.InstalledDirectory).StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase));

        input.ProcessId = 42;
        var result = coordinator.ApplyState(disabled.Id, true, restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Reloaded, result.Application);
        Assert.Equal(1, input.Sent.Count(ManagerGameBindings.IsReloadChord));
        Assert.DoesNotContain("重启游戏", result.Message, StringComparison.Ordinal);
        Assert.Equal(LiveSwitchCapability.Immediate, active.LiveSwitchCapability);
        Assert.Equal(LiveSwitchCapability.Immediate, disabled.LiveSwitchCapability);
        Assert.True(disabled.Enabled);
    }

    [Fact]
    public void SameHashDifferentVertexCountsArePreloadedTogether()
    {
        var library = NewLibrary();
        var first = InstallSimple(library, "Velina Capacity A", "10675a0f");
        var second = InstallSimple(library, "Velina Capacity B", "10675a0f");
        WriteVertexLimitIni(library.GetAbsolutePath(first), vertexCount: 55734, byteStride: 40);
        WriteVertexLimitIni(library.GetAbsolutePath(second), vertexCount: 60000, byteStride: 40);

        var live = new LiveModSwitchService(_paths);
        var summary = live.PrepareForStartup(library.GetAll());

        Assert.Equal(2, summary.ImmediateCount);
        Assert.Equal(0, summary.RestartOnlyCount);
        Assert.Equal(LiveSwitchCapability.Immediate, first.LiveSwitchCapability);
        Assert.Equal(LiveSwitchCapability.Immediate, second.LiveSwitchCapability);
        Assert.All(new[] { first, second }, manifest =>
        {
            var path = library.GetAbsolutePath(manifest);
            var ini = File.ReadAllText(Path.Combine(path, "mod.ini"));
            var guard = ini.IndexOf("ZZZMOD-LIVE-GUARD-BEGIN", StringComparison.Ordinal);
            Assert.True(guard > 0);
            Assert.True(ini.IndexOf("override_vertex_count", StringComparison.OrdinalIgnoreCase) < guard);
            Assert.True(ini.IndexOf("override_byte_stride", StringComparison.OrdinalIgnoreCase) < guard);
            Assert.True(File.Exists(Path.Combine(path, "zzzmod-live.ini")));
            Assert.True(live.Audit(manifest).IsSafe);
        });
    }

    [Fact]
    public void SameHashFallbackCapacityAndDifferentByteStrideAreBothPreloaded()
    {
        var library = NewLibrary();
        var fallback = InstallSimple(library, "Velina Capacity Fallback", "10675a0f");
        var explicitCount = InstallSimple(library, "Velina Capacity Explicit", "10675a0f");
        WriteVertexLimitIniWithoutCount(library.GetAbsolutePath(fallback), byteStride: 32);
        WriteVertexLimitIni(library.GetAbsolutePath(explicitCount), vertexCount: 55734, byteStride: 40);

        var live = new LiveModSwitchService(_paths);
        var summary = live.PrepareForStartup(library.GetAll());

        Assert.Equal(2, summary.ImmediateCount);
        Assert.Equal(0, summary.RestartOnlyCount);
        Assert.Equal(LiveSwitchCapability.Immediate, fallback.LiveSwitchCapability);
        Assert.Equal(LiveSwitchCapability.Immediate, explicitCount.LiveSwitchCapability);
        var fallbackIni = File.ReadAllText(Path.Combine(library.GetAbsolutePath(fallback), "mod.ini"));
        Assert.DoesNotContain("override_vertex_count", fallbackIni, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("override_byte_stride = 32", fallbackIni, StringComparison.Ordinal);
        Assert.Contains("ZZZMOD-LIVE-GUARD-BEGIN", fallbackIni, StringComparison.Ordinal);
        Assert.Contains("override_vertex_count = 55734", File.ReadAllText(Path.Combine(library.GetAbsolutePath(explicitCount), "mod.ini")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(library.GetAbsolutePath(fallback), "zzzmod-live.ini")));
        Assert.True(File.Exists(Path.Combine(library.GetAbsolutePath(explicitCount), "zzzmod-live.ini")));
        Assert.True(live.Audit(fallback).IsSafe);
        Assert.True(live.Audit(explicitCount).IsSafe);
    }

    [Fact]
    public void RealVelinaCopyPassesStartupGateAuditWhenPresent()
    {
        var source = Path.Combine("D:\\ZZZMod", "Mods", "Velina");
        if (!Directory.Exists(source))
        {
            return;
        }

        var fixture = Path.Combine(_paths.ModsRoot, "Velina_Fixture");
        FileSystemSafety.CopyDirectory(source, fixture);
        var manifest = new ModManifest
        {
            Id = "Velina_Fixture",
            DisplayName = "Velina Fixture",
            InstalledDirectory = "Velina_Fixture"
        };
        var service = new LiveModSwitchService(_paths);

        service.PrepareForStartup([manifest]);

        Assert.Equal(LiveSwitchCapability.Immediate, manifest.LiveSwitchCapability);
        Assert.True(service.Audit(manifest).IsSafe);
        var ini = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(fixture, "*.ini", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("override_vertex_count", ini, StringComparison.Ordinal);
        Assert.Contains("ZZZMOD-LIVE-GUARD-BEGIN", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualReloadIsBlockedWhileVertexLimitModIsLoaded()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Velina Static Loaded", "10675a0f");
        library.SetEnabled(manifest.Id, true);
        WriteVertexLimitIni(library.GetAbsolutePath(manifest));
        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput { ProcessId = 42 };
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));

        var result = coordinator.ReloadAndSynchronize(restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Pending, result.Application);
        Assert.Empty(input.Sent);
        Assert.Contains("底层重载", result.Message, StringComparison.Ordinal);
        Assert.Contains("重启游戏", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchPreparationNormalizesPendingDisabledDirectory()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Velina Pending Disable", "10675a0f");
        library.SetEnabled(manifest.Id, true);
        library.SetEnabled(manifest.Id, false, keepLoaded: true);
        Assert.False(Path.GetFileName(manifest.InstalledDirectory).StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase));
        var live = new LiveModSwitchService(_paths);
        var input = new FakeGameInput();
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));

        coordinator.PrepareForLaunch();

        Assert.StartsWith("DISABLED_", Path.GetFileName(manifest.InstalledDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(library.GetAbsolutePath(manifest)));
    }

    [Fact]
    public void StateChangeRecoversStaleEnabledDirectoryFromDisk()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Velina Stale Path", "10675a0f");
        var disabledPath = library.GetAbsolutePath(manifest);
        var staleActivePath = Path.Combine(_paths.ModsRoot, manifest.Id);
        manifest.InstalledDirectory = manifest.Id;
        library.SaveChanges();

        Assert.False(Directory.Exists(staleActivePath));
        Assert.True(Directory.Exists(disabledPath));

        library.SetEnabled(manifest.Id, true);

        Assert.True(manifest.Enabled);
        Assert.Equal(manifest.Id, manifest.InstalledDirectory);
        Assert.True(Directory.Exists(staleActivePath));
        Assert.False(Directory.Exists(disabledPath));
    }

    [Fact]
    public void LoadingLibraryReconcilesStaleDirectoryWithoutChangingDesiredState()
    {
        var library = NewLibrary();
        var manifest = InstallSimple(library, "Velina Reloaded Path", "10675a0f");
        var actualDirectory = manifest.InstalledDirectory;
        manifest.InstalledDirectory = manifest.Id;
        library.SaveChanges();

        var reloaded = NewLibrary();
        var reconciled = Assert.Single(reloaded.GetAll());

        Assert.False(reconciled.Enabled);
        Assert.Equal(actualDirectory, reconciled.InstalledDirectory);
        Assert.True(Directory.Exists(reloaded.GetAbsolutePath(reconciled)));
    }

    [Fact]
    public void PreparedLaunchAllowsImmediateModAlongsideLoadedVertexLimitModWithoutF10()
    {
        var library = NewLibrary();
        var staticMod = InstallSimple(library, "Velina Static Base", "10675a0f");
        var liveMod = InstallSimple(library, "Alice Live Accessory", "22222222");
        library.SetEnabled(staticMod.Id, true);
        library.SetEnabled(liveMod.Id, true);
        WriteVertexLimitIni(library.GetAbsolutePath(staticMod));
        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput();
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));
        coordinator.PrepareForLaunch();
        input.ProcessId = 42;

        var result = coordinator.ApplyState(liveMod.Id, false, restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Immediate, result.Application);
        var chord = Assert.Single(input.Sent);
        Assert.False(ManagerGameBindings.IsReloadChord(chord));
    }

    [Fact]
    public void RuleFiveEmptyVertexLimitGateIsRemovedIdempotently()
    {
        var manifest = CreateDirectManifest("Velina Rule Five Vertex Limit");
        manifest.LiveSwitchVariable = "zzzmgr_enabled_velina";
        manifest.LiveSwitchSlot = 0;
        manifest.LiveSwitchRuleVersion = "5";
        var qualified = "$\\ZZZModManager\\zzzmgr_enabled_velina\\enabled";
        var path = Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory, "mod.ini");
        File.WriteAllText(path, $"""
            [TextureOverrideVelinaBodyDownVertexLimitRaise]
            hash = 10675a0f
            ; ZZZMOD-LIVE-GUARD-BEGIN {qualified}
            if {qualified}
            override_vertex_count = 55734
            override_byte_stride = 40
            ; ZZZMOD-LIVE-GUARD-END {qualified}
            endif
            """, Encoding.UTF8);
        var service = new LiveModSwitchService(_paths);

        service.Prepare(manifest);
        var firstMigration = File.ReadAllText(path);
        service.Prepare(manifest);
        var secondMigration = File.ReadAllText(path);

        Assert.DoesNotContain("ZZZMOD-LIVE-GUARD", firstMigration, StringComparison.Ordinal);
        Assert.Equal(firstMigration, secondMigration);
        Assert.Equal("10", manifest.LiveSwitchRuleVersion);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(firstMigration, "override_vertex_count = 55734"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(firstMigration, "override_byte_stride = 40"));
    }

    [Fact]
    public void ReloadPhysicallyIsolatesLoadedDisabledDuplicateBeforeF10()
    {
        var library = NewLibrary();
        var active = InstallSimple(library, "Velina Active", "6b25e6d8");
        var duplicate = InstallSimple(library, "Velina Duplicate", "6b25e6d8");
        library.SetEnabled(active.Id, true);
        library.SetEnabled(duplicate.Id, true);
        library.SetEnabled(duplicate.Id, false, keepLoaded: true);
        Assert.False(Path.GetFileName(duplicate.InstalledDirectory).StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase));

        var live = new LiveModSwitchService(_paths);
        live.PrepareAll(library.GetAll());
        library.SaveChanges();
        var input = new FakeGameInput { ProcessId = 42 };
        var coordinator = new GameModStateCoordinator(library, live, input, () => "game.exe", new AppLogger(_paths));

        var result = coordinator.ReloadAndSynchronize(restoreManagerWindow: true);

        Assert.Equal(ModStateApplication.Reloaded, result.Application);
        Assert.StartsWith("DISABLED_", Path.GetFileName(duplicate.InstalledDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(library.GetAbsolutePath(duplicate)));
        Assert.Equal(1, input.Sent.Count(ManagerGameBindings.IsReloadChord));
    }

    [Fact]
    public void ActiveUnmanagedDirectoriesAreQuarantinedWithoutTouchingManagedMods()
    {
        var library = NewLibrary();
        var managed = InstallSimple(library, "Managed Mod", "1234abcd");
        library.SetEnabled(managed.Id, true);
        var managedPath = library.GetAbsolutePath(managed);
        var unmanagedPath = Path.Combine(_paths.ModsRoot, "Raw Source Folder");
        Directory.CreateDirectory(unmanagedPath);
        WriteSimpleIni(unmanagedPath, "deadbeef");

        var changes = library.QuarantineActiveUnmanagedDirectories();

        var change = Assert.Single(changes);
        Assert.Equal("Raw Source Folder", change.OriginalDirectory);
        Assert.True(Directory.Exists(managedPath));
        Assert.False(Directory.Exists(unmanagedPath));
        Assert.True(Directory.Exists(Path.Combine(_paths.ModsRoot, change.QuarantinedDirectory)));
        Assert.StartsWith("DISABLED_UNMANAGED_", change.QuarantinedDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(library.GetAll().Single().Enabled);
    }

    private ModLibrary NewLibrary() => new(_paths, new JsonFileStore(), new ConflictDetector());

    private ModManifest InstallSimple(ModLibrary library, string name, string hash)
    {
        var staged = Path.Combine(_paths.StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        WriteSimpleIni(staged, hash);
        var report = new ImportReport { Status = ImportStatus.Ready, Hashes = new HashSet<string>([hash], StringComparer.OrdinalIgnoreCase) };
        return library.Install(new ImportCandidate { DisplayName = name, StagedPath = staged, SourcePath = staged }, report);
    }

    private ModManifest CreateDirectManifest(string name, string legacyKey = "")
    {
        var id = FileSystemSafety.SanitizeDirectoryName(name).Replace(' ', '_');
        var path = Path.Combine(_paths.ModsRoot, id);
        Directory.CreateDirectory(path);
        WriteSimpleIni(path, id.GetHashCode().ToString("x8"));
        return new ModManifest { Id = id, DisplayName = name, InstalledDirectory = id, LiveSwitchKey = legacyKey };
    }

    private static void WriteSimpleIni(string path, string hash)
    {
        File.WriteAllText(Path.Combine(path, "mod.ini"), $"""
            [TextureOverrideBody]
            hash = {hash}
            handling = skip
            vb0 = ResourceBody

            [ResourceBody]
            filename = body.buf
            """, Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(path, "body.buf"), [1, 2, 3]);
    }

    private static void WriteVertexLimitIni(string path, string hash = "10675a0f", int vertexCount = 55734, int byteStride = 40)
    {
        File.WriteAllText(Path.Combine(path, "mod.ini"), $"""
            [TextureOverrideVelinaBodyDownVertexLimitRaise]
            hash = {hash}
            override_vertex_count = {vertexCount}
            override_byte_stride = {byteStride}
            vb0 = ResourceBody

            [ResourceBody]
            filename = body.buf
            """, Encoding.UTF8);
    }

    private static void WriteVertexLimitIniWithoutCount(string path, string hash = "10675a0f", int byteStride = 40)
    {
        File.WriteAllText(Path.Combine(path, "mod.ini"), $"""
            [TextureOverrideVelinaBodyDownVertexLimitRaise]
            hash = {hash}
            override_byte_stride = {byteStride}
            vb0 = ResourceBody

            [ResourceBody]
            filename = body.buf
            """, Encoding.UTF8);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class FakeGameInput : IGameModReloadService
    {
        public int? ProcessId { get; set; }
        public bool FailNextNonReload { get; set; }
        public List<GameKeyChord> Sent { get; } = [];
        public ModReloadResult Reload(string? gameExecutablePath) => SendKey(gameExecutablePath, ManagerGameBindings.ReloadChord);

        public ModReloadResult SendKey(string? gameExecutablePath, GameKeyChord chord, bool restorePreviousWindow = true)
        {
            Sent.Add(chord);
            if (!ManagerGameBindings.IsReloadChord(chord) && FailNextNonReload)
            {
                FailNextNonReload = false;
                return new ModReloadResult { GameRunning = true, Message = "simulated failure" };
            }

            return new ModReloadResult { GameRunning = true, Succeeded = true, Message = "ok" };
        }

        public bool IsGameRunning(string? gameExecutablePath) => ProcessId is not null;
        public int? GetGameProcessId(string? gameExecutablePath) => ProcessId;
        public ModReloadResult ActivateGame(string? gameExecutablePath) => new() { GameRunning = true, Succeeded = true, Message = "ok" };
    }
}
