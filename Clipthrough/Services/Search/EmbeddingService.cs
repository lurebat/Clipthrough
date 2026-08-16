using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Clipthrough.Services.Search;

/// <summary>
/// ONNX-backed implementation of <see cref="IEmbeddingService"/> using the bundled
/// <c>all-MiniLM-L6-v2</c> sentence-transformer (int8 quantized, 384-dim).
///
/// The Xenova ONNX export outputs raw token embeddings (<c>last_hidden_state</c>); we perform
/// attention-masked mean pooling and L2 normalization on our side so the returned vectors are
/// directly comparable via dot product.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService, IDisposable
{
    private const int MaxTokens = 128;
    private const int EmbeddingDim = 384;

    private readonly string _modelDir;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _sessionLock = new();

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private string[]? _inputNames;
    private string? _outputName;
    private bool _disposed;

    public EmbeddingService(string? modelDirectory = null)
    {
        _modelDir = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "SemanticModel");
    }

    public int Dimensions => EmbeddingDim;

    public bool IsReady => _session is not null && _tokenizer is not null;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await EmbedBatchAsync(new[] { text ?? string.Empty }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var session = _session!;
        var tokenizer = _tokenizer!;
        var padId = tokenizer.PaddingTokenId;

        var perInput = new List<long[]>(texts.Count);
        var emptyMask = new bool[texts.Count];
        var maxLen = 1;

        for (var i = 0; i < texts.Count; i++)
        {
            var raw = texts[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                emptyMask[i] = true;
                perInput.Add(Array.Empty<long>());
                continue;
            }

            var ids = tokenizer.EncodeToIds(raw, MaxTokens, addSpecialTokens: true, out _, out _, considerPreTokenization: true, considerNormalization: true);
            var arr = new long[ids.Count];
            for (var j = 0; j < ids.Count; j++) arr[j] = ids[j];
            perInput.Add(arr);
            if (arr.Length > maxLen) maxLen = arr.Length;
        }

        var batch = texts.Count;
        var inputIds = new long[batch * maxLen];
        var attention = new long[batch * maxLen];
        var tokenType = new long[batch * maxLen]; // all zeros

        for (var i = 0; i < batch; i++)
        {
            var row = perInput[i];
            for (var j = 0; j < row.Length; j++)
            {
                inputIds[i * maxLen + j] = row[j];
                attention[i * maxLen + j] = 1;
            }
            for (var j = row.Length; j < maxLen; j++)
            {
                inputIds[i * maxLen + j] = padId;
                // attention stays 0
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var dims = new[] { (long)batch, maxLen };
        using var idsTensor = OrtValue.CreateTensorValueFromMemory(inputIds, dims);
        using var attnTensor = OrtValue.CreateTensorValueFromMemory(attention, dims);
        using var typeTensor = OrtValue.CreateTensorValueFromMemory(tokenType, dims);

        var feeds = new Dictionary<string, OrtValue>(3);
        foreach (var name in _inputNames!)
        {
            switch (name)
            {
                case "input_ids": feeds[name] = idsTensor; break;
                case "attention_mask": feeds[name] = attnTensor; break;
                case "token_type_ids": feeds[name] = typeTensor; break;
            }
        }

        using var runOptions = new RunOptions();
        IDisposableReadOnlyCollection<OrtValue> outputs;
        lock (_sessionLock)
        {
            outputs = session.Run(runOptions, feeds, new[] { _outputName! });
        }

        using (outputs)
        {
            var outTensor = outputs[0];
            var shape = outTensor.GetTensorTypeAndShape().Shape;
            var data = outTensor.GetTensorDataAsSpan<float>();

            var result = new float[batch][];

            if (shape.Length == 3)
            {
                // [batch, tokens, dim] — mean pool with attention mask, then L2 normalize
                var tokens = (int)shape[1];
                var dim = (int)shape[2];
                if (dim != EmbeddingDim)
                    throw new InvalidOperationException($"Unexpected embedding dim {dim}, expected {EmbeddingDim}.");

                for (var b = 0; b < batch; b++)
                {
                    var vec = new float[dim];
                    if (emptyMask[b])
                    {
                        result[b] = vec;
                        continue;
                    }

                    var count = 0;
                    for (var t = 0; t < tokens; t++)
                    {
                        if (attention[b * maxLen + t] == 0) continue;
                        var offset = (b * tokens + t) * dim;
                        for (var d = 0; d < dim; d++) vec[d] += data[offset + d];
                        count++;
                    }
                    if (count > 0)
                    {
                        var inv = 1f / count;
                        for (var d = 0; d < dim; d++) vec[d] *= inv;
                    }
                    Normalize(vec);
                    result[b] = vec;
                }
            }
            else if (shape.Length == 2)
            {
                // [batch, dim] — already pooled by the graph
                var dim = (int)shape[1];
                if (dim != EmbeddingDim)
                    throw new InvalidOperationException($"Unexpected embedding dim {dim}, expected {EmbeddingDim}.");

                for (var b = 0; b < batch; b++)
                {
                    var vec = new float[dim];
                    if (!emptyMask[b])
                    {
                        data.Slice(b * dim, dim).CopyTo(vec);
                        Normalize(vec);
                    }
                    result[b] = vec;
                }
            }
            else
            {
                throw new InvalidOperationException($"Unsupported output rank {shape.Length}.");
            }

            return result;
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (IsReady) return;
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady) return;

            var modelPath = Path.Combine(_modelDir, "model.onnx");
            var vocabPath = Path.Combine(_modelDir, "vocab.txt");
            if (!File.Exists(modelPath)) throw new FileNotFoundException("Semantic model ONNX file not found.", modelPath);
            if (!File.Exists(vocabPath)) throw new FileNotFoundException("Semantic model vocab file not found.", vocabPath);

            // BertTokenizer default options match the all-MiniLM tokenizer_config (lowercase, CJK splitting).
            var tokenizer = await BertTokenizer.CreateAsync(vocabPath, options: new BertOptions { LowerCaseBeforeTokenization = true }, ct).ConfigureAwait(false);

            var sessionOpts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            };
            var session = new InferenceSession(modelPath, sessionOpts);

            var inputNames = session.InputNames.ToArray();
            // Pick the embedding-style output: rank 2 or 3 with a trailing dim == EmbeddingDim.
            // Falls back to the first output if metadata doesn't include static shape info.
            string? outputName = null;
            foreach (var name in session.OutputNames)
            {
                if (!session.OutputMetadata.TryGetValue(name, out var meta)) continue;
                var dims = meta.Dimensions;
                if (dims.Length >= 2 && dims[^1] == EmbeddingDim) { outputName = name; break; }
            }
            outputName ??= session.OutputNames[0];

            _tokenizer = tokenizer;
            _session = session;
            _inputNames = inputNames;
            _outputName = outputName;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        if (sum <= 0) return;
        var inv = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _loadGate.Dispose();
    }
}
