using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Scriptor.NugetManagement;
using Scriptor.ReferenceManagement;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Scriptor.Compiler;

public class DynamicCompiler
{
    private readonly ILogger<DynamicCompiler> _logger;
    private readonly INuGetPackageManager _packageManager;
    private readonly AssemblyLoadContext _loadContext;
    private readonly string _packagesDirectory;
    private readonly IReferenceManager _referenceManager;

    public DynamicCompiler(
        ILogger<DynamicCompiler> logger,
        INuGetPackageManager packageManager,
        IReferenceManager referenceManager)
    {
        _logger = logger;
        _packageManager = packageManager;
        _referenceManager = referenceManager;
        _loadContext = new AssemblyLoadContext("ScriptorDynamicCommands", isCollectible: true);

        // Create packages directory in the executing folder
        _packagesDirectory = Path.Combine(
              Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
              "scriptor", "packages"
          );

        // Log where packages are cached for transparency
        _logger.LogDebug("Package cache directory: {PackageDirectory}", _packagesDirectory);


        if (!Directory.Exists(_packagesDirectory))
        {
            Directory.CreateDirectory(_packagesDirectory);
        }
    }

    public async Task<Assembly?> CompileCommandFromSourceAsync(string sourceCode, string? assemblyName = null)
    {
        assemblyName ??= $"DynamicCommand_{Guid.NewGuid():N}";

        var referenceDirectives = _referenceManager.ParseReferenceDirectives(sourceCode);
        var customReferences = await _referenceManager.ResolveReferencesAsync(referenceDirectives);

        // Parse package references from the source code
        var packageReferences = _packageManager.ParsePackageReferences(sourceCode);

        // Resolve NuGet packages if any were found
        List<string> packageAssemblyPaths = new();
        if (packageReferences.Any())
        {
            _logger.LogInformation("Resolving {Count} NuGet package references", packageReferences.Count);
            packageAssemblyPaths = await _packageManager.ResolvePackageReferencesAsync(
                packageReferences, _packagesDirectory);
        }

        // Transform top-level statements to class structure if needed
        string transformedCode = TransformTopLevelStatements(sourceCode);

        var syntaxTree = CSharpSyntaxTree.ParseText(transformedCode);
        return await CompileSyntaxTreesAsync(new[] { syntaxTree }, assemblyName, packageAssemblyPaths.Concat(customReferences).ToList());

    }

    private string TransformTopLevelStatements(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot() as CompilationUnitSyntax;

        if (root == null)
            return sourceCode;

        // Check if there are top-level statements
        var globalStatements = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (!globalStatements.Any())
            return sourceCode; // No top-level statements, return as-is

        var result = new StringBuilder();

        // Add using statements
        foreach (var usingDirective in root.Usings)
        {
            result.AppendLine(usingDirective.ToString());
        }

        result.AppendLine();

        // Add non-global members (classes, interfaces, etc.) that come before global statements
        var nonGlobalMembers = root.Members.Where(m => !(m is GlobalStatementSyntax)).ToList();

        // Create a Program class with Main method
        result.AppendLine("public class Program");
        result.AppendLine("{");
        result.AppendLine("    public static void Main(string[] args)");
        result.AppendLine("    {");

        // Add the global statements inside Main
        foreach (var globalStatement in globalStatements)
        {
            var statementText = globalStatement.Statement.ToString();
            // Indent the statement
            var lines = statementText.Split('\n');
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    result.AppendLine("        " + line.TrimStart());
                else
                    result.AppendLine();
            }
        }

        result.AppendLine("    }");
        result.AppendLine("}");
        result.AppendLine();

        // Add other type declarations outside the Program class
        foreach (var member in nonGlobalMembers)
        {
            result.AppendLine(member.ToString());
            result.AppendLine();
        }

        return result.ToString();
    }

    private async Task<Assembly?> CompileSyntaxTreesAsync(
        IEnumerable<SyntaxTree> syntaxTrees,
        string assemblyName,
        List<string> packageAssemblyPaths)
    {
        var references = await GetRequiredReferencesAsync(packageAssemblyPaths);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false
            )
        );

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            _logger.LogError("Compilation failed:");
            foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                _logger.LogError("  {Location}: {Message}",
                    diagnostic.Location.GetLineSpan(),
                    diagnostic.GetMessage());
            }
            return null;
        }

        ms.Seek(0, SeekOrigin.Begin);

        // Load package assemblies into the load context first
        foreach (var packageAssemblyPath in packageAssemblyPaths)
        {
            try
            {
                _loadContext.LoadFromAssemblyPath(packageAssemblyPath);
                _logger.LogDebug("Loaded package assembly: {Assembly}", packageAssemblyPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load package assembly: {Assembly}", packageAssemblyPath);
            }
        }

        var assembly = _loadContext.LoadFromStream(ms);

        _logger.LogInformation("Successfully compiled assembly: {AssemblyName}", assemblyName);
        return assembly;
    }

    private async Task<MetadataReference[]> GetRequiredReferencesAsync(List<string> packageAssemblyPaths)
    {
        // Get references to assemblies that commands typically need
        var references = new List<MetadataReference>
        {
            // System assemblies
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEnumerable<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.ComponentModel.DataAnnotations.ValidationAttribute).Assembly.Location),
        };

        // Add Microsoft.Extensions references if available
        try
        {
            references.Add(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(ILogger).Assembly.Location));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Some Microsoft.Extensions assemblies not available: {Message}", ex.Message);
        }

        // Add NuGet package assemblies
        foreach (var packageAssemblyPath in packageAssemblyPaths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(packageAssemblyPath));
                _logger.LogDebug("Added package reference: {Assembly}", packageAssemblyPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add package reference: {Assembly}", packageAssemblyPath);
            }
        }

        // Add all currently loaded assemblies that might be needed
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not add reference to assembly {Assembly}: {Message}",
                    assembly.FullName, ex.Message);
            }
        }

        return references.Distinct().ToArray();
    }

    public void Dispose()
    {
        _loadContext?.Unload();
    }
}