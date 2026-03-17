using AiCli.Core.Configuration;
using AiCli.Core.Types;

namespace AiCli.Core.Chat;

/// <summary>
/// 模型角色，用于路由到最合适的本地模型。
/// </summary>
public enum ModelRole
{
    /// <summary>
    /// 语义嵌入 / 向量检索（bge-m3）。
    /// 用于 RAG、相似度搜索、代码库语义索引。
    /// </summary>
    Embedding,

    /// <summary>
    /// 复杂推理 / 大上下文（gpt-oss:20b，128K context）。
    /// 用于架构规划、复杂重构分析、整体代码库理解。
    /// </summary>
    Thinking,

    /// <summary>
    /// 快速代码执行（qwen2.5-coder:7b）。
    /// 用于单文件编辑、简单修复、批量小改动。
    /// </summary>
    Fast,
}

/// <summary>
/// 多模型编排器：根据任务角色自动路由到最合适的本地模型。
/// 实现 IContentGenerator 接口，默认路由到思考模型，保持向后兼容。
/// </summary>
public sealed class MultiModelOrchestrator : IContentGenerator, IAsyncDisposable
{
    private readonly OllamaContentGenerator _embeddingGenerator;
    private readonly OllamaContentGenerator _thinkingGenerator;
    private readonly OllamaContentGenerator _fastGenerator;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public MultiModelOrchestrator(Config config, HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();

        _embeddingGenerator = new OllamaContentGenerator(config, config.GetEmbeddingModel(), enableThinking: false, _httpClient);
        _thinkingGenerator  = new OllamaContentGenerator(config, config.GetThinkingModel(),  enableThinking: true,  _httpClient);
        _fastGenerator      = new OllamaContentGenerator(config, config.GetFastModel(),      enableThinking: false, _httpClient);
    }

    /// <summary>
    /// 根据角色返回对应的生成器。
    /// </summary>
    public IContentGenerator GetGenerator(ModelRole role) => role switch
    {
        ModelRole.Embedding => _embeddingGenerator,
        ModelRole.Thinking  => _thinkingGenerator,
        ModelRole.Fast      => _fastGenerator,
        _                   => _thinkingGenerator,
    };

    /// <summary>
    /// 根据消息内容自动推断角色并路由。
    /// - 消息很短（&lt;200字）且含代码关键字 → Fast
    /// - 其余 → Thinking
    /// </summary>
    public IContentGenerator AutoSelect(ContentMessage message)
    {
        var text = string.Concat(message.Parts.OfType<TextContentPart>().Select(p => p.Text));

        if (text.Length < 200 && ContainsCodeKeywords(text))
            return _fastGenerator;

        return _thinkingGenerator;
    }

    // ── IContentGenerator（委托到思考模型，保持向后兼容）──────────────────

    public string GetModelId() => _thinkingGenerator.GetModelId();

    public Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.SendMessageAsync(message, cancellationToken);

    public IAsyncEnumerable<ContentMessage> SendMessageStreamAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.SendMessageStreamAsync(message, cancellationToken);

    public Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.GenerateContentAsync(request, cancellationToken);

    public IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.GenerateContentStreamAsync(request, cancellationToken);

    public Task<CountTokensResponse> CountTokensAsync(
        CountTokensRequest request,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.CountTokensAsync(request, cancellationToken);

    public Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default)
        => _embeddingGenerator.EmbedContentAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _embeddingGenerator.DisposeAsync();
        await _thinkingGenerator.DisposeAsync();
        await _fastGenerator.DisposeAsync();

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static readonly string[] _codeKeywords =
    {
        "fix", "edit", "rename", "add", "remove", "replace", "insert",
        "修复", "编辑", "重命名", "添加", "删除", "替换",
    };

    private static bool ContainsCodeKeywords(string text)
    {
        var lower = text.ToLowerInvariant();
        return _codeKeywords.Any(k => lower.Contains(k));
    }
}
