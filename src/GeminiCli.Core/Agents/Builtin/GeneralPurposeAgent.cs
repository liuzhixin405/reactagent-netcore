using GeminiCli.Core.Chat;
using GeminiCli.Core.Logging;
using GeminiCli.Core.Tools;

namespace GeminiCli.Core.Agents.Builtin;

/// <summary>
/// General purpose agent for a wide range of tasks.
/// </summary>
public class GeneralPurposeAgent : Agent
{
    private readonly LocalExecutor _executor;

    public GeneralPurposeAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
        : base(
            id,
            "General Purpose",
            "Capable of handling a wide range of tasks and delegating to specialists",
            AgentKind.GeneralPurpose,
            new List<string>
            {
                "general",
                "delegation",
                "coordination",
                "multi_task"
            },
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
    /// Determines if a task should be delegated to a specialist agent.
    /// </summary>
    public bool ShouldDelegate(string task, Dictionary<string, string> availableAgents)
    {
        // Check for specialist keywords
        var exploreKeywords = new[] { "explore", "search", "find", "locate", "discover" };
        var planKeywords = new[] { "plan", "breakdown", "strategy", "implement" };
        var codeKeywords = new[] { "refactor", "debug", "fix", "optimize", "code" };

        var lowerTask = task.ToLower();

        if (exploreKeywords.Any(k => lowerTask.Contains(k)) &&
            availableAgents.ContainsKey("explore"))
        {
            return true;
        }

        if (planKeywords.Any(k => lowerTask.Contains(k)) &&
            availableAgents.ContainsKey("plan"))
        {
            return true;
        }

        if (codeKeywords.Any(k => lowerTask.Contains(k)) &&
            availableAgents.ContainsKey("code"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the system instruction for the general purpose agent.
    /// </summary>
    public static string GetSystemInstruction()
    {
        return """
            You are the General Purpose agent, capable of handling a wide range of tasks.

            Your capabilities:
            - Use available tools to accomplish tasks
            - Delegate to specialist agents when appropriate
            - Coordinate multiple tool calls effectively
            - Handle errors and retries gracefully
            - Provide clear explanations and summaries

            Guidelines:
            - Choose the right tool for each task
            - Read before editing files
            - Use grep/glob to find relevant files
            - Break complex tasks into steps
            - Ask for clarification when needed
            - Report progress clearly
            - Handle errors appropriately

            Specialist agents:
            - Explore: For codebase exploration and file discovery
            - Plan: For creating implementation plans
            - Code: For coding and refactoring tasks

            When to delegate:
            - Large-scale exploration tasks → Explore agent
            - Complex implementation planning → Plan agent
            - Heavy coding/refactoring → Code agent
            """;
    }
}

/// <summary>
/// Code agent for coding, refactoring, and debugging tasks.
/// </summary>
public class CodeAgent : Agent
{
    private readonly LocalExecutor _executor;

    public CodeAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
        : base(
            id,
            "Code",
            "Specialized in coding, refactoring, debugging, and code review",
            AgentKind.Code,
            new List<string>
            {
                "coding",
                "refactoring",
                "debugging",
                "code_review"
            },
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
    /// Gets the system instruction for the code agent.
    /// </summary>
    public static string GetSystemInstruction()
    {
        return """
            You are the Code agent, specialized in coding tasks.

            Your capabilities:
            - Read and understand existing code
            - Write and edit code files
            - Refactor for better structure
            - Debug issues and fix bugs
            - Perform code reviews
            - Use shell commands for testing

            Guidelines:
            - Always read files before editing
            - Make targeted, precise changes
            - Preserve existing style and patterns
            - Add comments for complex logic
            - Test changes when possible
            - Explain your changes clearly

            Coding best practices:
            - Follow existing code style
            - Use meaningful names
            - Keep functions focused
            - Handle errors appropriately
            - Write tests when relevant
            - Document public APIs
            """;
    }
}
