using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Scriptor.NugetManagement;

public class NuGetPackageManager : INuGetPackageManager
{
    private readonly ILogger<NuGetPackageManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly ConcurrentDictionary<string, Task<List<string>>> _downloadTasks;
    private readonly ConcurrentDictionary<string, List<string>> _packageCache;
    private bool _disposed;

    private const string NuGetApiUrl = "https://api.nuget.org/v3-flatcontainer";
    private const string NuGetSearchUrl = "https://azuresearch-usnc.nuget.org/query";

    // Framework compatibility matrix (simplified)
    private static readonly Dictionary<string, int> FrameworkPriority = new()
    {
        ["net9.0"] = 900,
        ["net8.0"] = 800,
        ["net7.0"] = 700,
        ["net6.0"] = 600,
        ["net5.0"] = 500,
        ["netcoreapp3.1"] = 310,
        ["netcoreapp3.0"] = 300,
        ["netstandard2.1"] = 210,
        ["netstandard2.0"] = 200,
        ["netstandard1.6"] = 160,
        ["netstandard1.5"] = 150,
        ["netstandard1.4"] = 140,
        ["netstandard1.3"] = 130,
        ["netstandard1.2"] = 120,
        ["netstandard1.1"] = 110,
        ["netstandard1.0"] = 100,
        ["net48"] = 480,
        ["net472"] = 472,
        ["net471"] = 471,
        ["net47"] = 470,
        ["net462"] = 462,
        ["net461"] = 461,
        ["net46"] = 460,
        ["net452"] = 452,
        ["net451"] = 451,
        ["net45"] = 450,
        ["net40"] = 400,
        ["net35"] = 350,
        ["net20"] = 200
    };

    public NuGetPackageManager(ILogger<NuGetPackageManager> logger, HttpClient httpClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        _downloadTasks = new ConcurrentDictionary<string, Task<List<string>>>();
        _packageCache = new ConcurrentDictionary<string, List<string>>();
    }

    public List<string> ParsePackageReferences(string sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return new List<string>();

        var packageReferences = new List<string>();

        // Match patterns like:
        // // #nuget: Newtonsoft.Json
        // // #nuget: Newtonsoft.Json@13.0.3
        // // #package: Serilog@2.12.0
        var pattern = @"//\s*#(?:nuget|package):\s*([^\s@]+)(?:@([^\s]+))?";
        var matches = Regex.Matches(sourceCode, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);

        foreach (Match match in matches)
        {
            var packageName = match.Groups[1].Value.Trim();
            var version = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;

            if (!string.IsNullOrEmpty(packageName))
            {
                packageReferences.Add(string.IsNullOrEmpty(version) ? packageName : $"{packageName}@{version}");
            }
        }

        return packageReferences.Distinct().ToList();
    }

    public async Task<List<string>> ResolvePackageReferencesAsync(
        IEnumerable<string> packageReferences,
        string packagesDirectory,
        string targetFramework = "net8.0")
    {
        if (packageReferences == null)
            throw new ArgumentNullException(nameof(packageReferences));
        if (string.IsNullOrWhiteSpace(packagesDirectory))
            throw new ArgumentException("Packages directory cannot be null or empty", nameof(packagesDirectory));

        var assemblyPaths = new List<string>();
        var resolvedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(packagesDirectory))
        {
            Directory.CreateDirectory(packagesDirectory);
        }

        // Resolve all packages including dependencies
        var allPackages = await ResolveAllPackagesWithDependenciesAsync(packageReferences, targetFramework);

        foreach (var package in allPackages)
        {
            if (resolvedPackages.Contains($"{package.Id}@{package.Version}"))
                continue;

            try
            {
                var paths = await ResolvePackageAsync(package.Id, package.Version, packagesDirectory, targetFramework);
                assemblyPaths.AddRange(paths);
                resolvedPackages.Add($"{package.Id}@{package.Version}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve package: {PackageId}@{Version}", package.Id, package.Version);
            }
        }

