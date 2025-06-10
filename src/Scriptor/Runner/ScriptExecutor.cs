using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Scriptor.Runner;


public class ScriptExecutor
{
    private readonly ILogger _logger;

    public ScriptExecutor(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> ExecuteAsync(Assembly assembly, string? methodName, string? className, string[] args)
    {
        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        try
        {
            var (entryPointType, entryPointMethod) = FindEntryPoint(assembly, methodName, className);

            _logger.LogInformation("Executing: {ClassName}.{MethodName}", entryPointType.Name, entryPointMethod.Name);

            var methodArgs = PrepareMethodArguments(entryPointMethod, args);
            var instance = CreateInstanceIfNeeded(entryPointType, entryPointMethod);

            return await InvokeMethodAsync(entryPointMethod, instance, methodArgs);
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaderExceptions = ex.LoaderExceptions?.Where(e => e != null).ToArray() ?? Array.Empty<Exception>();
            var message = $"Failed to load types from assembly: {string.Join("; ", loaderExceptions.Select(e => e.Message))}";
            throw new InvalidOperationException(message, ex);
        }
    }

    private (Type entryPointType, MethodInfo entryPointMethod) FindEntryPoint(Assembly assembly, string? methodName, string? className)
    {
        var entryPointType = FindEntryPointType(assembly, className);
        var entryPointMethod = FindEntryPointMethod(entryPointType, methodName);

        return (entryPointType, entryPointMethod);
    }

    private Type FindEntryPointType(Assembly assembly, string? className)
    {
        if (!string.IsNullOrEmpty(className))
        {
            var specificType = assembly.GetType(className) ??
                assembly.GetTypes().FirstOrDefault(t =>
                    t.Name.Equals(className, StringComparison.OrdinalIgnoreCase) ||
                    t.FullName?.Equals(className, StringComparison.OrdinalIgnoreCase) == true);

            if (specificType == null)
            {
                var availableTypes = assembly.GetTypes()
                    .Where(t => t.IsClass && t.IsPublic)
                    .Select(t => t.FullName ?? t.Name)
                    .ToArray();

                throw new InvalidOperationException(
                    $"Class '{className}' not found in compiled assembly. " +
                    $"Available classes: {string.Join(", ", availableTypes)}");
            }

            return specificType;
        }

        return AutoDetectEntryPointType(assembly);
    }

    private Type AutoDetectEntryPointType(Assembly assembly)
    {
        var types = assembly.GetTypes().Where(t => t.IsClass && t.IsPublic).ToArray();

        // Look for classes with Main or Run methods
        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            if (methods.Any(m => m.Name == "Main" || m.Name == "Run"))
            {
                return type;
            }
        }

        // If no Main/Run methods found, use the first public class
        var firstType = types.FirstOrDefault();
        if (firstType == null)
        {
            throw new InvalidOperationException("No suitable entry point class found");
        }

        return firstType;
    }

    private MethodInfo FindEntryPointMethod(Type entryPointType, string? methodName)
    {
        var candidateMethodNames = new List<string>();

        if (!string.IsNullOrEmpty(methodName))
        {
            candidateMethodNames.Add(methodName);
        }

        candidateMethodNames.AddRange(new[] { "Main", "Run", "Execute" });

        foreach (var candidateMethod in candidateMethodNames.Distinct())
        {
            // Look for static methods first (preferred for entry points)
            var staticMethod = entryPointType.GetMethod(candidateMethod, BindingFlags.Public | BindingFlags.Static);
            if (staticMethod != null)
                return staticMethod;

            // Then look for instance methods
            var instanceMethod = entryPointType.GetMethod(candidateMethod, BindingFlags.Public | BindingFlags.Instance);
            if (instanceMethod != null)
                return instanceMethod;
        }

        var availableMethods = entryPointType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && !m.IsConstructor)
            .Select(m => $"{(m.IsStatic ? "static " : "")}{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
            .ToArray();

        throw new InvalidOperationException(
            $"No suitable entry point method found in class '{entryPointType.Name}'. " +
            $"Available methods: {string.Join(", ", availableMethods)}");
    }

    private object[] PrepareMethodArguments(MethodInfo entryPointMethod, string[] args)
    {
        var parameters = entryPointMethod.GetParameters();
        var methodArgs = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];

            if (param.ParameterType == typeof(string[]))
            {
                methodArgs[i] = args;
            }
            else if (param.ParameterType == typeof(string) && args.Length > i)
            {
                methodArgs[i] = args[i];
            }
            else if (param.HasDefaultValue)
            {
                methodArgs[i] = param.DefaultValue;
            }
            else if (param.ParameterType.IsValueType)
            {
                methodArgs[i] = Activator.CreateInstance(param.ParameterType);
            }
            else
            {
                methodArgs[i] = null;
            }
        }

        return methodArgs;
    }

    private object? CreateInstanceIfNeeded(Type entryPointType, MethodInfo entryPointMethod)
    {
        if (entryPointMethod.IsStatic)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(entryPointType);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create instance of class '{entryPointType.Name}': {ex.Message}", ex);
        }
    }

    private async Task<int> InvokeMethodAsync(MethodInfo entryPointMethod, object? instance, object[] methodArgs)
    {
        try
        {
            var result = entryPointMethod.Invoke(instance, methodArgs);
            return await ProcessMethodResult(result);
        }
        catch (TargetInvocationException ex)
        {
            // Unwrap the inner exception for cleaner error reporting
            throw ex.InnerException ?? ex;
        }
    }

    private async Task<int> ProcessMethodResult(object? result)
    {
        int exitCode = 0;

        // Handle async methods
        if (result is Task task)
        {
            await task;

            // Handle Task<T> return types
            if (task.GetType().IsGenericType)
            {
                var resultProperty = task.GetType().GetProperty("Result");
                var taskResult = resultProperty?.GetValue(task);

                if (taskResult != null)
                {
                    exitCode = ExtractExitCode(taskResult);
                    _logger.LogInformation("Script completed with result: {Result}", taskResult);
                }
                else
                {
                    _logger.LogInformation("Script completed successfully");
                }
            }
            else
            {
                _logger.LogInformation("Script completed successfully");
            }
        }
        else if (result != null)
        {
            exitCode = ExtractExitCode(result);
            _logger.LogInformation("Script completed with result: {Result}", result);
        }
        else
        {
            _logger.LogInformation("Script completed successfully");
        }

        return exitCode;
    }

    private int ExtractExitCode(object result)
    {
        return result switch
        {
            int intResult => intResult,
            uint uintResult => (int)Math.Min(uintResult, int.MaxValue),
            long longResult => (int)Math.Max(Math.Min(longResult, int.MaxValue), int.MinValue),
            bool boolResult => boolResult ? 0 : 1,
            _ => 0
        };
    }
}