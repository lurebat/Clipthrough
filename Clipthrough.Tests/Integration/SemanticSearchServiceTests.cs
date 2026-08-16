using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Integration;

public sealed class SemanticSearchServiceTests
{
    [Fact]
    public async Task QueryAsync_RanksCachedClipsByCosineSimilarity()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        // Capture three clips with known distinct bodies — the fake embedder hashes each text
        // deterministically so each gets its own vector in the cache.
        var phrases = new[] { "red apple fruit", "blue ocean water", "green forest tree" };
        foreach (var phrase in phrases)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = phrase,
                ContentBytes = Encoding.UTF8.GetBytes(phrase),
                SourceApp = "Test",
            });
        }

        // Seed embeddings by running the worker once.
        var embedding = new DeterministicEmbeddingService(dims: 16);
        var indicator = new Clipthrough.Services.BackgroundJobIndicator();
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, indicator);
        worker.Start();
        await WaitForBatchAsync(worker);
        await worker.StopAsync();

        var semantic = new SemanticSearchService(scope.ClipStoreService, embedding);
        await semantic.RefreshCacheAsync();
        Assert.True(semantic.IsReady);
        Assert.Equal(3, semantic.CachedCount);

        // Querying with the same text as one of the stored clips should rank that clip first.
        var ranked = await semantic.QueryAsync("red apple fruit", topK: 3);
        Assert.Equal(3, ranked.Count);
        var appleId = await FindClipIdByText(scope, "red apple fruit");
        Assert.Equal(appleId, ranked[0].ClipId);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyWhenCacheEmpty()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var embedding = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, embedding);
        await semantic.RefreshCacheAsync();

        Assert.True(semantic.IsReady);
        Assert.Equal(0, semantic.CachedCount);

        var hits = await semantic.QueryAsync("anything", topK: 5);
        Assert.Empty(hits);
    }


    // ======== U14: QueryAsync is race-free under concurrent RefreshCacheAsync ========

    [Fact]
    public async Task QueryAsync_ConcurrentRefresh_NoIndexOutOfRange()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        // Seed a mix of clip counts so RefreshCacheAsync keeps changing the snapshot size.
        var phrases = new[] { "cat", "dog", "bird", "fish", "ant" };
        foreach (var p in phrases)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
                IncrementExistingCopyCount = true,
            });
        }
        var emb = new DeterministicEmbeddingService(dims: 8);
        var worker = new EmbeddingWorker(scope.ClipStoreService, emb, new BackgroundJobIndicator());
        worker.Start();
        await WaitForBatchAsync(worker);
        await worker.StopAsync();

        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await semantic.RefreshCacheAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new ConcurrentBag<Exception>();

        // Concurrently run RefreshCacheAsync and QueryAsync — must not throw IndexOutOfRangeException.
        var tasks = new List<Task>();
        for (var i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try { await semantic.RefreshCacheAsync(cts.Token); } catch (OperationCanceledException) { }
                    await Task.Delay(5, default);
                }
            }));
        }
        for (var i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try { await semantic.QueryAsync("cat", topK: 3, cts.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { exceptions.Add(ex); }
                    await Task.Delay(3, default);
                }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }

    // ======== U17: Incremental AppendEmbeddingsAsync — O(M) not O(M*N) ========

    [Fact]
    public async Task AppendEmbeddingsAsync_AppendsNewRecordsWithoutFullReload()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var phrases = new[] { "alpha beta gamma", "delta epsilon", "zeta eta theta" };
        var ids = new List<long>();
        foreach (var p in phrases)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
                IncrementExistingCopyCount = true,
            });
            ids.Add(clip!.Id);
        }

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);

        // Seed the first two clips into the cache via full refresh.
        var firstTwoRecords = new List<ClipEmbeddingRecord>
        {
            new(ids[0], emb.VectorFor(phrases[0])),
            new(ids[1], emb.VectorFor(phrases[1])),
        };
        // Simulate: save to DB and call RefreshCacheAsync to populate the cache.
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(firstTwoRecords, "test");
        await semantic.RefreshCacheAsync();
        Assert.Equal(2, semantic.CachedCount);

        // Now append the third clip's record without a full reload.
        var third = new ClipEmbeddingRecord(ids[2], emb.VectorFor(phrases[2]));
        await semantic.AppendEmbeddingsAsync(new[] { third });

        Assert.Equal(3, semantic.CachedCount);

        // Query confirms the newly appended record is findable.
        var hits = await semantic.QueryAsync(phrases[2], topK: 3);
        Assert.NotEmpty(hits);
        Assert.Equal(ids[2], hits[0].ClipId);
    }

    /// <summary>
    /// Removal has to survive its own hardest case: dropping a middle entry
    /// moves every later vector down a slot, and a slot map left pointing at the
    /// old positions would match one clip's id against another clip's vector -
    /// a wrong answer rather than a missing one. The assertion is therefore on
    /// what each surviving clip still ranks for, not just on the count.
    /// </summary>
    [Fact]
    public async Task RemoveEmbeddingsAsync_DropsTheClipAndKeepsTheRestAddressable()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var phrases = new[] { "alpha beta gamma", "delta epsilon", "zeta eta theta" };
        var ids = new List<long>();
        foreach (var p in phrases)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
                IncrementExistingCopyCount = true,
            });
            ids.Add(clip!.Id);
        }

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(
            [.. ids.Select((id, i) => new ClipEmbeddingRecord(id, emb.VectorFor(phrases[i])))],
            "test");
        await semantic.RefreshCacheAsync();
        Assert.Equal(3, semantic.CachedCount);

        // The middle one, so the last entry has to shift down over it.
        await semantic.RemoveEmbeddingsAsync([ids[1]]);

        Assert.Equal(2, semantic.CachedCount);

        var all = await semantic.QueryAsync(phrases[1], topK: 10);
        Assert.DoesNotContain(ids[1], all.Select(h => h.ClipId));

        // Each survivor must still rank first for its own text. If compaction
        // paired the ids with the wrong vectors, the shifted clip would answer to
        // the wrong phrase and this would fail rather than merely returning fewer
        // hits. Note this proves the published snapshot only — QueryAsync never
        // reads the slot map, so the tests below are what defend that.
        Assert.Equal(ids[0], (await semantic.QueryAsync(phrases[0], topK: 1))[0].ClipId);
        Assert.Equal(ids[2], (await semantic.QueryAsync(phrases[2], topK: 1))[0].ClipId);
    }

    /// <summary>
    /// Removal has to rebuild the id-to-slot map, not just the vectors. The map is
    /// invisible to <c>QueryAsync</c>, which reads the published snapshot, so a
    /// stale map costs nothing until the *next* append — at which point a
    /// re-embedded survivor is written to the index it occupied before compaction,
    /// which now belongs to a different clip. The victim then answers to text it
    /// never contained, and the clip that was actually re-embedded keeps its old
    /// vector. Four clips with the hole in the middle are the minimum that tells
    /// the shifted slot apart from the correct one.
    /// </summary>
    [Fact]
    public async Task RemoveEmbeddingsAsync_ThenReembeddingASurvivor_UpdatesTheRightClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var phrases = new[] { "alpha beta gamma", "delta epsilon", "zeta eta theta", "iota kappa lambda" };
        var ids = new List<long>();
        foreach (var p in phrases)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
                IncrementExistingCopyCount = true,
            });
            ids.Add(clip!.Id);
        }

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(
            [.. ids.Select((id, i) => new ClipEmbeddingRecord(id, emb.VectorFor(phrases[i])))],
            "test");
        await semantic.RefreshCacheAsync();
        Assert.Equal(4, semantic.CachedCount);

        // Drop index 1, so index 2 shifts to 1 and index 3 shifts to 2.
        await semantic.RemoveEmbeddingsAsync([ids[1]]);
        Assert.Equal(3, semantic.CachedCount);

        // Re-embed the clip that moved from slot 2 to slot 1. Against a stale map
        // this lands on slot 2 — the clip that moved from 3 — so "omega" would come
        // back as ids[3].
        await semantic.AppendEmbeddingsAsync([new ClipEmbeddingRecord(ids[2], emb.VectorFor("omega"))]);

        Assert.Equal(3, semantic.CachedCount);
        var omega = await semantic.QueryAsync("omega", topK: 1);
        Assert.NotEmpty(omega);
        Assert.Equal(ids[2], omega[0].ClipId);
        Assert.True(omega[0].Score > 0.9f, $"expected near-1.0 cosine, got {omega[0].Score}");

        // And the innocent neighbour still answers to its own text.
        Assert.Equal(ids[3], (await semantic.QueryAsync(phrases[3], topK: 1))[0].ClipId);
    }

    /// <summary>
    /// The same id arriving twice must be a no-op. It is reachable in practice —
    /// a retention sweep and an explicit delete can both name a clip — and if the
    /// slot map still holds the first removal's entry, the second call counts a
    /// clip that is no longer in the cache, under-sizes the survivor arrays and
    /// throws while compacting.
    /// </summary>
    [Fact]
    public async Task RemoveEmbeddingsAsync_SameIdTwice_IsANoOp()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var phrases = new[] { "alpha beta gamma", "delta epsilon", "zeta eta theta" };
        var ids = new List<long>();
        foreach (var p in phrases)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
                IncrementExistingCopyCount = true,
            });
            ids.Add(clip!.Id);
        }

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(
            [.. ids.Select((id, i) => new ClipEmbeddingRecord(id, emb.VectorFor(phrases[i])))],
            "test");
        await semantic.RefreshCacheAsync();

        await semantic.RemoveEmbeddingsAsync([ids[0]]);
        await semantic.RemoveEmbeddingsAsync([ids[0]]);

        Assert.Equal(2, semantic.CachedCount);
        Assert.Equal(ids[1], (await semantic.QueryAsync(phrases[1], topK: 1))[0].ClipId);
        Assert.Equal(ids[2], (await semantic.QueryAsync(phrases[2], topK: 1))[0].ClipId);
    }

    [Fact]
    public async Task RemoveEmbeddingsAsync_UnknownId_LeavesTheCacheAlone()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "alpha beta gamma",
            ContentBytes = Encoding.UTF8.GetBytes("alpha beta gamma"),
        });

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(clip!.Id, emb.VectorFor("alpha beta gamma"))], "test");
        await semantic.RefreshCacheAsync();

        await semantic.RemoveEmbeddingsAsync([clip.Id + 9999]);

        Assert.Equal(1, semantic.CachedCount);
        Assert.Equal(clip.Id, (await semantic.QueryAsync("alpha beta gamma", topK: 1))[0].ClipId);
    }

    /// <summary>
    /// Emptying the cache must leave it usable rather than in a state a later
    /// append or query trips over.
    /// </summary>
    [Fact]
    public async Task RemoveEmbeddingsAsync_RemovingEverything_LeavesAWorkingEmptyCache()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "alpha beta gamma",
            ContentBytes = Encoding.UTF8.GetBytes("alpha beta gamma"),
        });

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(clip!.Id, emb.VectorFor("alpha beta gamma"))], "test");
        await semantic.RefreshCacheAsync();

        await semantic.RemoveEmbeddingsAsync([clip.Id]);

        Assert.Equal(0, semantic.CachedCount);
        Assert.Empty(await semantic.QueryAsync("alpha beta gamma", topK: 5));
    }

    [Fact]
    public async Task AppendEmbeddingsAsync_DuplicateId_NotAddedTwice()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "dedupe test",
            ContentBytes = Encoding.UTF8.GetBytes("dedupe test"),
            IncrementExistingCopyCount = true,
        });
        Assert.NotNull(clip);

        var emb = new DeterministicEmbeddingService(dims: 4);
        var record = new ClipEmbeddingRecord(clip!.Id, emb.VectorFor("dedupe test"));
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(new[] { record }, "test");

        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        await semantic.RefreshCacheAsync();
        Assert.Equal(1, semantic.CachedCount);

        // Append the same record again — should not increase count.
        await semantic.AppendEmbeddingsAsync(new[] { record });
        Assert.Equal(1, semantic.CachedCount);
    }

    [Fact]
    public async Task AppendEmbeddingsAsync_EmptyCacheFallsBackToFullRefresh()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "fresh start",
            ContentBytes = Encoding.UTF8.GetBytes("fresh start"),
            IncrementExistingCopyCount = true,
        });
        Assert.NotNull(clip);

        var emb = new DeterministicEmbeddingService(dims: 4);
        var record = new ClipEmbeddingRecord(clip!.Id, emb.VectorFor("fresh start"));
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(new[] { record }, "test");

        // SemanticSearchService with empty cache: AppendEmbeddingsAsync should fall back to full refresh.
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        Assert.Equal(0, semantic.CachedCount);

        await semantic.AppendEmbeddingsAsync(new[] { record });
        Assert.Equal(1, semantic.CachedCount);
    }

    // U17 / bug #3: a clip that gets re-embedded (e.g. after OCR adds text) must
    // have its cached vector REPLACED, not silently skipped as a duplicate id —
    // otherwise semantic search keeps scoring against the stale vector forever.
    [Fact]
    public async Task AppendEmbeddingsAsync_ReembeddedClip_ReplacesStaleVector()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        var apple = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "apple",
            ContentBytes = Encoding.UTF8.GetBytes("apple"),
            IncrementExistingCopyCount = true,
        });
        var banana = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "banana",
            ContentBytes = Encoding.UTF8.GetBytes("banana"),
            IncrementExistingCopyCount = true,
        });

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);

        await scope.ClipStoreService.SaveEmbeddingBatchAsync(new[]
        {
            new ClipEmbeddingRecord(apple!.Id, emb.VectorFor("apple")),
            new ClipEmbeddingRecord(banana!.Id, emb.VectorFor("banana")),
        }, "test");
        await semantic.RefreshCacheAsync();
        Assert.Equal(2, semantic.CachedCount);

        // Re-embed the "apple" clip with the vector for a different concept.
        // The id is already cached, so the old code would skip it (stale vector).
        await semantic.AppendEmbeddingsAsync(new[]
        {
            new ClipEmbeddingRecord(apple.Id, emb.VectorFor("cherry")),
        });

        // Count is unchanged (update in place, not an add).
        Assert.Equal(2, semantic.CachedCount);

        // The apple clip now matches "cherry" best — proving its vector was replaced.
        var hits = await semantic.QueryAsync("cherry", topK: 2);
        Assert.NotEmpty(hits);
        Assert.Equal(apple.Id, hits[0].ClipId);
        Assert.True(hits[0].Score > 0.9f, $"expected near-1.0 cosine, got {hits[0].Score}");
    }

    // ======== C8: a backfill must not copy the whole cache once per batch ========

    /// <summary>
    /// The embedding worker appends in batches of 32, so a 100k backfill calls this
    /// method ~3,000 times. Rebuilding the id map and reallocating the vector array on
    /// every one of those calls is quadratic: the review measured 223 GiB of memcpy and
    /// ~1,000 gen-2 collections for a single backfill.
    ///
    /// Allocated bytes is the observable that actually distinguishes the two shapes.
    /// Wall-clock would too, but it would turn a real regression into a flaky test on a
    /// loaded CI box, and count/query assertions pass just as happily against the
    /// quadratic version.
    /// </summary>
    [Fact]
    public async Task AppendEmbeddingsAsync_DoesNotReallocateTheCachePerBatch()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        const int dims = 128;
        var emb = new DeterministicEmbeddingService(dims);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);

        // One real clip only, to establish the dimension. Appends never touch the store,
        // so the rest of the cache can be synthetic and the test stays fast.
        var seed = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "seed",
            ContentBytes = Encoding.UTF8.GetBytes("seed"),
            IncrementExistingCopyCount = true,
        });
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(
            new[] { new ClipEmbeddingRecord(seed!.Id, emb.VectorFor("seed")) }, "test");
        await semantic.RefreshCacheAsync();

        // Grow to a realistic library size. Cost here is not measured.
        const int cached = 20_000;
        var nextId = 1_000_000L;
        for (var i = 0; i < cached; i += 32)
        {
            var batch = new List<ClipEmbeddingRecord>(32);
            for (var j = 0; j < 32; j++)
            {
                var id = nextId++;
                batch.Add(new ClipEmbeddingRecord(id, emb.VectorFor($"seeded-{id}")));
            }
            await semantic.AppendEmbeddingsAsync(batch);
        }
        Assert.Equal(cached + 1, semantic.CachedCount);

        // Measure only the steady-state appends against that warm cache.
        const int measuredBatches = 100;
        var lastMeasuredId = 0L;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < measuredBatches; i++)
        {
            var batch = new List<ClipEmbeddingRecord>(32);
            for (var j = 0; j < 32; j++)
            {
                var id = nextId++;
                lastMeasuredId = id;
                batch.Add(new ClipEmbeddingRecord(id, emb.VectorFor($"measured-{id}")));
            }
            await semantic.AppendEmbeddingsAsync(batch);
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        // Copying the cache every batch costs ~20,000 x 128 x 4 = 10 MB per call, so the
        // quadratic shape allocates about a gigabyte here. Rebuilding the id map alone
        // costs a further ~1 MB per call. The budget sits far below either.
        const long budget = 64L * 1024 * 1024;
        Assert.True(
            allocated < budget,
            $"{measuredBatches} appends to a {cached}-entry cache allocated " +
            $"{allocated / (1024 * 1024)} MB, over the {budget / (1024 * 1024)} MB budget. " +
            "The cache is being reallocated or the id map rebuilt per batch.");

        // Anti-vacuity: the appends really happened, and the cache is still coherent.
        Assert.Equal(cached + 1 + (measuredBatches * 32), semantic.CachedCount);
        var hits = await semantic.QueryAsync($"measured-{lastMeasuredId}", topK: 1);
        Assert.Equal(lastMeasuredId, hits[0].ClipId);
    }

    /// <summary>
    /// Scoring is the whole cost of a semantic query - one pass over every cached embedding,
    /// on every throttled keystroke - so it is worth widening to SIMD and worth keeping only
    /// the best few rather than sorting them all. Both of those are easy to get subtly wrong,
    /// so this checks the ranking against a scalar reference computed from the same vectors.
    ///
    /// The dimension counts straddle the SIMD width: 8 matches it exactly on a 256-bit machine,
    /// while 13 always leaves a remainder that only the scalar tail can handle - it is not a
    /// multiple of 4, 8 or 16, so the tail runs whatever width the host actually has.
    /// </summary>
    [Theory]
    [InlineData(8, 5)]
    [InlineData(13, 5)]
    [InlineData(13, 40)]
    public async Task QueryAsync_ReturnsTheHighestScoringClipsInOrder(int dims, int topK)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        const int clipCount = 30;
        for (var i = 0; i < clipCount; i++)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"phrase number {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"phrase number {i}"),
                SourceApp = "Test",
            });
        }

        var embedding = new DeterministicEmbeddingService(dims);
        var indicator = new Clipthrough.Services.BackgroundJobIndicator();
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, indicator);
        worker.Start();
        await WaitForBatchAsync(worker);
        await worker.StopAsync();

        var semantic = new SemanticSearchService(scope.ClipStoreService, embedding);
        await semantic.RefreshCacheAsync();
        Assert.Equal(clipCount, semantic.CachedCount);

        const string queryText = "phrase number 7";
        var ranked = await semantic.QueryAsync(queryText, topK);

        // Independent oracle: score every clip with a plain scalar loop over the same vectors.
        var stored = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = 100 });
        var queryVector = embedding.VectorFor(queryText);
        var expected = stored.Items
            .Select(clip =>
            {
                var vector = embedding.VectorFor(clip.Content!);
                var score = 0f;
                for (var i = 0; i < dims; i++)
                {
                    score += vector[i] * queryVector[i];
                }

                return (clip.Id, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToArray();

        Assert.Equal(Math.Min(topK, clipCount), ranked.Count);
        Assert.Equal(expected.Select(x => x.Id), ranked.Select(x => x.ClipId));

        for (var i = 0; i < ranked.Count; i++)
        {
            Assert.Equal(expected[i].Score, ranked[i].Score, 4);
        }

        // Ranking is only meaningful if the scores actually differ; an all-equal corpus would
        // let any selection pass.
        Assert.True(
            ranked[0].Score - ranked[^1].Score > 0.01f,
            $"Scores were too close to distinguish a ranking: {ranked[0].Score} vs {ranked[^1].Score}.");
    }

    /// <summary>
    /// Vectors shorter than one SIMD register skip the widened loop entirely, so the scalar
    /// tail has to carry the whole dot product. Three dimensions only admit eight distinct
    /// sign patterns, which makes exact score ties likely - hence a three-clip corpus and
    /// assertions that tolerate ties.
    /// </summary>
    [Fact]
    public async Task QueryAsync_ReturnsEverythingWhenTopKExceedsTheCache()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });

        foreach (var phrase in new[] { "alpha", "beta", "gamma" })
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = phrase,
                ContentBytes = Encoding.UTF8.GetBytes(phrase),
                SourceApp = "Test",
            });
        }

        const int dims = 3;
        var embedding = new DeterministicEmbeddingService(dims);
        var indicator = new Clipthrough.Services.BackgroundJobIndicator();
        var worker = new EmbeddingWorker(scope.ClipStoreService, embedding, indicator);
        worker.Start();
        await WaitForBatchAsync(worker);
        await worker.StopAsync();

        var semantic = new SemanticSearchService(scope.ClipStoreService, embedding);
        await semantic.RefreshCacheAsync();

        const string queryText = "alpha";
        var ranked = await semantic.QueryAsync(queryText, topK: 50);

        // The scalar tail is the only code path here, so check the values too, not just order.
        var queryVector = embedding.VectorFor(queryText);
        foreach (var result in ranked)
        {
            var clip = await scope.ClipStoreService.GetByIdAsync(result.ClipId);
            var vector = embedding.VectorFor(clip!.Content!);
            var expected = 0f;
            for (var i = 0; i < dims; i++)
            {
                expected += vector[i] * queryVector[i];
            }

            Assert.Equal(expected, result.Score, 4);
        }

        Assert.Equal(3, ranked.Count);
        Assert.Equal(3, ranked.Select(x => x.ClipId).Distinct().Count());
        Assert.True(ranked[0].Score >= ranked[1].Score && ranked[1].Score >= ranked[2].Score);
    }

    private static async Task<long> FindClipIdByText(TemporaryDatabaseScope scope, string text)
    {
        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = 50 });
        return result.Items.First(c => c.Content == text).Id;
    }

    private static async Task WaitForBatchAsync(EmbeddingWorker worker)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = worker.BatchCompleted.Subscribe(n => tcs.TrySetResult(n));
        worker.Poke();
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private sealed class DeterministicEmbeddingService : IEmbeddingService
    {
        private readonly int _dims;
        public DeterministicEmbeddingService(int dims) { _dims = dims; }
        public int Dimensions => _dims;
        public bool IsReady => true;

        public Task<float[]> EmbedAsync(string text, System.Threading.CancellationToken ct = default)
            => Task.FromResult(Vector(text));

        public Task<System.Collections.Generic.IReadOnlyList<float[]>> EmbedBatchAsync(
            System.Collections.Generic.IReadOnlyList<string> texts,
            System.Threading.CancellationToken ct = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<float[]>>(texts.Select(Vector).ToArray());

        private float[] Vector(string s)
        {
            var vec = new float[_dims];
            var hash = s?.GetHashCode() ?? 0;
            double sumSq = 0;
            for (var i = 0; i < _dims; i++)
            {
                vec[i] = ((hash >> (i % 32)) & 1) == 1 ? 1f : -1f;
                // Perturb to break ties against the query text.
                vec[i] += 0.01f * ((hash >> i) & 0xFF) / 255f;
                sumSq += vec[i] * vec[i];
            }
            var norm = (float)Math.Sqrt(sumSq);
            if (norm > 0) for (var i = 0; i < _dims; i++) vec[i] /= norm;
            return vec;
        }

        /// <summary>Expose the same deterministic vector for test assertions without async overhead.</summary>
        public float[] VectorFor(string text) => Vector(text);
    }
}
