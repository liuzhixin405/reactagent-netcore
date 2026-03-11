using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using GeminiCli.Core.Configuration;

namespace GeminiCli.Cli.Commands;

/// <summary>
/// Handler for the config command.
/// </summary>
public class ConfigCommand : CommandBase
{
    private readonly Option<string?> _apiKeyOption;
    private readonly Option<string?> _modelOption;
    private readonly Option<string?> _baseUrlOption;
    private readonly Option<bool> _showOption;
    private readonly Option<bool> _editOption;
    private readonly Option<bool> _resetOption;

    public override Command Command { get; }

    public ConfigCommand(Config config) : base(config)
    {
        Command = new Command("config")
        {
            Description = "Manage configuration settings"
        };

        // Add options
        _apiKeyOption = new Option<string?>(
            new[] { "-k", "--api-key" },
            () => null,
            "Set the API key");
        Command.AddOption(_apiKeyOption);

        _modelOption = new Option<string?>(
            new[] { "-m", "--model" },
            () => null,
            "Set the default AI model");
        Command.AddOption(_modelOption);

        _baseUrlOption = new Option<string?>(
            new[] { "-b", "--base-url" },
            () => null,
            "Set the API base URL (e.g. http://localhost:11434 for Ollama)");
        Command.AddOption(_baseUrlOption);

        _showOption = new Option<bool>(
            new[] { "-s", "--show" },
            () => false,
            "Show current configuration");
        Command.AddOption(_showOption);

        _editOption = new Option<bool>(
            new[] { "-e", "--edit" },
            () => false,
            "Open configuration file in editor");
        Command.AddOption(_editOption);

        _resetOption = new Option<bool>(
            new[] { "-r", "--reset" },
            () => false,
            "Reset configuration to defaults");
        Command.AddOption(_resetOption);

        // Set handler
        Command.SetHandler(async context => context.ExitCode = await HandleConfigAsync(context));
    }

    private async Task<int> HandleConfigAsync(InvocationContext context)
    {
        var apiKey = context.ParseResult.GetValueForOption(_apiKeyOption);
        var model = context.ParseResult.GetValueForOption(_modelOption);
        var baseUrl = context.ParseResult.GetValueForOption(_baseUrlOption);
        var showConfig = context.ParseResult.GetValueForOption(_showOption);
        var editConfig = context.ParseResult.GetValueForOption(_editOption);
        var resetConfig = context.ParseResult.GetValueForOption(_resetOption);

        try
        {
            // Reset configuration
            if (resetConfig)
            {
                DisplayInfo("Resetting configuration to defaults...");

                var configDir = _config.Storage.UserConfigDir;
                var configFile = Path.Combine(configDir, "settings.json");

                if (File.Exists(configFile))
                {
                    File.Delete(configFile);
                }

                DisplaySuccess("Configuration reset to defaults.");
                return 0;
            }

            // Edit configuration
            if (editConfig)
            {
                var configFile = Path.Combine(_config.Storage.UserConfigDir, "settings.json");

                // Try to open in default editor
                var editor = Environment.GetEnvironmentVariable("EDITOR") ??
                             Environment.GetEnvironmentVariable("VISUAL") ??
                             "notepad";

                DisplayInfo($"Opening configuration file in: {editor}");

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = configFile,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    DisplayError($"Could not open editor. File location: {configFile}");
                }

                return 0;
            }

            // Set API key
            if (!string.IsNullOrEmpty(apiKey))
            {
                _config.WithApiKey(apiKey!);
                await _config.SaveSettingsAsync(userLevel: true);
                DisplaySuccess("API key configured.");
                return 0;
            }

            // Set model
            if (!string.IsNullOrEmpty(model))
            {
                _config.WithModel(model!);
                await _config.SaveSettingsAsync(userLevel: true);
                DisplaySuccess($"Model set to: {model}");
                return 0;
            }

            if (!string.IsNullOrEmpty(baseUrl))
            {
                _config.WithBaseUrl(baseUrl!);
                await _config.SaveSettingsAsync(userLevel: true);
                DisplaySuccess($"Base URL set to: {baseUrl}");
                return 0;
            }

            // Show current configuration
            if (showConfig)
            {
                DisplayConfig();
                return 0;
            }

            // No options - show usage
            DisplayConfigUsage();
            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Error managing configuration: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Displays the current configuration.
    /// </summary>
    private void DisplayConfig()
    {
        var apiKey = _config.HasApiKey() ? _config.GetApiKey() : null;
        var model = _config.GetModel();
        var configDir = _config.Storage.UserConfigDir;

        var maskedApiKey = !string.IsNullOrEmpty(apiKey)
            ? $"{apiKey[..Math.Min(8, apiKey.Length)]}..."
            : "[red]Not configured[/]";

        _console.MarkupLine("[bold yellow]Configuration[/]");
        _console.MarkupLine($"[bold]API Key:[/] {maskedApiKey}");
        _console.MarkupLine($"[bold]Model:[/] {model}");
        _console.MarkupLine($"[bold]Base URL:[/] {_config.GetBaseUrl()}");
        _console.MarkupLine($"[bold]Config Directory:[/] {configDir}");
    }

    /// <summary>
    /// Displays configuration usage information.
    /// </summary>
    private void DisplayConfigUsage()
    {
        _console.MarkupLine("[bold]Usage:[/] gemini config [options]");
        _console.WriteLine();
        _console.MarkupLine("Options:");
        _console.MarkupLine("  [dim]-k, --api-key <key>[/]   Set the API key");
        _console.MarkupLine("  [dim]-m, --model <model>[/]    Set the default AI model");
        _console.MarkupLine("  [dim]-b, --base-url <url>[/]  Set the API base URL");
        _console.MarkupLine("  [dim]-s, --show[/]             Show current configuration");
        _console.MarkupLine("  [dim]-e, --edit[/]             Open configuration file in editor");
        _console.MarkupLine("  [dim]-r, --reset[/]            Reset configuration to defaults");
        _console.WriteLine();
        _console.MarkupLine("Examples:");
        _console.MarkupLine("  [green]gemini config --api-key sk-...[/]");
        _console.MarkupLine("  [green]gemini config --model gemini-pro[/]");
        _console.MarkupLine("  [green]gemini config --base-url http://localhost:11434[/]");
        _console.MarkupLine("  [green]gemini config --show[/]");
    }
}
