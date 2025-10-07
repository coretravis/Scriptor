namespace Scriptor.ReferenceManagement;

public interface IReferenceManager
{
    List<string> ParseReferenceDirectives(string sourceCode);
    Task<List<string>> ResolveReferencesAsync(List<string> references);
}
