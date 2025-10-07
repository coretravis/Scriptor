using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Scriptor.ReferenceManagement;

public class ReferenceManager : IReferenceManager
{
    private readonly ILogger<ReferenceManager> _logger;

    public ReferenceManager(ILogger<ReferenceManager> logger)
    {
        _logger = logger;
    }

    public List<string> ParseReferenceDirectives(string sourceCode)
    {
        var references = new List<string>();

        foreach (var line in sourceCode.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("// #reference:", StringComparison.OrdinalIgnoreCase))
            {
                var refName = trimmed.Replace("// #reference:", "", StringComparison.OrdinalIgnoreCase)
                                     .Trim();
                if (!string.IsNullOrEmpty(refName))
                    references.Add(refName);
            }

            if (trimmed.StartsWith("// #r:", StringComparison.OrdinalIgnoreCase))
            {
                var refName = trimmed.Replace("// #r:", "", StringComparison.OrdinalIgnoreCase)
                                     .Trim();
                if (!string.IsNullOrEmpty(refName))
                    references.Add(refName);
            }
        }

        return references;
    }

    public async Task<List<string>> ResolveReferencesAsync(List<string> references)
    {
        var resolved = new List<string>();
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();

        foreach (var refName in references)
        {
            string possiblePath = refName;

            // If user provided only filename, try resolving from runtime
            if (!File.Exists(possiblePath))
            {
                possiblePath = Path.Combine(runtimeDir, refName);
            }

            if (File.Exists(possiblePath))
            {
                resolved.Add(possiblePath);
                _logger.LogDebug("Resolved reference: {Ref}", possiblePath);
            }
            else
            {
                _logger.LogWarning("Reference not found: {Ref}", refName);
            }
        }

        return await Task.FromResult(resolved);
    }
}
