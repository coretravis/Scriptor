# Scriptor - Dynamic C# Script Compiler and Runner

Scriptor is a powerful command-line tool that allows you to compile and execute C# scripts dynamically without the need for a full project setup. It automatically handles NuGet package dependencies, provides flexible entry point detection, and supports modern .NET frameworks.

## Features

- **Dynamic C# Script Execution**: Run C# scripts without creating a full project
- **Automatic NuGet Package Management**: Reference NuGet packages directly in your scripts using comments
- **Flexible Entry Point Detection**: Automatically detects `Main`, `Run`, or `Execute` methods
- **Framework Compatibility**: Supports multiple .NET frameworks with intelligent compatibility resolution
- **Dependency Resolution**: Automatically resolves and downloads package dependencies
- **Assembly Isolation**: Uses collectible assembly load contexts for clean resource management
- **Comprehensive Logging**: Configurable logging levels for debugging and monitoring

## Installation

```bash
# Install from NuGet (example)
dotnet tool install -g Scriptor.Console

# Or build from source
git clone <repository-url>
cd Scriptor
dotnet build
dotnet pack -o ./nupkg
dotnet tool install -g --add-source ./nupkg Scriptor.Console
```

## Quick Start

### Basic Script Execution

Create a simple C# script file (`hello.cs`):

```csharp
using System;

public class HelloWorld
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Hello, World!");
        Console.WriteLine($"Received {args.Length} arguments");
        foreach (var arg in args)
        {
            Console.WriteLine($"  - {arg}");
        }
    }
}
```

Run the script:

```bash
scriptor hello.cs
```

### Using NuGet Packages

Create a script with NuGet package dependencies (`json-example.cs`):

```csharp
// #nuget: Newtonsoft.Json
// #nuget: Serilog@2.12.0

using System;
using Newtonsoft.Json;
using Serilog;

public class JsonExample
{
    public static void Main()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        var data = new { Name = "John", Age = 30 };
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        
        Log.Information("Generated JSON: {Json}", json);
        Console.WriteLine(json);
    }
}
```

Run the script (packages will be automatically downloaded):

```bash
scriptor json-example.cs --verbose
```

## Command Line Usage

```bash
scriptor <script-file> [options]
```

### Arguments

- `script-file`: Path to the C# script file to compile and run (required)

### Options

- `--method <method-name>`: Specify the entry point method name (default: auto-detect)
- `--class <class-name>`: Specify the class containing the entry point (default: auto-detect)
- `--framework <framework>`: Target framework version (default: net8.0)
- `--verbose`: Enable verbose logging for debugging
- `--args <arguments>`: Arguments to pass to the script (space-separated)

### Examples

```bash
# Basic execution
scriptor myscript.cs

# Specify entry point method
scriptor myscript.cs --method Run

# Specify class and method
scriptor myscript.cs --class MyApp --method Execute

# Pass arguments to script
scriptor myscript.cs --args arg1 arg2 "argument with spaces"

# Use different framework
scriptor myscript.cs --framework net6.0

# Enable verbose logging
scriptor myscript.cs --verbose
```

## NuGet Package Management

### Package Reference Syntax

Reference NuGet packages in your scripts using comment directives:

```csharp
// #nuget: PackageName
// #nuget: PackageName@Version
// #package: PackageName@Version
```

Examples:
```csharp
// #nuget: Newtonsoft.Json
// #nuget: Serilog@2.12.0
// #package: Microsoft.Extensions.Http@7.0.0
```

### Supported Package Features

- **Automatic Version Resolution**: If no version is specified, the latest version is used
- **Dependency Resolution**: All package dependencies are automatically resolved and downloaded
- **Framework Compatibility**: Packages are selected based on target framework compatibility
- **Assembly Loading**: Package assemblies are loaded in an isolated context
- **Caching**: Downloaded packages are cached locally for faster subsequent runs

### Framework Compatibility

Scriptor supports intelligent framework compatibility resolution:

- **.NET 5+**: net5.0, net6.0, net7.0, net8.0, net9.0
- **.NET Core**: netcoreapp2.0, netcoreapp2.1, netcoreapp3.0, netcoreapp3.1
- **.NET Standard**: netstandard1.0 through netstandard2.1
- **.NET Framework**: net35, net40, net45, net451, net452, net46, net461, net462, net47, net471, net472, net48

## Entry Point Detection

Scriptor automatically detects entry points using the following priority order:

1. **Specified Method**: If `--method` is provided, looks for that method
2. **Common Methods**: Searches for methods named `Main`, `Run`, or `Execute`
3. **Static Methods**: Prefers static methods over instance methods
4. **Method Signature**: Supports various parameter combinations:
   - `void Method()`
   - `void Method(string[] args)`
   - `Task Method()`
   - `Task Method(string[] args)`
   - `int Method(string[] args)`

### Supported Entry Point Patterns

```csharp
// Static void Main
public static void Main(string[] args) { }

// Async Main
public static async Task Main(string[] args) { }

// Return code Main
public static int Main(string[] args) { return 0; }

// Custom method names
public static void Run() { }
public static async Task Execute(string[] args) { }

// Instance methods (requires parameterless constructor)
public void Main() { }
public async Task Run(string[] args) { }
```

## Script Examples

### Simple Console Application

```csharp
using System;

public class SimpleApp
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Simple console application");
        if (args.Length > 0)
        {
            Console.WriteLine($"First argument: {args[0]}");
        }
    }
}
```

### HTTP Client Example

```csharp
// #nuget: Microsoft.Extensions.Http

using System;
using System.Net.Http;
using System.Threading.Tasks;

public class HttpExample
{
    public static async Task Main()
    {
        using var client = new HttpClient();
        var response = await client.GetStringAsync("https://api.github.com/users/octocat");
        Console.WriteLine(response);
    }
}
```

