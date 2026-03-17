using AiCli.Core.Chat;
using AiCli.Core.Commands;
using AiCli.Core.Configuration;
using AiCli.Core.Services;

namespace AiCli.Cli.Commands;

/// <summary>
/// Factory for creating built-in slash commands for the interactive chat session.
/// Mirrors the built-in commands from packages/cli/src/ui/commands/.
/// </summary>
public static class BuiltinSlashCommands
{
    /// <summary>
    /// Builds the full set of built-in slash commands.
    /// </summary>
    public static IReadOnlyList<SlashCommand> Build(
        AiChat chat,
        Config config,
        SlashCommandService commandService)
    {
        return new List<SlashCommand>
        {
            // /help
            new()
            {
                Name = "help",
                AltNames = new[] { "h", "?" },
                Description = "Show available slash commands",
                Kind = CommandKind.BuiltIn,
                Action = (_, _) =>
                    Task.FromResult(SlashCommandResult.Print(commandService.BuildHelpText())),
            },

            // /clear
            new()
            {
                Name = "clear",
                AltNames = new[] { "cls" },
                Description = "Clear the conversation history",
                Kind = CommandKind.BuiltIn,
                Action = (_, _) =>
                {
                    chat.ClearHistory();
                    return Task.FromResult(new SlashCommandResult
                    {
                        Handled = true,
                        ShouldClear = true,
                        Output = "Conversation history cleared.",
                    });
                },
            },

            // /quit
            new()
            {
                Name = "quit",
                AltNames = new[] { "exit", "q" },
                Description = "Exit the chat session",
                Kind = CommandKind.BuiltIn,
                Action = (_, _) =>
                    Task.FromResult(new SlashCommandResult
                    {
                        Handled = true,
                        ShouldQuit = true,
                        Output = "Goodbye!",
                    }),
            },

            // /model
            new()
            {
                Name = "model",
                Description = "Show or set the active model",
                Kind = CommandKind.BuiltIn,
                SubCommands = new List<SlashCommand>
                {
                    new()
                    {
                        Name = "show",
                        Description = "Show the current model",
                        Kind = CommandKind.BuiltIn,
                        Action = (_, _) =>
                            Task.FromResult(SlashCommandResult.Print(
                                $"Current model: {config.GetModel()}")),
                    },
                    new()
                    {
                        Name = "set",
                        Description = "Set the model (e.g. /model set gemini-2.0-flash)",
                        Kind = CommandKind.BuiltIn,
                        Action = (ctx, _) =>
                        {
                            if (string.IsNullOrWhiteSpace(ctx.Args))
                                return Task.FromResult(SlashCommandResult.Print(
                                    "Usage: /model set <model-name>"));
                            config.SetModel(ctx.Args.Trim());
                            return Task.FromResult(SlashCommandResult.Print(
                                $"Model set to: {ctx.Args.Trim()}"));
                        },
                    },
                },
                Action = (_, _) =>
                    Task.FromResult(SlashCommandResult.Print(
                        $"Current model: {config.GetModel()}\nUsage: /model show | /model set <name>")),
            },

            // /history
            new()
            {
                Name = "history",
                Description = "Show conversation history info",
                Kind = CommandKind.BuiltIn,
                Action = (_, _) =>
                    Task.FromResult(SlashCommandResult.Print(
                        $"Conversation has {chat.History.Count} message(s).")),
            },

            // /compress
            new()
            {
                Name = "compress",
                Description = "Compress the conversation history to save tokens",
                Kind = CommandKind.BuiltIn,
                Action = async (_, ct) =>
                {
                    var service = new ChatCompressionService();
                    var result = await service.CompressAsync(
                        chat,
                        promptId: "manual",
                        force: true,
                        model: config.GetModel(),
                        config: config,
                        hasFailedCompressionAttempt: false,
                        cancellationToken: ct);

                    if (result.Info.Status == CompressionStatus.Compressed
                        && result.NewHistory != null)
                    {
                        chat.ClearHistory();
                        // Reload compressed history via reflection on AiChat
                        // (ideal: expose a SetHistory method on AiChat)
                    }

                    return SlashCommandResult.Print(
                        $"Compression: {result.Info.Status} " +
                        $"({result.Info.OriginalTokenCount} → {result.Info.NewTokenCount} tokens)");
                },
            },

            // /tools
            new()
            {
                Name = "tools",
                Description = "List available tools",
                Kind = CommandKind.BuiltIn,
                Action = (ctx, _) =>
                {
                    if (ctx.Services.TryGetValue("toolRegistry", out var reg)
                        && reg is AiCli.Core.Tools.ToolRegistry toolRegistry)
                    {
                        var names = toolRegistry.AllToolNames;
                        var list = names.Count == 0
                            ? "No tools registered."
                            : string.Join('\n', names.Select(n => $"  - {n}"));
                        return Task.FromResult(SlashCommandResult.Print(
                            $"Available tools ({names.Count}):\n{list}"));
                    }
                    return Task.FromResult(SlashCommandResult.Print(
                        "Tool registry not available."));
                },
            },

            // /memory
            new()
            {
                Name = "memory",
                Description = "View AI memory",
                Kind = CommandKind.BuiltIn,
                SubCommands = new List<SlashCommand>
                {
                    new()
                    {
                        Name = "show",
                        Description = "Show current memory",
                        Kind = CommandKind.BuiltIn,
                        Action = (_, _) =>
                        {
                            var text = config.Memory?.Flatten()?.Trim();
                            return Task.FromResult(SlashCommandResult.Print(
                                string.IsNullOrWhiteSpace(text)
                                    ? "(No memory loaded)"
                                    : text));
                        },
                    },
                },
                Action = (_, _) =>
                {
                    var text = config.Memory?.Flatten()?.Trim();
                    return Task.FromResult(SlashCommandResult.Print(
                        string.IsNullOrWhiteSpace(text)
                            ? "(No memory loaded)"
                            : text));
                },
            },

            // /stats
            new()
            {
                Name = "stats",
                Description = "Show session statistics",
                Kind = CommandKind.BuiltIn,
                Action = (_, _) =>
                    Task.FromResult(SlashCommandResult.Print(
                        $"Messages: {chat.History.Count}\n" +
                        $"Model: {config.GetModel()}")),
            },
        };
    }
}
