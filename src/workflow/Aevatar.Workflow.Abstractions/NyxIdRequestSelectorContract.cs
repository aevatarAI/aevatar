namespace Aevatar.Workflow.Abstractions;

public static class NyxIdRequestSelectorContract
{
    private static readonly string[] EncodedRoutingTokens =
        ["%2e", "%2f", "%5c", "%00", "%25", "%3f", "%23", "%7b", "%7d"];
    private static readonly HashSet<string> AllowedHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Accept", "If-Match", "If-None-Match" };

    public static bool TryNormalize(
        NyxIdRequestSelector? selector,
        out NyxIdRequestSelector normalized,
        out string error)
    {
        normalized = new NyxIdRequestSelector();
        error = string.Empty;
        if (selector is null)
            return Fail("explicit request selector is required", out error);

        normalized.UserServiceId = selector.UserServiceId.Trim();
        normalized.Method = selector.Method;
        normalized.PathTemplate = selector.PathTemplate.Trim();
        normalized.BodyMode = selector.BodyMode;
        normalized.BodyRequired = selector.BodyRequired;
        normalized.ResponseMode = selector.ResponseMode;
        normalized.Risk = selector.Risk;
        normalized.QueryParameters.Add(selector.QueryParameters
            .Select(static value => value.Trim())
            .OrderBy(static value => value, StringComparer.Ordinal));
        normalized.HeaderParameters.Add(selector.HeaderParameters
            .Select(static value => value.Trim())
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

        if (normalized.UserServiceId.Length == 0 || ContainsTemplate(normalized.UserServiceId))
            return Fail("explicit request must select one exact static UserService", out error);
        if (normalized.Method == NyxIdRequestMethod.Unspecified || !System.Enum.IsDefined(normalized.Method))
            return Fail("explicit request method is not supported", out error);
        if (!TryResolveRisk(normalized.Method, normalized.Risk, out _))
            return Fail("explicit request risk is below the method floor", out error);
        if (!IsSafePathTemplate(normalized.PathTemplate, out error))
            return false;
        if (!ValidateNames(normalized.QueryParameters, "query", StringComparer.Ordinal, out error) ||
            !ValidateNames(normalized.HeaderParameters, "header", StringComparer.OrdinalIgnoreCase, out error))
        {
            return false;
        }
        if (normalized.QueryParameters.Any(static name =>
                name.StartsWith("_nyxid_", StringComparison.OrdinalIgnoreCase) || IsCredentialName(name)))
        {
            return Fail("explicit request query parameters cannot carry credentials or reserved NyxID facts", out error);
        }
        if (normalized.HeaderParameters.Any(static name => !AllowedHeaders.Contains(name)))
        {
            return Fail("explicit request headers must use the workflow-safe header allowlist", out error);
        }
        if (normalized.BodyMode == NyxIdRequestBodyMode.Unspecified || !System.Enum.IsDefined(normalized.BodyMode))
            return Fail("explicit request body_mode must be none or json", out error);
        if (normalized.Method is NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options &&
            normalized.BodyMode != NyxIdRequestBodyMode.None)
        {
            return Fail("explicit safe request methods require body_mode none", out error);
        }
        if (normalized.BodyRequired && normalized.BodyMode != NyxIdRequestBodyMode.Json)
            return Fail("explicit request body_required is valid only with body_mode json", out error);
        if (normalized.ResponseMode == NyxIdRequestResponseMode.Unspecified ||
            !System.Enum.IsDefined(normalized.ResponseMode))
        {
            return Fail("explicit request response_mode must be text or file_artifact", out error);
        }
        if (normalized.ResponseMode == NyxIdRequestResponseMode.FileArtifact &&
            (normalized.Method != NyxIdRequestMethod.Get || normalized.BodyMode != NyxIdRequestBodyMode.None))
        {
            return Fail("file_artifact response requires GET with body_mode none", out error);
        }

        return true;
    }

    public static string MethodName(NyxIdRequestMethod method) => method switch
    {
        NyxIdRequestMethod.Get => "GET",
        NyxIdRequestMethod.Head => "HEAD",
        NyxIdRequestMethod.Options => "OPTIONS",
        NyxIdRequestMethod.Post => "POST",
        NyxIdRequestMethod.Put => "PUT",
        NyxIdRequestMethod.Patch => "PATCH",
        NyxIdRequestMethod.Delete => "DELETE",
        _ => string.Empty,
    };

    public static bool TryResolveRisk(
        NyxIdRequestMethod method,
        NyxIdOperationRisk declaredRisk,
        out NyxIdOperationRisk effectiveRisk)
    {
        effectiveRisk = method switch
        {
            NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options =>
                NyxIdOperationRisk.ReadOnly,
            NyxIdRequestMethod.Post or NyxIdRequestMethod.Put or NyxIdRequestMethod.Patch =>
                NyxIdOperationRisk.Write,
            NyxIdRequestMethod.Delete => NyxIdOperationRisk.Destructive,
            _ => NyxIdOperationRisk.Unspecified,
        };
        if (effectiveRisk == NyxIdOperationRisk.Unspecified)
            return false;
        if (declaredRisk == NyxIdOperationRisk.Unspecified)
            return true;
        if (!System.Enum.IsDefined(declaredRisk))
            return false;

        var allowed = method switch
        {
            NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options or
                NyxIdRequestMethod.Post =>
                declaredRisk is NyxIdOperationRisk.ReadOnly or NyxIdOperationRisk.Write or
                    NyxIdOperationRisk.Destructive,
            NyxIdRequestMethod.Put or NyxIdRequestMethod.Patch =>
                declaredRisk is NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive,
            NyxIdRequestMethod.Delete => declaredRisk == NyxIdOperationRisk.Destructive,
            _ => false,
        };
        if (allowed)
            effectiveRisk = declaredRisk;
        return allowed;
    }

    public static bool IsRiskAttestationSatisfied(
        NyxIdRequestMethod method,
        NyxIdOperationRisk declaredRisk,
        NyxIdOperationRisk evaluatedRisk)
    {
        if (!TryResolveRisk(method, declaredRisk, out var effectiveRisk))
            return false;
        if (declaredRisk != NyxIdOperationRisk.Unspecified)
            return effectiveRisk == evaluatedRisk;

        return method switch
        {
            NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options =>
                evaluatedRisk is NyxIdOperationRisk.ReadOnly or NyxIdOperationRisk.Write or
                    NyxIdOperationRisk.Destructive,
            NyxIdRequestMethod.Post or NyxIdRequestMethod.Put or NyxIdRequestMethod.Patch =>
                evaluatedRisk is NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive,
            NyxIdRequestMethod.Delete => evaluatedRisk == NyxIdOperationRisk.Destructive,
            _ => false,
        };
    }

    public static bool SupportsDurableExecution(
        NyxIdRequestMethod method,
        NyxIdOperationRisk risk) => risk switch
        {
            NyxIdOperationRisk.ReadOnly =>
                method is NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options or
                    NyxIdRequestMethod.Post,
            NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive => true,
            _ => false,
        };

    public static IReadOnlyList<string> PathParameters(NyxIdRequestSelector selector) =>
        selector.PathTemplate.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static segment => segment.Length > 2 && segment[0] == '{' && segment[^1] == '}')
            .Select(static segment => segment[1..^1])
            .ToArray();

