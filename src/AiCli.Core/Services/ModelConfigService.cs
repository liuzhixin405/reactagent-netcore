using AiCli.Core.Logging;
using System.Text.Json;

namespace AiCli.Core.Services;

/// <summary>
/// Key used to resolve a model configuration.
/// </summary>
public record ModelConfigKey
{
    /// <summary>
    /// Model name or alias (e.g., "gemini-2.0-flash", "summarizer-default").
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Optional scope to narrow override application (e.g., agent name).
    /// </summary>
    public string? OverrideScope { get; init; }

    /// <summary>
    /// Whether this request is a retry — allows different settings on retries.
    /// </summary>
    public bool IsRetry { get; init; }

    /// <summary>
    /// Whether this is the primary interactive chat model.
    /// Enables fallback to "chat-base" alias when no alias is defined.
    /// </summary>
    public bool IsChatModel { get; init; }
}

/// <summary>
/// Generation configuration options (mirrors Gemini API GenerateContentConfig).
/// </summary>
public record GenerateContentConfig
{
    public float? Temperature { get; init; }
    public float? TopP { get; init; }
    public int? TopK { get; init; }
    public int? MaxOutputTokens { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    public string? ResponseMimeType { get; init; }
    public Dictionary<string, object>? Extra { get; init; }
}

/// <summary>
/// A model + generation config pair.
/// </summary>
public record ModelConfig
{
    public string? Model { get; init; }
    public GenerateContentConfig? GenerateContentConfig { get; init; }
}

/// <summary>
/// An alias that optionally extends another alias.
/// </summary>
public record ModelConfigAlias
{
    public string? Extends { get; init; }
    public required ModelConfig ModelConfig { get; init; }
}

/// <summary>
/// Conditions that must match for an override to apply.
/// </summary>
public record ModelConfigOverrideMatch
{
    public string? Model { get; init; }
    public string? OverrideScope { get; init; }
    public bool? IsRetry { get; init; }
}

/// <summary>
/// A scoped override: when the match conditions are met, apply the given config on top.
/// </summary>
public record ModelConfigOverride
{
    public required ModelConfigOverrideMatch Match { get; init; }
    public required ModelConfig ModelConfig { get; init; }
}

/// <summary>
/// Configuration for ModelConfigService.
/// </summary>
public record ModelConfigServiceConfig
{
    public Dictionary<string, ModelConfigAlias>? Aliases { get; init; }
    public Dictionary<string, ModelConfigAlias>? CustomAliases { get; init; }
    public IReadOnlyList<ModelConfigOverride>? Overrides { get; init; }
    public IReadOnlyList<ModelConfigOverride>? CustomOverrides { get; init; }
}

/// <summary>
/// A fully resolved model configuration (model name + generation config).
/// </summary>
public record ResolvedModelConfig
{
    public required string Model { get; init; }
    public required GenerateContentConfig GenerateContentConfig { get; init; }
}

/// <summary>
/// Resolves model configurations from aliases and overrides.
/// Supports alias inheritance chains and scoped overrides.
/// Ported from packages/core/src/services/modelConfigService.ts
/// </summary>
public class ModelConfigService
{
    private const int MaxAliasChainDepth = 100;

    private static readonly ILogger Logger = LoggerHelper.ForContext<ModelConfigService>();

    private readonly ModelConfigServiceConfig _config;
    private readonly Dictionary<string, ModelConfigAlias> _runtimeAliases = new();
    private readonly List<ModelConfigOverride> _runtimeOverrides = new();

    public ModelConfigService(ModelConfigServiceConfig config)
    {
        _config = config;
    }

    public void RegisterRuntimeAlias(string name, ModelConfigAlias alias)
    {
        _runtimeAliases[name] = alias;
    }

    public void RegisterRuntimeOverride(ModelConfigOverride @override)
    {
        _runtimeOverrides.Add(@override);
    }

    /// <summary>
    /// Resolves the model configuration for the given key.
    /// Throws if no model name can be resolved.
    /// </summary>
    public ResolvedModelConfig GetResolvedConfig(ModelConfigKey key)
    {
        var (model, config) = InternalGetResolvedConfig(key);

        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException(
                $"Could not resolve a model name for alias \"{key.Model}\". " +
                "Please ensure the alias chain or a matching override specifies a model.");

        return new ResolvedModelConfig
        {
            Model = model!,
            GenerateContentConfig = config ?? new GenerateContentConfig(),
        };
    }

    // ─── Internal Resolution ──────────────────────────────────────────────────

    private (string? model, GenerateContentConfig? config) InternalGetResolvedConfig(ModelConfigKey key)
    {
        var allAliases = MergeAliases();
        var allOverrides = MergeOverrides();

        var (aliasChain, baseModel, resolvedConfig) = ResolveAliasChain(
            key.Model, allAliases, key.IsChatModel);

        var modelToLevel = BuildModelLevelMap(aliasChain, baseModel);
        var matches = FindMatchingOverrides(allOverrides, key, modelToLevel);
        SortOverrides(matches);

        ModelConfig currentConfig = new() { Model = baseModel, GenerateContentConfig = resolvedConfig };
        foreach (var match in matches)
            currentConfig = Merge(currentConfig, match.modelConfig);

        return (currentConfig.Model, currentConfig.GenerateContentConfig);
    }

    private Dictionary<string, ModelConfigAlias> MergeAliases()
    {
        var result = new Dictionary<string, ModelConfigAlias>();
        foreach (var (k, v) in _config.Aliases ?? new())   result[k] = v;
        foreach (var (k, v) in _config.CustomAliases ?? new()) result[k] = v;
        foreach (var (k, v) in _runtimeAliases)            result[k] = v;
        return result;
    }

