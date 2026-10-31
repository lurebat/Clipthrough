using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-27. Removing one clip from the semantic cache copied every surviving
/// vector into a fresh array: 25 ms and 90 MB on the large-object heap per
/// removal at 60k x 384, and with the library at its cap every capture evicts
/// one. Removal now tombstones the id and keeps sharing the vectors.
/// </summary>
public sealed partial class SemanticSearchServiceTests
{
    private static async Task<(TemporaryDatabaseScope Scope, SemanticSearchService Semantic, DeterministicEmbeddingService Emb, List<long> Ids, string[] Phrases)> CacheOfAsync(params string[] phrases)
    {
        var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 8192 });
        var ids = new List<long>();
        foreach (var p in phrases)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = p,
                ContentBytes = Encoding.UTF8.GetBytes(p),
            });
            ids.Add(clip!.Id);
        }

        var emb = new DeterministicEmbeddingService(dims: 8);
        var semantic = new SemanticSearchService(scope.ClipStoreService, emb);
        // Claim first: a save lands only on a clip the worker claimed (U-B5, F-17).
        await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(ids.Count);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(
            [.. ids.Select((id, i) => new ClipEmbeddingRecord(id, emb.VectorFor(phrases[i])))],
            "test");
        await semantic.RefreshCacheAsync();
        return (scope, semantic, emb, ids, phrases);
    }

    [Fact]
    public async Task RemovingAClip_KeepsTheSameVectorArray()
    {
        var (scope, semantic, _, ids, _) = await CacheOfAsync("alpha beta gamma", "delta epsilon", "zeta eta theta");
        using (scope)
        {
            var before = semantic.CachedVectors;

            await semantic.RemoveEmbeddingsAsync([ids[1]]);

            Assert.Same(before, semantic.CachedVectors);
            Assert.Equal(2, semantic.CachedCount);

            // The removed clip's vector is still in the shared array; a query must skip it.
            var hits = await semantic.QueryAsync("delta epsilon", topK: 10);
            Assert.Equal(new[] { ids[0], ids[2] }.Order(), hits.Select(h => h.ClipId).Order());
        }
    }

    [Fact]
    public async Task ARemovedClip_NeverComesBack_ThroughAppendsOrACompactingCopy()
    {
        var (scope, semantic, emb, ids, phrases) = await CacheOfAsync("alpha beta gamma", "delta epsilon", "zeta eta theta", "iota kappa");
        using (scope)
        {
            await semantic.RemoveEmbeddingsAsync([ids[1]]);

            // More clips than the spare capacity: the first fit in place, the rest
            // force the copy that drops tombstones.
            var extra = new List<(long Id, string Phrase)>();
            var toAdd = semantic.CachedCapacity + 4;
            for (var i = 0; i < toAdd; i++)
            {
                var phrase = $"extra clip number {i} lambda";
                var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
                {
                    ContentType = ContentType.Text,
                    ContentFormat = ClipContentFormat.PlainText,
                    ContentText = phrase,
                    ContentBytes = Encoding.UTF8.GetBytes(phrase),
                });
                extra.Add((clip!.Id, phrase));
                await semantic.AppendEmbeddingsAsync([new ClipEmbeddingRecord(clip.Id, emb.VectorFor(phrase))]);
            }

            var all = await semantic.QueryAsync(phrases[1], topK: 1000);
            Assert.DoesNotContain(ids[1], all.Select(h => h.ClipId));
            Assert.Equal(3 + extra.Count, semantic.CachedCount);
            Assert.Equal(3 + extra.Count, all.Count);

            foreach (var i in new[] { 0, 2, 3 })
            {
                Assert.Equal(ids[i], (await semantic.QueryAsync(phrases[i], topK: 1))[0].ClipId);
            }

            Assert.Equal(extra[^1].Id, (await semantic.QueryAsync(extra[^1].Phrase, topK: 1))[0].ClipId);
        }
    }

    [Fact]
    public async Task ReembeddingARemovedClip_BringsBackOnlyItsNewVector()
    {
        var (scope, semantic, emb, ids, _) = await CacheOfAsync("alpha beta gamma", "delta epsilon", "zeta eta theta");
        using (scope)
        {
            await semantic.RemoveEmbeddingsAsync([ids[1]]);
            await semantic.AppendEmbeddingsAsync([new ClipEmbeddingRecord(ids[1], emb.VectorFor("omega rho sigma"))]);

            var hits = await semantic.QueryAsync("omega rho sigma", topK: 10);
            Assert.Single(hits, h => h.ClipId == ids[1]);
            Assert.Equal(ids[1], hits[0].ClipId);
            Assert.Equal(3, semantic.CachedCount);
        }
    }
}