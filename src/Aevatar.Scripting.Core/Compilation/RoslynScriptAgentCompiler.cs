using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.Scripting.Core.Compilation;

public sealed class RoslynScriptAgentCompiler : IScriptAgentCompiler
{
    private readonly ScriptSandboxPolicy _sandboxPolicy;

    public RoslynScriptAgentCompiler(ScriptSandboxPolicy sandboxPolicy)
    {
        _sandboxPolicy = sandboxPolicy;
    }

    public Task<ScriptCompilationResult> CompileAsync(
        ScriptCompilationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ScriptId))
            diagnostics.Add("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(request.Revision))
            diagnostics.Add("Revision is required.");
        if (string.IsNullOrWhiteSpace(request.Source))
            diagnostics.Add("Source is required.");
        if (diagnostics.Count > 0)
            return Task.FromResult(new ScriptCompilationResult(false, null, null, diagnostics));

        var sandbox = _sandboxPolicy.Validate(request.Source);
        if (!sandbox.IsValid)
            return Task.FromResult(new ScriptCompilationResult(false, null, null, sandbox.Violations));

        var syntaxTree = CSharpSyntaxTree.ParseText(request.Source);
        var syntaxErrors = syntaxTree
            .GetDiagnostics(ct)
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();

        if (syntaxErrors.Length > 0)
            return Task.FromResult(new ScriptCompilationResult(false, null, null, syntaxErrors));

        var contractManifest = ExtractContractManifest(request.Source);

        IScriptAgentDefinition compiledDefinition = new CompiledScriptAgentDefinition(
            request.ScriptId,
            request.Revision,
            request.Source,
            contractManifest);
        return Task.FromResult(
            new ScriptCompilationResult(
                true,
                compiledDefinition,
                contractManifest,
                Array.Empty<string>()));
    }

    private static ScriptContractManifest ExtractContractManifest(string source)
    {
        var inputSchema = MatchSingle(source, @"^\s*//\s*contract\.input\s*:\s*(?<value>.+)\s*$");
        var outputsRaw = MatchSingle(source, @"^\s*//\s*contract\.outputs\s*:\s*(?<value>.+)\s*$");
        var stateSchema = MatchSingle(source, @"^\s*//\s*contract\.state\s*:\s*(?<value>.+)\s*$");
        var readModelSchema = MatchSingle(source, @"^\s*//\s*contract\.readmodel\s*:\s*(?<value>.+)\s*$");

        var outputs = string.IsNullOrWhiteSpace(outputsRaw)
            ? Array.Empty<string>()
            : outputsRaw
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();

        return new ScriptContractManifest(
            string.IsNullOrWhiteSpace(inputSchema) ? "unspecified" : inputSchema,
            outputs,
            string.IsNullOrWhiteSpace(stateSchema) ? "unspecified" : stateSchema,
            string.IsNullOrWhiteSpace(readModelSchema) ? "unspecified" : readModelSchema);
    }

    private static string MatchSingle(string source, string pattern)
    {
        var match = Regex.Match(source ?? string.Empty, pattern, RegexOptions.Multiline);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private sealed class CompiledScriptAgentDefinition : IScriptAgentDefinition
    {
        public CompiledScriptAgentDefinition(
            string scriptId,
            string revision,
            string source,
            ScriptContractManifest contractManifest)
        {
            ScriptId = scriptId;
            Revision = revision;
            Source = source;
            ContractManifest = contractManifest;
        }

        public string ScriptId { get; }
        public string Revision { get; }
        private string Source { get; }
        public ScriptContractManifest ContractManifest { get; }

        public async Task<ScriptDecisionResult> DecideAsync(
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return await ExecuteScriptDecisionAsync(Source, context, ct);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private static async Task<ScriptDecisionResult> ExecuteScriptDecisionAsync(
            string source,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(source))
                return new ScriptDecisionResult(Array.Empty<IMessage>());

            // Fast path for sources that do not define a decision entry point.
            if (!Regex.IsMatch(source, @"\bDecide\s*\(", RegexOptions.CultureInvariant))
                return new ScriptDecisionResult(Array.Empty<IMessage>());

            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var compilation = CSharpCompilation.Create(
                assemblyName: "Aevatar.DynamicScript." + Guid.NewGuid().ToString("N"),
                syntaxTrees: [syntaxTree],
                references: GetMetadataReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            await using var assemblyStream = new MemoryStream();
            var emitResult = compilation.Emit(assemblyStream, cancellationToken: ct);
            if (!emitResult.Success)
            {
                var diagnostics = string.Join(
                    Environment.NewLine,
                    emitResult.Diagnostics
                        .Where(x => x.Severity == DiagnosticSeverity.Error)
                        .Select(x => x.ToString()));
                throw new InvalidOperationException("Script execution compilation failed: " + diagnostics);
            }

            assemblyStream.Position = 0;
            var loadContext = new AssemblyLoadContext(
                "Aevatar.DynamicScript.LoadContext." + Guid.NewGuid().ToString("N"),
                isCollectible: true);
            loadContext.Resolving += ResolveFromDefault;
            try
            {
                var assembly = loadContext.LoadFromStream(assemblyStream);
                var decideMethod = FindDecideMethod(assembly);
                if (decideMethod == null)
                    return new ScriptDecisionResult(Array.Empty<IMessage>());

                var arguments = BuildArguments(decideMethod.GetParameters(), context, ct);
                var invocationResult = decideMethod.Invoke(obj: null, parameters: arguments);
                if (invocationResult is Task decisionTask)
                {
                    await decisionTask.ConfigureAwait(false);
                    invocationResult = TryGetTaskResult(decisionTask);
                }

                return NormalizeDecisionResult(invocationResult);
            }
            finally
            {
                loadContext.Resolving -= ResolveFromDefault;
                loadContext.Unload();
            }
        }

        private static MethodInfo? FindDecideMethod(Assembly assembly)
        {
            return assembly
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(method => string.Equals(method.Name, "Decide", StringComparison.Ordinal))
                .OrderBy(method => method.GetParameters().Length)
                .ThenBy(method => method.DeclaringType?.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static object?[] BuildArguments(
            ParameterInfo[] parameters,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;
                if (parameterType == typeof(ScriptExecutionContext))
                {
                    args[i] = context;
                    continue;
                }

                if (parameterType == typeof(CancellationToken))
                {
                    args[i] = ct;
                    continue;
                }

                if (parameterType == typeof(IScriptRuntimeCapabilities))
                {
                    args[i] = context.Capabilities;
                    continue;
                }

                if (parameterType == typeof(string))
                {
                    args[i] = context.InputJson ?? string.Empty;
                    continue;
                }

                args[i] = DeserializeInput(context.InputJson ?? string.Empty, parameterType);
            }

            return args;
        }

        private static object? DeserializeInput(string inputJson, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(inputJson))
            {
                if (targetType == typeof(string))
                    return string.Empty;

                try
                {
                    var emptyJsonResult = JsonSerializer.Deserialize("{}", targetType, JsonOptions);
                    if (emptyJsonResult != null)
                        return emptyJsonResult;
                }
                catch
                {
                    // Fall through to non-JSON defaults.
                }

                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    return Activator.CreateInstance(targetType);

                try
                {
                    return Activator.CreateInstance(targetType);
                }
                catch
                {
                    return null;
                }
            }

            try
            {
                return JsonSerializer.Deserialize(inputJson, targetType, JsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to deserialize script input json into `{targetType.FullName}`.",
                    ex);
            }
        }

        private static ScriptDecisionResult NormalizeDecisionResult(object? invocationResult)
        {
            if (invocationResult is ScriptDecisionResult scriptDecisionResult)
                return scriptDecisionResult;

            return new ScriptDecisionResult(NormalizeDomainEvents(invocationResult));
        }

        private static IReadOnlyList<IMessage> NormalizeDomainEvents(object? invocationResult)
        {
            if (invocationResult == null)
                return Array.Empty<IMessage>();

            if (invocationResult is IMessage singleMessage)
                return [singleMessage];

            if (invocationResult is IEnumerable<IMessage> messageList)
                return messageList.ToArray();

            if (invocationResult is string eventName)
                return [new Google.Protobuf.WellKnownTypes.StringValue { Value = eventName }];

            if (invocationResult is IEnumerable<string> eventNames)
            {
                return eventNames
                    .Select(x => (IMessage)new Google.Protobuf.WellKnownTypes.StringValue { Value = x ?? string.Empty })
                    .ToArray();
            }

            if (invocationResult is IEnumerable<object?> objectList)
            {
                var normalized = new List<IMessage>();
                foreach (var item in objectList)
                    normalized.AddRange(NormalizeDomainEvents(item));
                return normalized;
            }

            return [new Google.Protobuf.WellKnownTypes.StringValue { Value = JsonSerializer.Serialize(invocationResult) }];
        }

        private static object? TryGetTaskResult(Task task)
        {
            var taskType = task.GetType();
            var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProperty?.GetValue(task);
        }

        private static IReadOnlyList<MetadataReference> GetMetadataReferences()
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(x => !x.IsDynamic && !string.IsNullOrWhiteSpace(x.Location))
                .Select(x => x.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
        }

        private static Assembly? ResolveFromDefault(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            _ = context;
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                x => string.Equals(x.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
        }
    }
}
