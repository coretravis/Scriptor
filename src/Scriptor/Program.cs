using Scriptor.Runner;
using System.CommandLine;

namespace Scriptor;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Debug: Print raw arguments
        Console.WriteLine($"Debug: Raw args = [{string.Join(", ", args)}]");

        var rootCommand = CreateRootCommand();
        return await rootCommand.InvokeAsync(args);
    }

    private static RootCommand CreateRootCommand()
    {
        var scriptFileArgument = new Argument<string>("script-file", "Path to the C# script file to compile and run");
        var methodOption = new Option<string>("--method", "Entry point method name (default: 'Main' or 'Run')") { IsRequired = false };
        var classOption = new Option<string>("--class", "Class name containing the entry point (auto-detected if not specified)") { IsRequired = false };
        var frameworkOption = new Option<string>("--framework", "Target framework (default: net8.0)") { IsRequired = false };
        var verboseOption = new Option<bool>("--verbose", "Enable verbose logging") { IsRequired = false };
        var argsOption = new Option<string[]>("--args", "Arguments to pass to the script") { IsRequired = false, AllowMultipleArgumentsPerToken = true };

        var rootCommand = new RootCommand("Scriptor - Dynamic C# Script Compiler and Runner")
        {
            scriptFileArgument,
            methodOption,
            classOption,
            frameworkOption,
            verboseOption,
            argsOption
        };

        rootCommand.SetHandler(async (string scriptFile, string method, string className, string framework, bool verbose, string[] scriptArgs) =>
        {
            try
            {
                var config = new ScriptRunnerConfig
                {
                    ScriptFile = scriptFile,
                    Method = method,
                    ClassName = className,
                    Framework = framework ?? "net8.0",
                    Verbose = verbose,
                    ScriptArgs = scriptArgs ?? Array.Empty<string>()
                };

                if (verbose)
                {
                    PrintDebugInfo(config);
                }

                var runner = new ScriptRunner();
                var exitCode = await runner.RunAsync(config);
                Environment.Exit(exitCode);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();

                if (verbose)
                {
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }

                Environment.Exit(1);
            }
        },
        scriptFileArgument,
        methodOption,
        classOption,
        frameworkOption,
        verboseOption,
        argsOption);

        return rootCommand;
    }

    private static void PrintDebugInfo(ScriptRunnerConfig config)
    {
        Console.WriteLine($"Debug: scriptFile = '{config.ScriptFile}'");
        Console.WriteLine($"Debug: method = '{config.Method}'");
        Console.WriteLine($"Debug: className = '{config.ClassName}'");
        Console.WriteLine($"Debug: framework = '{config.Framework}'");
        Console.WriteLine($"Debug: verbose = {config.Verbose}");
        Console.WriteLine($"Debug: scriptArgs = [{string.Join(", ", config.ScriptArgs)}]");
    }
}