using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scriptor.Compiler;
using Scriptor.NugetManagement;

namespace Scriptor.Runner;

public class ScriptRunner
{
    public async Task<int> RunAsync(ScriptRunnerConfig config)
    {
        ValidateConfig(config);

        var serviceProvider = SetupDependencyInjection(config.Verbose);
        var logger = serviceProvider.GetRequiredService<ILogger<ScriptRunner>>();
        var compiler = serviceProvider.GetRequiredService<DynamicCompiler>();

        try
        {
            logger.LogInformation("Loading script file: {ScriptFile}", config.ScriptFile);

            var sourceCode = await LoadScriptFileAsync(config.ScriptFile);

            logger.LogInformation("Compiling script...");

            var assemblyName = GetSafeAssemblyName(config.ScriptFile);
            var assembly = await compiler.CompileCommandFromSourceAsync(sourceCode, assemblyName);

            if (assembly == null)
            {
                throw new InvalidOperationException("Compilation failed - no assembly was produced");
            }

            logger.LogInformation("Script compiled successfully");

            var executor = new ScriptExecutor(logger);
            var exitCode = await executor.ExecuteAsync(assembly, config.Method, config.ClassName, config.ScriptArgs);

            return exitCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run script: {ScriptFile}", config.ScriptFile);
            throw;
        }
        finally
        {
            compiler?.Dispose();
            await serviceProvider.DisposeAsync();
        }
    }

    private void ValidateConfig(ScriptRunnerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ScriptFile))
        {
            Console.WriteLine($"Debug: Received scriptFile parameter: '{config.ScriptFile ?? "null"}'");
            throw new ArgumentException("Script file path cannot be null or empty", nameof(config.ScriptFile));
        }

        try
        {
            config.ScriptFile = Path.GetFullPath(config.ScriptFile);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid script file path: {ex.Message}", nameof(config.ScriptFile), ex);
        }

        if (!File.Exists(config.ScriptFile))
        {
            throw new FileNotFoundException($"Script file not found: {config.ScriptFile}");
        }

        var extension = Path.GetExtension(config.ScriptFile).ToLowerInvariant();
        if (extension != ".cs" && extension != ".csx")
        {
            throw new ArgumentException($"Script file must have .cs or .csx extension, got: {extension}");
        }
    }

    private ServiceProvider SetupDependencyInjection(bool verbose)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });

        services.AddHttpClient();
        services.AddSingleton<INuGetPackageManager, NuGetPackageManager>();
        services.AddSingleton<DynamicCompiler>();

        return services.BuildServiceProvider();
    }

    private async Task<string> LoadScriptFileAsync(string scriptFile)
    {
        try
        {
            var sourceCode = await File.ReadAllTextAsync(scriptFile);

            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                throw new InvalidOperationException("Script file is empty or contains only whitespace");
            }

            return sourceCode;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Access denied reading script file: {scriptFile}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Error reading script file: {scriptFile}", ex);
        }
    }

    private string GetSafeAssemblyName(string scriptFile)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(scriptFile);

            var invalidChars = Path.GetInvalidFileNameChars().Concat(new char[] { ' ', '-', '.', '(', ')', '[', ']' });
            foreach (var invalidChar in invalidChars)
            {
                fileName = fileName.Replace(invalidChar, '_');
            }

            if (!char.IsLetter(fileName[0]) && fileName[0] != '_')
            {
                fileName = "_" + fileName;
            }

            return string.IsNullOrEmpty(fileName) ? "DynamicScript" : fileName;
        }
        catch
        {
            return "DynamicScript";
        }
    }
}
