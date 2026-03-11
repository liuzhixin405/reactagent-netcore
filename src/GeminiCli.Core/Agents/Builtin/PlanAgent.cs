using GeminiCli.Core.Chat;
using GeminiCli.Core.Logging;
using GeminiCli.Core.Tools;

namespace GeminiCli.Core.Agents.Builtin;

/// <summary>
/// Plan agent for creating and managing implementation plans.
/// </summary>
public class PlanAgent : Agent
{
    private readonly LocalExecutor _executor;
    private readonly List<string> _planSteps = new();

    public PlanAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
        : base(
            id,
            "Plan",
            "Create detailed implementation plans with step-by-step breakdown",
            AgentKind.Plan,
            new List<string> { "planning", "task_breakdown", "strategy" },
            toolRegistry,
            chat)
    {
        _executor = new LocalExecutor();
    }

    /// <summary>
    /// Gets the current plan steps.
    /// </summary>
    public IReadOnlyList<string> PlanSteps => _planSteps.AsReadOnly();

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
    /// Creates a plan for a given task.
    /// </summary>
    public async Task<List<string>> CreatePlanAsync(
        string task,
        CancellationToken cancellationToken = default)
    {
        var message = new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart($"Create a detailed implementation plan for:\n\n{task}\n\n" +
                    "Break down the task into clear, actionable steps. " +
                    "Each step should be specific and measurable.")
            }
        };

        var response = await Chat.SendMessageAsync(message, cancellationToken);

        // Extract plan steps from response
        var steps = new List<string>();
        var lines = response.Parts
            .OfType<TextContentPart>()
            .SelectMany(p => p.Text.Split('\n'))
            .ToList();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 &&
                (trimmed.StartsWith("1.") || trimmed.StartsWith("2.") ||
                 trimmed.StartsWith("3.") || trimmed.StartsWith("4.") ||
                 trimmed.StartsWith("5.") || trimmed.StartsWith("6.") ||
                 trimmed.StartsWith("7.") || trimmed.StartsWith("8.") ||
                 trimmed.StartsWith("9.") || trimmed.StartsWith("10.") ||
                 trimmed.StartsWith("-") || trimmed.StartsWith("*")))
            {
                steps.Add(trimmed);
            }
        }

        _planSteps.Clear();
        _planSteps.AddRange(steps);

        return steps;
    }

    /// <summary>
    /// Updates the plan with additional context.
    /// </summary>
    public async Task UpdatePlanAsync(
        string context,
        CancellationToken cancellationToken = default)
    {
        if (_planSteps.Count == 0)
        {
            throw new InvalidOperationException("No plan to update");
        }

        var currentPlan = string.Join("\n", _planSteps);
        var message = new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart($"Update the following plan with this context:\n\n" +
                    $"Current plan:\n{currentPlan}\n\n" +
                    $"Additional context:\n{context}")
            }
        };

        await Chat.SendMessageAsync(message, cancellationToken);
    }

    /// <summary>
    /// Validates the plan.
    /// </summary>
    public async Task<bool> ValidatePlanAsync(
        CancellationToken cancellationToken = default)
    {
        if (_planSteps.Count == 0)
        {
            throw new InvalidOperationException("No plan to validate");
        }

        var message = new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart($"Validate this plan for completeness and correctness:\n\n" +
                    $"{string.Join("\n", _planSteps)}\n\n" +
                    "Check for: missing steps, unclear instructions, " +
                    "dependencies, and potential issues. " +
                    "Respond with 'VALID' if the plan is good, " +
                    "or 'INVALID' followed by issues.")
            }
        };

        var response = await Chat.SendMessageAsync(message, cancellationToken);
        var text = string.Join("", response.Parts
            .OfType<TextContentPart>()
            .Select(p => p.Text));

        return text.Contains("VALID");
    }

    /// <summary>
    /// Clears the current plan.
    /// </summary>
    public void ClearPlan()
    {
        _planSteps.Clear();
    }

    /// <summary>
    /// Gets the system instruction for the plan agent.
    /// </summary>
    public static string GetSystemInstruction()
    {
        return """
            You are the Plan agent, specialized in creating detailed implementation plans.

            Your capabilities:
            - Break down complex tasks into manageable steps
            - Identify dependencies between steps
            - Consider edge cases and error handling
            - Estimate time and complexity
            - Provide clear, actionable instructions

            Guidelines for planning:
            1. Start with understanding the goal and constraints
            2. Break down into logical phases or components
            3. Each step should be specific and testable
            4. Identify dependencies and ordering
            5. Consider potential issues and fallbacks
            6. Keep steps balanced in size and complexity
            7. End with verification/testing steps

            When creating plans:
            - Use numbered lists for steps
            - Each step should be actionable
            - Include validation checkpoints
            - Note dependencies between steps
            - Consider error scenarios
            - Provide success criteria for each step
            """;
    }
}
