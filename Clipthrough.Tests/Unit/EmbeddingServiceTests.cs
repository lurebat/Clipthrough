using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class EmbeddingServiceTests
{
    private static EmbeddingService NewService() => new();

    [Fact]
    public async Task EmbedAsync_ProducesNormalizedVectorOfExpectedDimension()
    {
        using var svc = NewService();
        var vec = await svc.EmbedAsync("hello world");

        Assert.Equal(svc.Dimensions, vec.Length);
        Assert.Equal(384, vec.Length);

        double sumSq = 0;
        foreach (var f in vec) sumSq += (double)f * f;
        Assert.InRange(sumSq, 0.99, 1.01);
    }

    [Fact]
    public async Task EmbedBatchAsync_ProducesOneVectorPerInput_InOrder()
    {
        using var svc = NewService();
        var inputs = new[] { "cat sitting on a mat", "feline on a rug", "stock market crashed today" };
        var vecs = await svc.EmbedBatchAsync(inputs);

        Assert.Equal(inputs.Length, vecs.Count);
        Assert.All(vecs, v => Assert.Equal(384, v.Length));

        // Similar sentences should cosine-correlate more than unrelated ones.
        float Dot(float[] a, float[] b)
        {
            float s = 0;
            for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        var simCatFeline = Dot(vecs[0], vecs[1]);
        var simCatStock = Dot(vecs[0], vecs[2]);
        Assert.True(simCatFeline > simCatStock,
            $"expected cat/feline ({simCatFeline:F3}) > cat/stock ({simCatStock:F3})");
    }

    [Fact]
    public async Task EmbedAsync_EmptyInput_ReturnsZeroVector()
    {
        using var svc = NewService();
        var vec = await svc.EmbedAsync("   ");
        Assert.Equal(384, vec.Length);
        Assert.True(vec.All(f => f == 0f));
    }
}
