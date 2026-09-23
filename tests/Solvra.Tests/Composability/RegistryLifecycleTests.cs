#nullable enable

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Solvra.Hooks;
using Solvra.Security;
using Solvra.Skills;
using Solvra.Tools;
using Xunit.Abstractions;

namespace Solvra.Tests.Composability;

public sealed class RegistryLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public void ToolRegistrationDisposeRestoresPriorState()
    {
        var registry = new ToolRegistry();
        var before = registry.GetAllTools().ToArray();

        var registration = registry.RegisterTool(new StubTool("temporary"));
        Assert.NotNull(registry.GetTool("temporary"));

        registration.Dispose();
        registration.Dispose();
        Assert.Equal(before, registry.GetAllTools());
    }

    [Fact]
    public void ToolDuplicatePolicyRequiresForceAndStaleHandleCannotRemoveReplacement()
    {
        var registry = new ToolRegistry();
        var first = new StubTool("same");
        var second = new StubTool("same");
        var firstHandle = registry.RegisterTool(first);

        Assert.Throws<InvalidOperationException>(() => registry.RegisterTool(second));
        var secondHandle = registry.RegisterTool(second, force: true);
        firstHandle.Dispose();
        Assert.Same(second, registry.GetTool("same"));

        secondHandle.Dispose();
        Assert.Null(registry.GetTool("same"));
    }

    [Fact]
    public void ToolUnregisterRemovesCurrentOwner()
    {
        var registry = new ToolRegistry();
        registry.RegisterTool(new StubTool("temporary"));
        Assert.True(registry.Unregister("temporary"));
        Assert.False(registry.Unregister("temporary"));
    }

    [Fact]
    public void HookHandlesRemoveOnlyTheirOwnRegistration()
    {
        var engine = new HookEngine();
        var first = new StubHook("same");
        var second = new StubHook("same");
        var firstHandle = engine.Register(first);
        var secondHandle = engine.Register(second);

        Assert.Equal(new IHook[] { first, second }, engine.GetHooks());
        firstHandle.Dispose();
        firstHandle.Dispose();
        Assert.Equal(new IHook[] { second }, engine.GetHooks());
        secondHandle.Dispose();
        Assert.Empty(engine.GetHooks());
    }

    [Fact]
    public void HookHandleUsesIdentityRatherThanCustomEquality()
    {
        var engine = new HookEngine();
        var first = new EqualHook("same");
        var second = new EqualHook("same");
        var firstHandle = engine.Register(first);
        engine.Register(second);

        firstHandle.Dispose();

        Assert.Same(second, Assert.Single(engine.GetHooks()));
    }

    [Fact]
    public void HookIdUnregisterRetainsExistingRemoveAllPolicy()
    {
        var engine = new HookEngine();
        engine.Register(new StubHook("same"));
        engine.Register(new StubHook("same"));
        engine.Register(new StubHook("other"));

        engine.Unregister("same");
        Assert.Equal("other", Assert.Single(engine.GetHooks()).Id);
    }

    [Fact]
    public void RegistrationDisposeProbeDoesNotGrowRegistry()
    {
        const int iterations = 10_000;
        var registry = new ToolRegistry();
        var hooks = new HookEngine();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            using var tool = registry.RegisterTool(new StubTool("probe"));
            using var hook = hooks.Register(new StubHook("probe"));
        }
        timer.Stop();
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

        Assert.Empty(registry.GetAllTools());
        Assert.Empty(hooks.GetHooks());
        output.WriteLine(
            "component lifecycle: {0} tool+hook register/dispose pairs in {1:F2} ms; retained memory delta {2} bytes",
            iterations, timer.Elapsed.TotalMilliseconds, memoryAfter - memoryBefore);
    }

    [Fact]
    public async Task SkillAddChangeDeleteProbeNeedsNoProcessRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skill_probe_{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "probe");
        Directory.CreateDirectory(directory);
        try
        {
            var loader = new SkillLoader(root);
            var timer = Stopwatch.StartNew();
            await WriteSkill(directory, "one");
            var added = await loader.ReloadAsync();
            var addMs = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            await WriteSkill(directory, "two");
            var changed = await loader.ReloadAsync();
            var changeMs = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            Directory.Delete(directory, recursive: true);
            var removed = await loader.ReloadAsync();
            var deleteMs = timer.Elapsed.TotalMilliseconds;

            Assert.Equal(new[] { "probe" }, added.Added);
            Assert.Equal(new[] { "probe" }, changed.Changed);
            Assert.Equal(new[] { "probe" }, removed.Removed);
            Assert.Empty(await loader.GetAllSkillsAsync());
            output.WriteLine(
                "skill lifecycle: add {0:F2} ms, change {1:F2} ms, delete {2:F2} ms; final skills 0",
                addMs, changeMs, deleteMs);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CollectibleAssemblyLoadContextCanReleaseAnIsolatedAssembly()
    {
        var reference = LoadThenReleaseAssembly();
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadThenReleaseAssembly()
    {
        var context = new AssemblyLoadContext("solvra-composability-probe", isCollectible: true);
        using var stream = File.OpenRead(typeof(SkillLoader).Assembly.Location);
        var assembly = context.LoadFromStream(stream);
        Assert.Equal("Solvra", assembly.GetName().Name);
        var reference = new WeakReference(context, trackResurrection: false);
        context.Unload();
        return reference;
    }

    private static Task WriteSkill(string directory, string content) =>
        File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), $"""
            ---
            name: probe
            description: measurement
            trigger_patterns: ["probe"]
            ---
            {content}
            """);

    private sealed class StubTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "test";
        public PermissionLevel PermissionLevel => PermissionLevel.Read;
        public JsonElement GetInputSchema() => JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        public Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context,
            CancellationToken ct = default) => Task.FromResult(new ToolExecuteResult("ok", false));
    }

    private sealed class StubHook(string id) : IHook
    {
        public string Id { get; } = id;
        public HookEvent Event => HookEvent.PreToolUse;
        public string[]? ToolFilter => null;
        public Task<HookResult> ExecuteAsync(HookContext context) =>
            Task.FromResult(new HookResult(HookAction.Allow));
    }

    private sealed class EqualHook(string id) : IHook
    {
        public string Id { get; } = id;
        public HookEvent Event => HookEvent.PreToolUse;
        public string[]? ToolFilter => null;
        public Task<HookResult> ExecuteAsync(HookContext context) =>
            Task.FromResult(new HookResult(HookAction.Allow));
        public override bool Equals(object? obj) => obj is EqualHook;
        public override int GetHashCode() => 0;
    }
}