    private static bool IsSafePathTemplate(string path, out string error)
    {
        error = string.Empty;
        if (!HasValidPathShape(path))
        {
            return Fail("explicit request path_template must be one normalized static relative path", out error);
        }
        if (EncodedRoutingTokens.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return Fail("explicit request path_template contains encoded routing syntax", out error);

        var placeholders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in path.Split('/').Skip(1))
        {
            if (segment is "." or "..")
                return Fail("explicit request path_template cannot traverse directories", out error);
            if (!segment.Contains('{') && !segment.Contains('}'))
                continue;
            var name = segment.Length > 2 ? segment[1..^1] : string.Empty;
            if (segment[0] != '{' || segment[^1] != '}' ||
                name.Length == 0 || !(IsAsciiLetter(name[0]) || name[0] == '_') ||
                name.Any(static character => !IsAsciiLetterOrDigit(character) && character != '_') ||
                !placeholders.Add(name))
            {
                return Fail("explicit request path placeholders must be unique named path segments", out error);
            }
        }

        return true;
    }

    private static bool HasValidPathShape(string path) =>
        path.Length != 0 &&
        path.StartsWith("/", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.EndsWith("/", StringComparison.Ordinal) &&
        !path.Contains("//", StringComparison.Ordinal) &&
        !Uri.TryCreate(path.TrimStart('/'), UriKind.Absolute, out _) &&
        !path.Contains('\\') &&
        !path.Contains('?') &&
        !path.Contains('#') &&
        !path.Contains('\0') &&
        !path.Any(char.IsWhiteSpace) &&
        !ContainsTemplate(path);

    private static bool ValidateNames(
        IEnumerable<string> names,
        string kind,
        StringComparer comparer,
        out string error)
    {
        error = string.Empty;
        var seen = new HashSet<string>(comparer);
        foreach (var name in names)
        {
            if (name.Length == 0 ||
                name.Any(static character => !char.IsLetterOrDigit(character) && character is not ('_' or '-' or '.')) ||
                !seen.Add(name))
            {
                return Fail($"explicit request {kind} parameter names must be unique static tokens", out error);
            }
        }

        return true;
    }

    private static bool ContainsTemplate(string value) => value.Contains("${", StringComparison.Ordinal);

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiLetterOrDigit(char character) =>
        IsAsciiLetter(character) || character is >= '0' and <= '9';

    private static bool IsCredentialName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized is "authorization" or "proxyauthorization" or "cookie" or "setcookie" or
               "apikey" or "xapikey" or "token" or "apitoken" or "xauthtoken" or
               "accesstoken" or "xaccesstoken" or "bearertoken" ||
               normalized.EndsWith("apikey", StringComparison.Ordinal) ||
               normalized.EndsWith("accesstoken", StringComparison.Ordinal) ||
               normalized.EndsWith("authtoken", StringComparison.Ordinal);
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