### JSON Processing with Logging

```csharp
// #nuget: Newtonsoft.Json
// #nuget: Serilog
// #nuget: Serilog.Sinks.Console

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Serilog;

public class JsonProcessor
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        var data = new List<object>
        {
            new { Id = 1, Name = "Alice", Age = 30 },
            new { Id = 2, Name = "Bob", Age = 25 },
            new { Id = 3, Name = "Charlie", Age = 35 }
        };

        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        
        Log.Information("Processing {Count} records", data.Count);
        Console.WriteLine(json);
        
        Log.Information("JSON processing completed");
    }
}
```

### File Processing Example

```csharp
// #nuget: System.IO.Abstractions

using System;
using System.IO;
using System.Threading.Tasks;

public class FileProcessor
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: scriptor file-processor.cs --args <file-path>");
            return;
        }

        var filePath = args[0];
        
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }

        var content = await File.ReadAllTextAsync(filePath);
        var lines = content.Split('\n');
        
        Console.WriteLine($"File: {filePath}");
        Console.WriteLine($"Lines: {lines.Length}");
        Console.WriteLine($"Characters: {content.Length}");
        Console.WriteLine($"Words: {content.Split(' ', '\n', '\r', '\t').Length}");
    }
}
```

## Architecture

### Core Components

1. **Program**: Main entry point and command-line interface
2. **DynamicCompiler**: Handles C# source code compilation using Roslyn
3. **NuGetPackageManager**: Manages NuGet package resolution and downloading
4. **Assembly Execution**: Dynamic method invocation with parameter mapping

### Package Management Flow

1. **Parse Package References**: Extract `#nuget` comments from source code
2. **Resolve Versions**: Get latest versions for unspecified package versions
3. **Dependency Resolution**: Recursively resolve all package dependencies
4. **Download Packages**: Download and extract NuGet packages to local cache
5. **Assembly Selection**: Choose compatible assemblies based on target framework
6. **Load Assemblies**: Load assemblies into isolated load context

### Compilation Process

1. **Source Parsing**: Parse C# source code into syntax trees
2. **Reference Resolution**: Collect metadata references from system and NuGet assemblies
3. **Compilation**: Use Roslyn to compile source code to in-memory assembly
4. **Assembly Loading**: Load compiled assembly into execution context
5. **Entry Point Discovery**: Find and validate entry point method
6. **Execution**: Invoke entry point with appropriate parameters

## Configuration

### Framework Targeting

Specify different target frameworks:

```bash
# Target .NET 6.0
scriptor script.cs --framework net6.0

# Target .NET Core 3.1
scriptor script.cs --framework netcoreapp3.1

# Target .NET Framework 4.8
scriptor script.cs --framework net48
```

### Logging Configuration

Control logging verbosity:

```bash
# Minimal logging (default)
scriptor script.cs

# Verbose logging for debugging
scriptor script.cs --verbose
```

### Package Cache

Packages are cached in the application directory under `packages/`:
```
packages/
├── newtonsoft.json/
│   └── 13.0.3/
├── serilog/
│   └── 2.12.0/
└── microsoft.extensions.http/
    └── 7.0.0/
```

## Error Handling

Scriptor provides comprehensive error handling for common scenarios:

- **File Not Found**: Clear error when script file doesn't exist
- **Compilation Errors**: Detailed compilation error messages with line numbers
- **Package Resolution Failures**: Specific errors for failed package downloads
- **Entry Point Issues**: Helpful messages when entry points can't be found
- **Runtime Exceptions**: Clean error reporting with optional stack traces

## Performance Considerations

- **Package Caching**: Downloaded packages are cached to avoid re-downloading
- **Concurrent Downloads**: Multiple packages are downloaded concurrently
- **Assembly Isolation**: Uses collectible assembly load contexts to prevent memory leaks
- **Optimized Compilation**: Uses release-mode compilation for better performance

## Limitations

- **Security**: Scripts run with full trust - use caution with untrusted scripts
- **Platform Dependencies**: Some NuGet packages may have platform-specific requirements
- **Framework Compatibility**: Not all packages are compatible with all frameworks
- **Resource Cleanup**: Long-running scripts should properly dispose of resources

## Troubleshooting

### Common Issues

1. **Package Not Found**
   ```
   Error: Could not resolve latest version for package PackageName
   ```
   - Check package name spelling
   - Verify package exists on NuGet.org
   - Try specifying a specific version

2. **Compilation Errors**
   ```
   Compilation failed:
   (10,15): error CS0246: The type or namespace name 'JsonConvert' could not be found
   ```
   - Ensure all required `using` statements are included
   - Verify NuGet package references are correct
   - Check for typos in class/method names

3. **Entry Point Not Found**
   ```
   No suitable entry point method found in class 'MyClass'
   ```
   - Ensure your class has a `Main`, `Run`, or `Execute` method
   - Use `--method` option to specify custom entry point
   - Check method visibility (should be public)

4. **Framework Compatibility**
   ```
   No compatible assemblies found for target framework
   ```
   - Try a different target framework
   - Check package documentation for supported frameworks
   - Use `--verbose` to see detailed compatibility information

### Debug Mode

Use verbose logging to diagnose issues:

```bash
scriptor script.cs --verbose
```

This provides detailed information about:
- Package resolution process
- Assembly loading
- Compilation diagnostics
- Entry point detection
- Execution flow

## Contributing

Contributions are welcome! Please see the contributing guidelines for details on:
- Code style and formatting
- Testing requirements
- Pull request process
- Issue reporting

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Changelog

### Version 1.0.0
- Initial release
- Dynamic C# script compilation and execution
- NuGet package management with dependency resolution
- Automatic entry point detection
- Multiple framework support
- Comprehensive logging and error handling