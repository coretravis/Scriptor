namespace Scriptor.Runner;

public class ScriptRunnerConfig
{
    public string ScriptFile { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? ClassName { get; set; }
    public string Framework { get; set; } = "net8.0";
    public bool Verbose { get; set; }
    public string[] ScriptArgs { get; set; } = Array.Empty<string>();
}
