using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 7 checkpoint — the watch loop's pure pieces: the relevance filter and the
/// debounced coalescing batcher. No file system watchers, no MSBuild, no database.
/// Timing assertions use generous margins to stay robust on busy CI machines.
/// </summary>
public sealed class ChangeBatcherTests
{
    // ------------------------------------------------------------ relevance filter --

    [Theory]
    [InlineData(@"C:\repo\src\Thing.cs", true)]
    [InlineData(@"C:\repo\Components\Pages\Home.razor", true)]
    [InlineData(@"C:\repo\src\notes.txt", false)]
    [InlineData(@"C:\repo\src\obj\Debug\net10.0\Gen.cs", false)]
    [InlineData(@"C:\repo\src\bin\Debug\net10.0\Copy.cs", false)]
    [InlineData(@"C:\repo\.git\objects\aa\Thing.cs", false)]
    [InlineData(@"C:\repo\obj\x.razor", false)]
    [InlineData(@"C:\repo\Objects\Thing.cs", true)] // 'Objects' is not the 'obj' segment
    [InlineData("", false)]
    public void Relevance_filter_accepts_authored_source_only(string path, bool expected)
        => Assert.Equal(expected, WatchLoop.IsRelevantSourceFile(path));

    // ----------------------------------------------------------------- coalescing --

    [Fact]
    public async Task Batch_fires_after_quiet_window_with_distinct_coalesced_paths()
    {
        var batcher = new ChangeBatcher(TimeSpan.FromMilliseconds(100), WatchLoop.IsRelevantSourceFile);

        batcher.Post(@"C:\repo\A.cs");
        batcher.Post(@"C:\repo\B.cs");
        batcher.Post(@"C:\repo\A.cs"); // duplicate — coalesced
        batcher.Post(@"C:\repo\skip.txt"); // irrelevant — dropped

        var batch = await batcher.WaitForBatchAsync(TestToken());

        Assert.False(batch.Overflow);
        Assert.Equal(2, batch.Paths.Count);
        Assert.Contains(@"C:\repo\A.cs", batch.Paths);
        Assert.Contains(@"C:\repo\B.cs", batch.Paths);
    }

    [Fact]
    public async Task Batch_waits_for_the_debounce_quiet_window()
    {
        var debounce = TimeSpan.FromMilliseconds(250);
        var batcher = new ChangeBatcher(debounce, WatchLoop.IsRelevantSourceFile);

        var start = DateTime.UtcNow;
        batcher.Post(@"C:\repo\A.cs");
        var batch = await batcher.WaitForBatchAsync(TestToken());
        var elapsed = DateTime.UtcNow - start;

        Assert.Single(batch.Paths);
        // The batch must not fire before the quiet window has passed (small scheduling
        // slack allowed on the lower bound).
        Assert.True(elapsed >= debounce - TimeSpan.FromMilliseconds(30),
            $"Batch fired after {elapsed.TotalMilliseconds:F0} ms — before the {debounce.TotalMilliseconds:F0} ms quiet window.");
    }

    [Fact]
    public async Task Events_during_a_processing_gap_accumulate_into_the_next_batch()
    {
        var batcher = new ChangeBatcher(TimeSpan.FromMilliseconds(50), WatchLoop.IsRelevantSourceFile);

        batcher.Post(@"C:\repo\A.cs");
        var first = await batcher.WaitForBatchAsync(TestToken());
        Assert.Single(first.Paths);

        // "While the cycle ran", more events arrived — the next wait picks them up.
        batcher.Post(@"C:\repo\B.cs");
        batcher.Post(@"C:\repo\C.cs");
        var second = await batcher.WaitForBatchAsync(TestToken());
        Assert.Equal(2, second.Paths.Count);
        Assert.DoesNotContain(@"C:\repo\A.cs", second.Paths);
    }

    [Fact]
    public async Task Overflow_fires_a_marked_batch_even_without_paths()
    {
        var batcher = new ChangeBatcher(TimeSpan.FromMilliseconds(50), WatchLoop.IsRelevantSourceFile);

        batcher.PostOverflow();
        var batch = await batcher.WaitForBatchAsync(TestToken());

        Assert.True(batch.Overflow);
        Assert.Empty(batch.Paths);
    }

    [Fact]
    public async Task PostAlways_bypasses_the_relevance_filter()
    {
        var batcher = new ChangeBatcher(TimeSpan.FromMilliseconds(50), WatchLoop.IsRelevantSourceFile);

        batcher.PostAlways(@"C:\repo\.git\HEAD"); // fails the filter, must still fire
        var batch = await batcher.WaitForBatchAsync(TestToken());

        Assert.Single(batch.Paths);
    }

    [Fact]
    public async Task Cancellation_stops_the_wait()
    {
        var batcher = new ChangeBatcher(TimeSpan.FromMilliseconds(50), WatchLoop.IsRelevantSourceFile);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batcher.WaitForBatchAsync(cts.Token));
    }

    /// <summary>A safety timeout so a regression can never hang the test run.</summary>
    private static CancellationToken TestToken() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
}
