using Aevatar.Scripting.Core.Compilation;
using Google.Protobuf;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class GrpcToolsScriptProtoCompiler : IScriptProtoCompiler
{
    private static readonly Regex UnsupportedWrapperPattern = new(
        "wrappers\\.proto|google\\.protobuf\\.(StringValue|BoolValue|Int32Value|Int64Value|UInt32Value|UInt64Value|DoubleValue|FloatValue|BytesValue)",
        RegexOptions.Compiled);

    public ScriptProtoCompilationResult Compile(ScriptBehaviorCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CompilePackage(request.Package);
    }

    private static ScriptProtoCompilationResult CompilePackage(ScriptSourcePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var normalized = package.Normalize();
        if (normalized.ProtoFiles.Count == 0)
            return ScriptProtoCompilationResult.Empty;

        var wrapperDiagnostics = ValidateUnsupportedWrapperUsage(normalized.ProtoFiles);
        if (wrapperDiagnostics.Count > 0)
            return new ScriptProtoCompilationResult(false, Array.Empty<ScriptSourceFile>(), ByteString.Empty, wrapperDiagnostics);

        var diagnostics = new List<string>();
        var packageRoot = Path.Combine(Path.GetTempPath(), "aevatar-script-proto", Guid.NewGuid().ToString("N"));
        var protoRoot = Path.Combine(packageRoot, "proto");
        var builtinRoot = Path.Combine(packageRoot, "builtin");
        var csharpOut = Path.Combine(packageRoot, "csharp");
        var descriptorOut = Path.Combine(packageRoot, "descriptor");
        Directory.CreateDirectory(protoRoot);
        Directory.CreateDirectory(builtinRoot);
        Directory.CreateDirectory(csharpOut);
        Directory.CreateDirectory(descriptorOut);

        try
        {
            foreach (var file in normalized.ProtoFiles)
            {
                var path = Path.Combine(protoRoot, file.NormalizedPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content ?? string.Empty, Encoding.UTF8);
            }

            File.WriteAllText(
                Path.Combine(builtinRoot, ScriptBuiltInProtoSources.ScriptingSchemaOptionsFileName),
                ScriptBuiltInProtoSources.ScriptingSchemaOptionsContent,
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(builtinRoot, ScriptBuiltInProtoSources.ScriptingRuntimeOptionsFileName),
                ScriptBuiltInProtoSources.ScriptingRuntimeOptionsContent,
                Encoding.UTF8);

            var descriptorPath = Path.Combine(descriptorOut, "script-package.desc");
            var protocPath = ResolveProtocPath();
            var wellKnownRoot = ResolveWellKnownProtoRoot();
            var protoArgs = normalized.ProtoFiles
                .Select(file => Quote(Path.Combine(protoRoot, file.NormalizedPath)))
                .ToArray();
            var args = string.Join(
                " ",
                new[]
                {
                    "--proto_path=" + Quote(protoRoot),
                    "--proto_path=" + Quote(builtinRoot),
                    "--proto_path=" + Quote(wellKnownRoot),
                    "--csharp_out=" + Quote(csharpOut),
                    "--descriptor_set_out=" + Quote(descriptorPath),
                    "--include_imports",
                }.Concat(protoArgs));

            var startInfo = new ProcessStartInfo
            {
                FileName = protocPath,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = protoRoot,
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start protoc at `{protocPath}`.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                diagnostics.AddRange(
                    string.Concat(stdout, Environment.NewLine, stderr)
                        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (diagnostics.Count == 0)
                    diagnostics.Add($"protoc exited with code {process.ExitCode}.");
                return new ScriptProtoCompilationResult(false, Array.Empty<ScriptSourceFile>(), ByteString.Empty, diagnostics);
            }

            var generatedSources = Directory.GetFiles(csharpOut, "*.cs", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(path => new ScriptSourceFile(
                    Path.GetRelativePath(csharpOut, path).Replace('\\', '/'),
                    File.ReadAllText(path, Encoding.UTF8)))
                .ToArray();
            var descriptorSet = File.Exists(descriptorPath)
                ? ByteString.CopyFrom(File.ReadAllBytes(descriptorPath))
                : ByteString.Empty;

            return new ScriptProtoCompilationResult(true, generatedSources, descriptorSet, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            diagnostics.Add(ex.Message);
            return new ScriptProtoCompilationResult(false, Array.Empty<ScriptSourceFile>(), ByteString.Empty, diagnostics);
        }
        finally
        {
            try
            {
                if (Directory.Exists(packageRoot))
                    Directory.Delete(packageRoot, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup only.
            }
        }
    }

    private static string ResolveProtocPath()
    {
        var systemProtoc = FindSystemProtoc();
        if (!string.IsNullOrWhiteSpace(systemProtoc))
            return systemProtoc;

        var basePath = ResolveGrpcToolsPackageRoot();
        var relative = GetToolRelativePath("protoc");
        var path = Path.Combine(basePath, relative);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Grpc.Tools protoc binary was not found at `{path}`.");
        return path;
    }

    private static string ResolveWellKnownProtoRoot()
    {
        const string homebrewInclude = "/opt/homebrew/include";
        if (ContainsDescriptorProto(homebrewInclude))
            return homebrewInclude;

        var path = Path.Combine(ResolveGrpcToolsPackageRoot(), "build", "native", "include");
        if (!ContainsDescriptorProto(path))
            throw new InvalidOperationException($"Grpc.Tools well-known proto include directory was not found at `{path}`.");
        return path;
    }

    private static bool ContainsDescriptorProto(string root)
    {
        if (!Directory.Exists(root))
            return false;

        return File.Exists(Path.Combine(root, "google", "protobuf", "descriptor.proto"));
    }

    private static string ResolveGrpcToolsPackageRoot()
    {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packagesRoot))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            packagesRoot = Path.Combine(home, ".nuget", "packages");
        }

        var grpcToolsRoot = Path.Combine(packagesRoot, "grpc.tools");
        if (!Directory.Exists(grpcToolsRoot))
            throw new InvalidOperationException($"Grpc.Tools package root was not found at `{grpcToolsRoot}`.");

        return Directory.EnumerateDirectories(grpcToolsRoot)
                   .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                   .FirstOrDefault()
               ?? throw new InvalidOperationException($"Grpc.Tools package root was not found at `{grpcToolsRoot}`.");
    }

    private static string GetToolRelativePath(string toolName)
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? toolName + ".exe"
            : toolName;
        var osFolder =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macosx_x64" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows_x64" :
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux_arm64" :
            "linux_x64";
        return Path.Combine("tools", osFolder, fileName);
    }

    private static string Quote(string path) =>
        "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string? FindSystemProtoc()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "protoc.exe"
            : "protoc";
        foreach (var root in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IReadOnlyList<string> ValidateUnsupportedWrapperUsage(
        IReadOnlyList<ScriptSourceFile> protoFiles)
    {
        var diagnostics = new List<string>();
        foreach (var file in protoFiles)
        {
            if (!UnsupportedWrapperPattern.IsMatch(file.Content ?? string.Empty))
                continue;

            diagnostics.Add(
                $"{file.NormalizedPath}: scripting proto packages must not reference protobuf wrapper leaf types. " +
                "Use scalar fields, proto3 optional fields, or typed sub-messages instead of google.protobuf.*Value and wrappers.proto.");
        }

        return diagnostics;
    }
}
