namespace Scriptor.NugetManagement;

public interface INuGetPackageManager : IDisposable
{
    Task<List<string>> ResolvePackageReferencesAsync(IEnumerable<string> packageReferences, string packagesDirectory, string targetFramework = "net8.0");
    List<string> ParsePackageReferences(string sourceCode);
}

public record PackageReference(string Id, string? Version = null);
public record PackageDependency(string Id, string Version, string TargetFramework);