    private List<ModelConfigOverride> MergeOverrides() =>
        (_config.Overrides ?? Array.Empty<ModelConfigOverride>())
        .Concat(_config.CustomOverrides ?? Array.Empty<ModelConfigOverride>())
        .Concat(_runtimeOverrides)
        .ToList();

    private (List<string> aliasChain, string? baseModel, GenerateContentConfig? config)
        ResolveAliasChain(string requestedModel, Dictionary<string, ModelConfigAlias> allAliases, bool isChatModel)
    {
        if (allAliases.TryGetValue(requestedModel, out _))
        {
            var chain = new List<string>();
            string? current = requestedModel;
            var visited = new HashSet<string>();

            while (current != null)
            {
                if (!allAliases.TryGetValue(current, out var alias))
                    throw new InvalidOperationException($"Alias \"{current}\" not found.");

                if (chain.Count >= MaxAliasChainDepth)
                    throw new InvalidOperationException(
                        $"Alias inheritance chain exceeded maximum depth of {MaxAliasChainDepth}.");

                if (!visited.Add(current))
                    throw new InvalidOperationException(
                        $"Circular alias dependency: {string.Join(" -> ", visited)} -> {current}");

                chain.Add(current);
                current = alias.Extends;
            }

            // Root-to-leaf: reverse for merging
            chain.Reverse();
            ModelConfig merged = new();
            foreach (var name in chain)
                merged = Merge(merged, allAliases[name].ModelConfig);

            return (chain, merged.Model, merged.GenerateContentConfig);
        }

        if (isChatModel && allAliases.ContainsKey("chat-base"))
        {
            var (fbChain, fbBase, fbConfig) = ResolveAliasChain("chat-base", allAliases, false);
            return (new List<string>(fbChain) { requestedModel }, requestedModel, fbConfig);
        }

        return (new List<string> { requestedModel }, requestedModel, null);
    }

    private static Dictionary<string, int> BuildModelLevelMap(
        List<string> aliasChain, string? baseModel)
    {
        var map = new Dictionary<string, int>();
        if (baseModel != null) map[baseModel] = 0;
        for (int i = 0; i < aliasChain.Count; i++)
            map[aliasChain[i]] = i + 1;
        return map;
    }

    private static List<(int specificity, int level, ModelConfig modelConfig, int index)>
        FindMatchingOverrides(
            List<ModelConfigOverride> overrides,
            ModelConfigKey key,
            Dictionary<string, int> modelToLevel)
    {
        var result = new List<(int specificity, int level, ModelConfig modelConfig, int index)>();

        for (int i = 0; i < overrides.Count; i++)
        {
            var ov = overrides[i];
            var match = ov.Match;
            int specificity = 0;
            int level = 0;
            bool isMatch = true;

            // model match
            if (match.Model != null)
            {
                specificity++;
                if (!modelToLevel.TryGetValue(match.Model, out level))
                { isMatch = false; goto next; }
            }

            // overrideScope match
            if (match.OverrideScope != null)
            {
                specificity++;
                var scope = match.OverrideScope == "core"
                    ? (string.IsNullOrEmpty(key.OverrideScope) || key.OverrideScope == "core")
                    : key.OverrideScope == match.OverrideScope;
                if (!scope) { isMatch = false; goto next; }
            }

            // isRetry match
            if (match.IsRetry.HasValue)
            {
                specificity++;
                if (key.IsRetry != match.IsRetry.Value) { isMatch = false; goto next; }
            }

            if (specificity == 0) { isMatch = false; } // empty match object — skip

            next:
            if (isMatch)
                result.Add((specificity, level, ov.ModelConfig, i));
        }

        return result;
    }

    private static void SortOverrides(List<(int specificity, int level, ModelConfig modelConfig, int index)> matches)
    {
        matches.Sort((a, b) =>
        {
            if (a.level != b.level) return a.level.CompareTo(b.level);
            if (a.specificity != b.specificity) return a.specificity.CompareTo(b.specificity);
            return a.index.CompareTo(b.index);
        });
    }

    // ─── Merge ────────────────────────────────────────────────────────────────

    public static ModelConfig Merge(ModelConfig baseConfig, ModelConfig @override) => new()
    {
        Model = @override.Model ?? baseConfig.Model,
        GenerateContentConfig = DeepMerge(
            baseConfig.GenerateContentConfig,
            @override.GenerateContentConfig),
    };

    public static GenerateContentConfig DeepMerge(
        GenerateContentConfig? a, GenerateContentConfig? b)
    {
        if (a == null) return b ?? new GenerateContentConfig();
        if (b == null) return a;

        // Merge Extra dictionaries
        Dictionary<string, object>? mergedExtra = null;
        if (a.Extra != null || b.Extra != null)
        {
            mergedExtra = new Dictionary<string, object>();
            foreach (var kv in a.Extra ?? new()) mergedExtra[kv.Key] = kv.Value;
            foreach (var kv in b.Extra ?? new()) mergedExtra[kv.Key] = kv.Value;
        }

        return new GenerateContentConfig
        {
            Temperature = b.Temperature ?? a.Temperature,
            TopP = b.TopP ?? a.TopP,
            TopK = b.TopK ?? a.TopK,
            MaxOutputTokens = b.MaxOutputTokens ?? a.MaxOutputTokens,
            StopSequences = b.StopSequences ?? a.StopSequences,
            ResponseMimeType = b.ResponseMimeType ?? a.ResponseMimeType,
            Extra = mergedExtra,
        };
    }
}
