using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// A clip that becomes sensitive must not keep - or later gain - an embedding
/// vector derived from its content (F-17). Eligibility is checked when the
/// embedding worker claims a clip; these cover what happens after the claim.
/// </summary>
public sealed class EmbeddingSensitivityTests
{
    private const string SensitiveOcrText = "password = NOT-A-REAL-CREDENTIAL";

    private static float[] Vector() => Enumerable.Repeat(0.5f, 8).ToArray();

    private static async Task<(long IsSensitive, string Status, long Vectors)> ReadStateAsync(TemporaryDatabaseScope scope, long clipId)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.is_sensitive, c.embedding_status, (SELECT COUNT(*) FROM clip_embeddings e WHERE e.clip_id = c.id) " +
            "FROM clips c WHERE c.id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.IsDBNull(1) ? "NULL" : reader.GetString(1), reader.GetInt64(2));
    }

    private static async Task<ClipEntry> CaptureTextAsync(TemporaryDatabaseScope scope, string text)
        => (await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
        }))!;

    /// <summary>
    /// Claim, then the user marks the clip sensitive while inference runs,
    /// then the worker saves. Before the fix the vector computed from the now
    /// sensitive text was stored and the status read 'succeeded'.
    /// </summary>
    [Fact]
    public async Task MarkedSensitiveWhileClaimed_SaveStoresNoVectorAndLeavesItExcluded()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureTextAsync(scope, "the quarterly launch plan draft");

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(8);
        Assert.Contains(claimed, c => c.ClipId == clip.Id);

        await scope.ClipStoreService.SetSensitiveAsync(clip.Id, true);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(clip.Id, Vector())], "test-model");

        var (isSensitive, status, vectors) = await ReadStateAsync(scope, clip.Id);
        Assert.Equal(1, isSensitive);
        Assert.Equal("excluded", status);
        Assert.Equal(0, vectors);
    }

    /// <summary>
    /// The ordinary path still saves: claimed, still eligible, stored.
    /// </summary>
    [Fact]
    public async Task ClaimedAndStillEligible_SaveStoresTheVector()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureTextAsync(scope, "the quarterly launch plan draft");

        await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(8);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(clip.Id, Vector())], "test-model");

        var (_, status, vectors) = await ReadStateAsync(scope, clip.Id);
        Assert.Equal("succeeded", status);
        Assert.Equal(1, vectors);
    }

    /// <summary>
    /// The save checks sensitivity itself rather than trusting every path that
    /// raises the flag to also set 'excluded'. The batch-capture dedupe update
    /// in ClipStoreService, for one, raises is_sensitive without touching the
    /// embedding status; this sets the flag the same way.
    /// </summary>
    [Fact]
    public async Task ClaimedClipFlaggedSensitiveWithoutExclusion_SaveStoresNoVector()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureTextAsync(scope, "the quarterly launch plan draft");
        await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(8);

        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var flag = connection.CreateCommand();
            flag.CommandText = "UPDATE clips SET is_sensitive = 1 WHERE id = $id;";
            flag.Parameters.AddWithValue("$id", clip.Id);
            await flag.ExecuteNonQueryAsync();
        }

        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(clip.Id, Vector())], "test-model");

        var (_, status, vectors) = await ReadStateAsync(scope, clip.Id);
        Assert.Equal(0, vectors);
        Assert.NotEqual("succeeded", status);
    }

    /// <summary>
    /// A vector computed for a claim that has since been released belongs to
    /// a batch nobody is waiting for; it must not land and must not mark the
    /// clip 'succeeded' over the state the release put it in.
    /// </summary>
    [Fact]
    public async Task SaveAfterTheClaimWasReleased_StoresNoVector()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureTextAsync(scope, "the quarterly launch plan draft");
        await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(8);
        await scope.ClipStoreService.ReleaseEmbeddingClaimsAsync([clip.Id]);

        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(clip.Id, Vector())], "test-model");

        var (_, status, vectors) = await ReadStateAsync(scope, clip.Id);
        Assert.Equal(0, vectors);
        Assert.Equal("pending", status);
    }

    /// <summary>
    /// An image is embedded from its OCR text. A later OCR pass that finds a
    /// secret marks the clip sensitive; it must also purge the vector and
    /// exclude the clip, the same as marking it sensitive by hand does.
    /// </summary>
    [Fact]
    public async Task OcrResultThatMarksAnEmbeddedImageSensitive_PurgesItsVector()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var png = System.Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");
        var image = (await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = png,
            ImageWidth = 1,
            ImageHeight = 1,
        }))!;

        Assert.True(await scope.ClipStoreService.TryClaimForOcrAsync(image.Id));
        Assert.True(await scope.ClipStoreService.SetOcrResultAsync(image.Id, "meeting notes for monday"));
        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(8);
        Assert.Contains(claimed, c => c.ClipId == image.Id);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(image.Id, Vector())], "test-model");
        Assert.Equal(1, (await ReadStateAsync(scope, image.Id)).Vectors);

        Assert.True(await scope.ClipStoreService.MarkOcrForRerunAsync(image.Id));
        Assert.True(await scope.ClipStoreService.TryClaimForOcrAsync(image.Id));
        Assert.True(await scope.ClipStoreService.SetOcrResultAsync(image.Id, SensitiveOcrText));

        var (isSensitive, status, vectors) = await ReadStateAsync(scope, image.Id);
        Assert.Equal(1, isSensitive);
        Assert.Equal("excluded", status);
        Assert.Equal(0, vectors);
    }
}