        return assemblyPaths.Distinct().ToList();
    }

    private async Task<List<PackageReference>> ResolveAllPackagesWithDependenciesAsync(
        IEnumerable<string> packageReferences,
        string targetFramework)
    {
        var resolvedPackages = new List<PackageReference>();
        var processedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageQueue = new Queue<PackageReference>();

        // Parse initial package references
        foreach (var packageRef in packageReferences)
        {
            var (packageId, version) = ParsePackageReference(packageRef);
            if (string.IsNullOrEmpty(version))
            {
                version = await GetLatestVersionAsync(packageId);
            }
            packageQueue.Enqueue(new PackageReference(packageId, version));
        }

        // Process packages and their dependencies
        while (packageQueue.Count > 0)
        {
            var package = packageQueue.Dequeue();
            var packageKey = $"{package.Id}@{package.Version}";

            if (processedPackages.Contains(packageKey))
                continue;

            processedPackages.Add(packageKey);
            resolvedPackages.Add(package);

            // Get dependencies for this package
            try
            {
                var dependencies = await GetPackageDependenciesAsync(package.Id, package.Version, targetFramework);
                foreach (var dep in dependencies)
                {
                    var depKey = $"{dep.Id}@{dep.Version}";
                    if (!processedPackages.Contains(depKey))
                    {
                        packageQueue.Enqueue(new PackageReference(dep.Id, dep.Version));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve dependencies for {PackageId}@{Version}", package.Id, package.Version);
            }
        }

        return resolvedPackages;
    }

    private async Task<List<PackageDependency>> GetPackageDependenciesAsync(string packageId, string version, string targetFramework)
    {
        var dependencies = new List<PackageDependency>();

        try
        {
            // Download package to read .nuspec file
            var packageUrl = $"{NuGetApiUrl}/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}/{packageId.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg";
            var packageBytes = await _httpClient.GetByteArrayAsync(packageUrl);

            using var packageStream = new MemoryStream(packageBytes);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);

            // Find .nuspec file
            var nuspecEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry != null)
            {
                using var nuspecStream = nuspecEntry.Open();
                var nuspecDoc = await XDocument.LoadAsync(nuspecStream, LoadOptions.None, CancellationToken.None);

                var ns = nuspecDoc.Root?.GetDefaultNamespace();
                var dependenciesElement = nuspecDoc.Root?.Element(ns + "metadata")?.Element(ns + "dependencies");

                if (dependenciesElement != null)
                {
                    // Handle group dependencies (framework-specific)
                    var groups = dependenciesElement.Elements(ns + "group");
                    foreach (var group in groups)
                    {
                        var groupFramework = group.Attribute("targetFramework")?.Value;
                        if (IsCompatibleFramework(groupFramework, targetFramework))
                        {
                            var deps = group.Elements(ns + "dependency");
                            foreach (var dep in deps)
                            {
                                var depId = dep.Attribute("id")?.Value;
                                var depVersion = dep.Attribute("version")?.Value ?? await GetLatestVersionAsync(depId);
                                if (!string.IsNullOrEmpty(depId))
                                {
                                    dependencies.Add(new PackageDependency(depId, depVersion, groupFramework));
                                }
                            }
                        }
                    }

                    // Handle direct dependencies (no groups)
                    if (!groups.Any())
                    {
                        var deps = dependenciesElement.Elements(ns + "dependency");
                        foreach (var dep in deps)
                        {
                            var depId = dep.Attribute("id")?.Value;
                            var depVersion = dep.Attribute("version")?.Value ?? await GetLatestVersionAsync(depId);
                            if (!string.IsNullOrEmpty(depId))
                            {
                                dependencies.Add(new PackageDependency(depId, depVersion, targetFramework));
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read dependencies for {PackageId}@{Version}", packageId, version);
        }

        return dependencies;
    }

    private async Task<List<string>> ResolvePackageAsync(string packageId, string version, string packagesDirectory, string targetFramework)
    {
        var cacheKey = $"{packageId}@{version}|{packagesDirectory}|{targetFramework}";

        return await _downloadTasks.GetOrAdd(cacheKey, async _ =>
        {
            if (_packageCache.TryGetValue(cacheKey, out var cachedPaths))
            {
                return cachedPaths;
            }

            await _downloadSemaphore.WaitAsync();
            try
            {
                return await ActuallyResolvePackageAsync(packageId, version, packagesDirectory, targetFramework);
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        });
    }

    private async Task<List<string>> ActuallyResolvePackageAsync(string packageId, string version, string packagesDirectory, string targetFramework)
    {
        var packageDir = Path.Combine(packagesDirectory, packageId.ToLowerInvariant(), version.ToLowerInvariant());
        var cacheKey = $"{packageId}@{version}|{packagesDirectory}|{targetFramework}";

        // Check if package is already downloaded and validated
        if (Directory.Exists(packageDir) && ValidatePackageIntegrity(packageDir))
        {
            var existingAssemblies = FindAssembliesInPackage(packageDir, targetFramework);
            if (existingAssemblies.Count > 0)
            {
                _logger.LogDebug("Package {PackageId}@{Version} already exists locally", packageId, version);
                _packageCache[cacheKey] = existingAssemblies;
                return existingAssemblies;
            }
        }

        _logger.LogInformation("Downloading package {PackageId}@{Version}", packageId, version);

        // Download and extract package
        await DownloadAndExtractPackageAsync(packageId, version, packageDir);

        // Validate downloaded package
        if (!ValidatePackageIntegrity(packageDir))
        {
            Directory.Delete(packageDir, true);
            throw new InvalidOperationException($"Package {packageId}@{version} failed integrity validation");
        }

        // Find and return assembly paths
        var assemblyPaths = FindAssembliesInPackage(packageDir, targetFramework);
        _logger.LogInformation("Resolved {Count} assemblies for package {PackageId}@{Version}", assemblyPaths.Count, packageId, version);

        _packageCache[cacheKey] = assemblyPaths;
        return assemblyPaths;
    }

    private (string packageId, string version) ParsePackageReference(string packageReference)
    {
        if (string.IsNullOrWhiteSpace(packageReference))
            throw new ArgumentException("Package reference cannot be null or empty", nameof(packageReference));

        var parts = packageReference.Split('@', 2);
        return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null);
    }

    private async Task<string> GetLatestVersionAsync(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new ArgumentException("Package ID cannot be null or empty", nameof(packageId));

        try
        {
            var searchUrl = $"{NuGetSearchUrl}?q=packageid:{packageId.ToLowerInvariant()}&take=1";
            var response = await _httpClient.GetStringAsync(searchUrl);

            using var doc = JsonDocument.Parse(response);
            var data = doc.RootElement.GetProperty("data");

            if (data.GetArrayLength() > 0)
            {
                var package = data[0];
                var version = package.GetProperty("version").GetString();
                _logger.LogDebug("Latest version for {PackageId}: {Version}", packageId, version);
                return version;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get latest version for {PackageId} from search API, trying fallback", packageId);
        }

        // Fallback: try to get version from package index
        try
        {
            var indexUrl = $"{NuGetApiUrl}/{packageId.ToLowerInvariant()}/index.json";
            var response = await _httpClient.GetStringAsync(indexUrl);

            using var doc = JsonDocument.Parse(response);
            var versions = doc.RootElement.GetProperty("versions");

            if (versions.GetArrayLength() > 0)
            {
                // Get the last version (should be latest)
                var lastIndex = versions.GetArrayLength() - 1;
                var version = versions[lastIndex].GetString();
                _logger.LogDebug("Latest version for {PackageId} from index: {Version}", packageId, version);
                return version;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve latest version for {PackageId}", packageId);
        }

        throw new InvalidOperationException($"Could not resolve latest version for package {packageId}");
    }

    private async Task DownloadAndExtractPackageAsync(string packageId, string version, string packageDir)
    {
        var packageUrl = $"{NuGetApiUrl}/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}/{packageId.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg";

        // Create package directory
        if (Directory.Exists(packageDir))
        {
            Directory.Delete(packageDir, true);
        }
        Directory.CreateDirectory(packageDir);

        try
        {
            // Download package
            var packageBytes = await _httpClient.GetByteArrayAsync(packageUrl);

            // Calculate and store hash for integrity validation
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(packageBytes);
                var hashFile = Path.Combine(packageDir, ".package.hash");
                await File.WriteAllTextAsync(hashFile, Convert.ToBase64String(hash));
            }

            // Extract package (nupkg files are zip files)
            using var packageStream = new MemoryStream(packageBytes);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                // Security check: prevent directory traversal
                var destinationPath = Path.GetFullPath(Path.Combine(packageDir, entry.FullName));
                if (!destinationPath.StartsWith(packageDir, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Skipping potentially dangerous file path: {Path}", entry.FullName);
                    continue;
                }

                if (entry.Name == string.Empty)
                {
                    // Directory entry
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    // File entry
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                    entry.ExtractToFile(destinationPath, true);
                }
            }

            _logger.LogDebug("Extracted package {PackageId}@{Version} to {Directory}", packageId, version, packageDir);
        }
        catch (Exception ex)
        {
            // Clean up on failure
            if (Directory.Exists(packageDir))
            {
                try
                {
                    Directory.Delete(packageDir, true);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up package directory after failed download: {Directory}", packageDir);
                }
            }
            throw new InvalidOperationException($"Failed to download and extract package {packageId}@{version}", ex);
        }
    }

    private bool ValidatePackageIntegrity(string packageDirectory)
    {
        try
        {
            var hashFile = Path.Combine(packageDirectory, ".package.hash");
            if (!File.Exists(hashFile))
            {
                _logger.LogDebug("No hash file found for package validation in {Directory}", packageDirectory);
                return false;
            }

            // Basic validation - check if key files exist
            var nuspecFiles = Directory.GetFiles(packageDirectory, "*.nuspec", SearchOption.TopDirectoryOnly);
            if (nuspecFiles.Length == 0)
            {
                _logger.LogWarning("No .nuspec file found in package directory {Directory}", packageDirectory);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Package validation failed for {Directory}", packageDirectory);
            return false;
        }
    }

    private List<string> FindAssembliesInPackage(string packageDirectory, string targetFramework)
    {
        var assemblyPaths = new List<string>();

        if (!Directory.Exists(packageDirectory))
            return assemblyPaths;

        // Look for assemblies in framework-specific locations first
        var libPath = Path.Combine(packageDirectory, "lib");
        var refPath = Path.Combine(packageDirectory, "ref");

        var compatibleAssemblies = new List<(string path, int priority)>();

        // Search in lib directory
        if (Directory.Exists(libPath))
        {
            var frameworkDirs = Directory.GetDirectories(libPath);
            foreach (var frameworkDir in frameworkDirs)
            {
                var framework = Path.GetFileName(frameworkDir).ToLowerInvariant();
                if (IsCompatibleFramework(framework, targetFramework))
                {
                    var priority = GetFrameworkPriority(framework, targetFramework);
                    var dlls = Directory.GetFiles(frameworkDir, "*.dll", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));

                    foreach (var dll in dlls)
                    {
                        compatibleAssemblies.Add((dll, priority));
                    }
                }
            }
        }

        // Search in ref directory (reference assemblies)
        if (Directory.Exists(refPath))
        {
            var frameworkDirs = Directory.GetDirectories(refPath);
            foreach (var frameworkDir in frameworkDirs)
            {
                var framework = Path.GetFileName(frameworkDir).ToLowerInvariant();
                if (IsCompatibleFramework(framework, targetFramework))
                {
                    var priority = GetFrameworkPriority(framework, targetFramework);
                    var dlls = Directory.GetFiles(frameworkDir, "*.dll", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));

                    foreach (var dll in dlls)
                    {
                        // Prefer ref assemblies over lib assemblies (higher priority)
                        compatibleAssemblies.Add((dll, priority + 1000));
                    }
                }
            }
        }

        // If no framework-specific assemblies found, look in root lib directory
        if (compatibleAssemblies.Count == 0 && Directory.Exists(libPath))
        {
            var dlls = Directory.GetFiles(libPath, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));

            foreach (var dll in dlls)
            {
                compatibleAssemblies.Add((dll, 0));
            }
        }

        // Return the highest priority assemblies, avoiding duplicates by assembly name
        var uniqueAssemblies = compatibleAssemblies
            .GroupBy(a => Path.GetFileNameWithoutExtension(a.path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.priority).First())
            .Select(a => a.path)
            .ToList();

        return uniqueAssemblies;
    }

    private bool IsCompatibleFramework(string packageFramework, string targetFramework)
    {
        if (string.IsNullOrEmpty(packageFramework) || string.IsNullOrEmpty(targetFramework))
            return false;

        packageFramework = NormalizeFrameworkName(packageFramework);
        targetFramework = NormalizeFrameworkName(targetFramework);

        // Exact match
        if (packageFramework.Equals(targetFramework, StringComparison.OrdinalIgnoreCase))
            return true;

        // Get framework priorities
        var packagePriority = FrameworkPriority.GetValueOrDefault(packageFramework, -1);
        var targetPriority = FrameworkPriority.GetValueOrDefault(targetFramework, -1);

        if (packagePriority == -1 || targetPriority == -1)
            return false;

        // .NET Standard compatibility
        if (packageFramework.StartsWith("netstandard") && targetFramework.StartsWith("net"))
        {
            // .NET Standard 2.0 is compatible with .NET Core 2.0+ and .NET Framework 4.6.1+
            if (packageFramework == "netstandard2.0")
                return targetPriority >= FrameworkPriority.GetValueOrDefault("netcoreapp2.0", 0) ||
                       targetPriority >= FrameworkPriority.GetValueOrDefault("net461", 0);

            // .NET Standard 2.1 is compatible with .NET Core 3.0+
            if (packageFramework == "netstandard2.1")
                return targetPriority >= FrameworkPriority.GetValueOrDefault("netcoreapp3.0", 0);
        }

        // Package framework should be same or lower version for compatibility
        return packagePriority <= targetPriority &&
               GetFrameworkFamily(packageFramework) == GetFrameworkFamily(targetFramework);
    }

    private string NormalizeFrameworkName(string framework)
    {
        if (string.IsNullOrEmpty(framework))
            return framework;

        framework = framework.ToLowerInvariant();

        // Handle common variations
        if (framework.StartsWith("netcoreapp"))
            return framework;
        if (framework.StartsWith("netstandard"))
            return framework;
        if (framework.StartsWith("net") && framework.Length > 3 && char.IsDigit(framework[3]))
        {
            // Handle net5.0, net6.0, etc.
            if (framework.Contains('.'))
                return framework;
            // Handle net50, net60, etc.
            if (framework.Length == 5)
                return framework.Insert(4, ".");
        }

        return framework;
    }

    private string GetFrameworkFamily(string framework)
    {
        if (framework.StartsWith("netstandard")) return "netstandard";
        if (framework.StartsWith("netcoreapp")) return "netcore";
        if (framework.StartsWith("net") && (framework.Contains('.') || framework.Length > 5)) return "netcore";
        if (framework.StartsWith("net")) return "netfx";
        return "unknown";
    }

    private int GetFrameworkPriority(string packageFramework, string targetFramework)
    {
        var packagePriority = FrameworkPriority.GetValueOrDefault(NormalizeFrameworkName(packageFramework), 0);
        var targetPriority = FrameworkPriority.GetValueOrDefault(NormalizeFrameworkName(targetFramework), 0);

        // Prefer exact matches
        if (packageFramework.Equals(targetFramework, StringComparison.OrdinalIgnoreCase))
            return packagePriority + 10000;

        // Prefer higher versions within the same family
        return packagePriority;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _downloadSemaphore?.Dispose();
                _httpClient?.Dispose();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}