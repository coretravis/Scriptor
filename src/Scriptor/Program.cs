using Scriptor.Runner;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace Scriptor;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"Debug: Raw args = [{string.Join(", ", args)}]");

        // Find the separator or unknown options that should be forwarded
        int separatorIndex = Array.IndexOf(args, "--");

        string[] commandArgs;
        string[] scriptArgs;

        if (separatorIndex >= 0)
        {
            commandArgs = args.Take(separatorIndex).ToArray();
            scriptArgs = args.Skip(separatorIndex + 1).ToArray();
        }
        else
        {
            // No explicit separator, try to detect where script args begin
            commandArgs = args;
            scriptArgs = Array.Empty<string>();
        }

        var rootCommand = CreateRootCommand();

        // Inject scriptArgs into the --args option automatically
        if (scriptArgs.Length > 0)
        {
            var allArgs = commandArgs.Concat(new[] { "--args" }).Concat(scriptArgs).ToArray();
            return await rootCommand.InvokeAsync(allArgs);
        }

        return await rootCommand.InvokeAsync(commandArgs);
    }


    private static RootCommand CreateRootCommand()
    {
        var scriptFileArgument = new Argument<string>("script-file", "Path to the C# script file to compile and run");
        var methodOption = new Option<string>("--method", "Entry point method name (default: 'Main' or 'Run')");
        var classOption = new Option<string>("--class", "Class name containing the entry point");
        var frameworkOption = new Option<string>("--framework", "Target framework (default: net8.0)");
        var verboseOption = new Option<bool>("--verbose", "Enable verbose logging");

        var rootCommand = new RootCommand("Scriptor - Dynamic C# Script Compiler and Runner")
    {
        scriptFileArgument,
        methodOption,
        classOption,
        frameworkOption,
        verboseOption
    };

        // 👇 This is the key line:
        rootCommand.TreatUnmatchedTokensAsErrors = false;

        rootCommand.SetHandler(async (InvocationContext ctx) =>
        {
            var parseResult = ctx.ParseResult;

            string scriptFile = parseResult.GetValueForArgument(scriptFileArgument);
            string method = parseResult.GetValueForOption(methodOption);
            string className = parseResult.GetValueForOption(classOption);
            string framework = parseResult.GetValueForOption(frameworkOption) ?? "net8.0";
            bool verbose = parseResult.GetValueForOption(verboseOption);

            // 👇 Capture all unrecognized args for the script
            string[] scriptArgs = parseResult.UnmatchedTokens.ToArray();

            var config = new ScriptRunnerConfig
            {
                ScriptFile = scriptFile,
                Method = method,
                ClassName = className,
                Framework = framework,
                Verbose = verbose,
                ScriptArgs = scriptArgs
            };

            if (verbose)
                PrintDebugInfo(config);

            var runner = new ScriptRunner();
            var exitCode = await runner.RunAsync(config);
            Environment.Exit(exitCode);
        });

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