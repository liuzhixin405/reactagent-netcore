using GeminiCli.Core.Chat;
using GeminiCli.Core.Logging;
using GeminiCli.Core.Tools;

namespace GeminiCli.Core.Agents.Builtin;

/// <summary>
/// Explore agent for fast codebase exploration.
/// Uses grep and glob to find files and code patterns.
/// </summary>
public class ExploreAgent : Agent
{
    private readonly LocalExecutor _executor;

    public ExploreAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
        : base(
            id,
            "Explore",
            "Fast codebase exploration using grep and glob patterns",
            AgentKind.Explore,
            new List<string> { "search", "file_discovery", "code_exploration" },
            toolRegistry,
            chat)
    {
        _executor = new LocalExecutor();
    }

    /// <summary>
    /// Executes a tool with the local executor.
    /// </summary>
    protected override async Task<ToolExecutionResult> ExecuteToolAsync(
        IToolBuilder tool,
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken)
    {
        var invocation = tool.Build(arguments ?? new Dictionary<string, object>());
        var options = _executor.CreateOptions(ApprovalMode.Auto);

        return await _executor.ExecuteAsync(invocation, options, cancellationToken);
    }

    /// <summary>
    /// Gets the system instruction for the explore agent.
    /// </summary>
    public static string GetSystemInstruction()
    {
        return """
            You are the Explore agent, specialized in fast codebase exploration.

            Your capabilities:
            - Use glob to find files by pattern (e.g., **/*.cs for all C# files)
            - Use grep to search for text patterns in files
            - Use read_file to examine file contents
            - Focus on finding relevant files and understanding code structure
            - Provide concise file paths and line numbers
            - Explain code patterns and architecture

            Guidelines:
            - Use glob patterns like **/*.cs, src/**/*.ts, etc.
            - Search for specific terms with grep (e.g., class names, function names)
            - Read only necessary files - don't read everything
            - Summarize findings with file paths
            - Note file sizes and modification times when relevant

            When exploring:
            1. Start with broad patterns (glob) to understand structure
            2. Use grep to find specific code elements
            3. Read key files to understand implementation
            4. Report back with clear, organized findings
            """;
    }
}
