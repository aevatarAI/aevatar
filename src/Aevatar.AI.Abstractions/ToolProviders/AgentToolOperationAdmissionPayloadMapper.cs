namespace Aevatar.AI.Abstractions.ToolProviders;

public static class AgentToolOperationAdmissionPayloadMapper
{
    public static AgentToolOperationAdmissionPayload ToPayload(AgentToolOperationAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        var payload = new AgentToolOperationAdmissionPayload
        {
            ServiceInstanceId = admission.ServiceInstanceId ?? string.Empty,
            ServiceSlug = admission.ServiceSlug ?? string.Empty,
            AuthorizationBasis = ToAuthorizationBasis(admission.AuthorizationBasis),
            HttpMethod = admission.HttpMethod ?? string.Empty,
            PathTemplate = admission.PathTemplate ?? string.Empty,
            ContractDigest = admission.ContractDigest ?? string.Empty,
            ResponsePolicy = ToResponsePolicy(admission.ResponsePolicy),
            ExecutionPolicy = ToExecutionPolicy(admission.ExecutionPolicy),
            CatalogDigest = admission.CatalogDigest ?? string.Empty,
            CatalogServiceSlug = admission.CatalogServiceSlug ?? string.Empty,
        };
        switch (admission.Identity)
        {
            case AgentToolOperationIdentity.PublishedEndpoint published:
                payload.PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
                {
                    EndpointId = published.EndpointId ?? string.Empty,
                };
                break;
            case AgentToolOperationIdentity.AuthoredRequest authored:
                payload.AuthoredRequest = new AgentToolAuthoredRequestIdentityPayload
                {
                    RequestContractDigest = authored.RequestContractDigest ?? string.Empty,
                };
                break;
            case AgentToolOperationIdentity.PlatformBuiltIn platform:
                payload.PlatformBuiltIn = new AgentToolPlatformBuiltInIdentityPayload
                {
                    CapabilityId = platform.CapabilityId ?? string.Empty,
                };
                break;
        }

        payload.Parameters.AddRange(admission.Parameters.Select(ToParameter));
        if (admission.RequestBody is not null)
            payload.RequestBody = ToRequestBody(admission.RequestBody);
        if (admission.ReadBack is not null)
            payload.ReadBack = ToReadBack(admission.ReadBack);
        return payload;
    }

    public static AgentToolOperationAdmission? FromPayload(AgentToolOperationAdmissionPayload? payload)
    {
        if (payload is null)
            return null;

        AgentToolOperationIdentity? identity = payload.IdentityCase switch
        {
            AgentToolOperationAdmissionPayload.IdentityOneofCase.PublishedEndpoint
                when !string.IsNullOrWhiteSpace(payload.PublishedEndpoint.EndpointId) =>
                new AgentToolOperationIdentity.PublishedEndpoint(
                    payload.PublishedEndpoint.EndpointId ?? string.Empty),
            AgentToolOperationAdmissionPayload.IdentityOneofCase.AuthoredRequest
                when !string.IsNullOrWhiteSpace(payload.AuthoredRequest.RequestContractDigest) =>
                new AgentToolOperationIdentity.AuthoredRequest(
                    payload.AuthoredRequest.RequestContractDigest ?? string.Empty),
            AgentToolOperationAdmissionPayload.IdentityOneofCase.PlatformBuiltIn
                when !string.IsNullOrWhiteSpace(payload.PlatformBuiltIn.CapabilityId) =>
                new AgentToolOperationIdentity.PlatformBuiltIn(
                    payload.PlatformBuiltIn.CapabilityId ?? string.Empty),
            _ => null,
        };
        if (identity is null)
            return null;

        var readBack = FromReadBack(payload.ReadBack);
        if (payload.ReadBack is not null && readBack is null)
            return null;

        return new AgentToolOperationAdmission(
            payload.ServiceInstanceId ?? string.Empty,
            payload.ServiceSlug ?? string.Empty,
            identity,
            FromAuthorizationBasis(payload.AuthorizationBasis),
            payload.HttpMethod ?? string.Empty,
            payload.PathTemplate ?? string.Empty,
            payload.ContractDigest ?? string.Empty,
            payload.Parameters.Select(FromParameter).ToArray(),
            payload.RequestBody is null ? null : FromRequestBody(payload.RequestBody),
            FromResponsePolicy(payload.ResponsePolicy),
            FromExecutionPolicy(payload.ExecutionPolicy),
            payload.CatalogDigest ?? string.Empty,
            readBack,
            payload.CatalogServiceSlug ?? string.Empty);
    }

    private static AgentToolOperationReadBackPayload ToReadBack(AgentToolOperationReadBack readBack)
    {
        if (readBack.ReadOperation.ReadBack is not null)
        {
            throw new InvalidOperationException(
                "An operation read-back cannot recursively carry another read-back contract.");
        }

        var payload = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ToPayload(readBack.ReadOperation),
            Arguments = readBack.Arguments?.Clone() ?? new Google.Protobuf.WellKnownTypes.Struct(),
            Assertion = ToReadBackAssertion(readBack.Assertion),
            CheckName = readBack.CheckName ?? string.Empty,
            EffectResultIdentityJsonPointer =
                readBack.EffectResultIdentityJsonPointer ?? string.Empty,
        };
        if (readBack.NotAppliedAssertion is not null)
            payload.NotAppliedAssertion = ToReadBackAssertion(readBack.NotAppliedAssertion);
        if (readBack.Pagination is not null)
        {
            payload.Pagination = new AgentToolReadBackPaginationPayload
            {
                HasMoreJsonPointer = readBack.Pagination.HasMoreJsonPointer ?? string.Empty,
                PageTokenJsonPointer = readBack.Pagination.PageTokenJsonPointer ?? string.Empty,
                PageTokenLocation = ToParameterLocation(readBack.Pagination.PageTokenLocation),
                PageTokenArgumentName = readBack.Pagination.PageTokenArgumentName ?? string.Empty,
                MaxPages = (uint)Math.Max(0, readBack.Pagination.MaxPages),
            };
        }
        if (readBack.ProviderResourceArgument is not null)
        {
            payload.ProviderResourceArgument = new AgentToolReadBackProviderResourceArgumentPayload
            {
                Location = ToParameterLocation(readBack.ProviderResourceArgument.Location),
                ArgumentName = readBack.ProviderResourceArgument.ArgumentName ?? string.Empty,
            };
        }
        return payload;
    }

    private static AgentToolOperationReadBack? FromReadBack(AgentToolOperationReadBackPayload? payload)
    {
        if (payload?.ReadOperation is null || payload.Assertion is null)
            return null;

        if (!AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryCanonicalize(
                payload.Assertion,
                out var assertion))
        {
            return null;
        }

        AgentToolReadBackAssertionPayload? notAppliedAssertion = null;
        if (payload.NotAppliedAssertion is not null &&
            !AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryCanonicalize(
                payload.NotAppliedAssertion,
                out notAppliedAssertion))
        {
            return null;
        }

        var readOperation = FromPayload(payload.ReadOperation);
        if (readOperation is null || readOperation.ReadBack is not null)
            return null;

        return new AgentToolOperationReadBack(
            readOperation,
            payload.Arguments?.Clone() ?? new Google.Protobuf.WellKnownTypes.Struct(),
            FromReadBackAssertion(assertion),
            payload.CheckName ?? string.Empty,
            notAppliedAssertion is null ? null : FromReadBackAssertion(notAppliedAssertion),
            payload.Pagination is null
                ? null
                : new AgentToolReadBackPagination(
                    payload.Pagination.HasMoreJsonPointer ?? string.Empty,
                    payload.Pagination.PageTokenJsonPointer ?? string.Empty,
                    FromParameterLocation(payload.Pagination.PageTokenLocation),
                    payload.Pagination.PageTokenArgumentName ?? string.Empty,
                    checked((int)payload.Pagination.MaxPages)),
            payload.ProviderResourceArgument is null
                ? null
                : new AgentToolReadBackProviderResourceArgument(
                    FromParameterLocation(payload.ProviderResourceArgument.Location),
                    payload.ProviderResourceArgument.ArgumentName ?? string.Empty),
            payload.EffectResultIdentityJsonPointer ?? string.Empty);
    }

    private static AgentToolReadBackAssertionPayload ToReadBackAssertion(
        AgentToolReadBackAssertion assertion) =>
        AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.CanonicalizeForWrite(new()
        {
            Match = ToReadBackMatch(assertion.Match),
            JsonPointer = assertion.JsonPointer ?? string.Empty,
            ExpectedValue = assertion.ExpectedValue?.Clone(),
            ElementJsonPointer = assertion.ElementJsonPointer ?? string.Empty,
            ExpectedValueSource = ToExpectedValueSource(assertion.ExpectedValueSource),
        });

    private static AgentToolReadBackAssertion FromReadBackAssertion(
        AgentToolReadBackAssertionPayload assertion) => new(
        FromReadBackMatch(assertion.Match),
        assertion.JsonPointer ?? string.Empty,
        assertion.ExpectedValue?.Clone(),
        assertion.ElementJsonPointer ?? string.Empty,
        FromExpectedValueSource(assertion.ExpectedValueSource));

    private static AgentToolReadBackMatchPayload ToReadBackMatch(AgentToolReadBackMatch value) => value switch
    {
        AgentToolReadBackMatch.Exists => AgentToolReadBackMatchPayload.Exists,
        AgentToolReadBackMatch.Absent => AgentToolReadBackMatchPayload.Absent,
        AgentToolReadBackMatch.Equals => AgentToolReadBackMatchPayload.Equals,
        AgentToolReadBackMatch.ArrayContainsEquals => AgentToolReadBackMatchPayload.ArrayContainsEquals,
        _ => AgentToolReadBackMatchPayload.Unspecified,
    };

    private static AgentToolReadBackMatch FromReadBackMatch(AgentToolReadBackMatchPayload value) => value switch
    {
        AgentToolReadBackMatchPayload.Exists => AgentToolReadBackMatch.Exists,
        AgentToolReadBackMatchPayload.Absent => AgentToolReadBackMatch.Absent,
        AgentToolReadBackMatchPayload.Equals => AgentToolReadBackMatch.Equals,
        AgentToolReadBackMatchPayload.ArrayContainsEquals => AgentToolReadBackMatch.ArrayContainsEquals,
        _ => AgentToolReadBackMatch.Unspecified,
    };

    private static AgentToolReadBackExpectedValueSourcePayload ToExpectedValueSource(
        AgentToolReadBackExpectedValueSource value) => value switch
    {
        AgentToolReadBackExpectedValueSource.ProviderResourceId =>
            AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId,
        _ => AgentToolReadBackExpectedValueSourcePayload.FrozenValue,
    };

    private static AgentToolReadBackExpectedValueSource FromExpectedValueSource(
        AgentToolReadBackExpectedValueSourcePayload value) => value switch
    {
        AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId =>
            AgentToolReadBackExpectedValueSource.ProviderResourceId,
        _ => AgentToolReadBackExpectedValueSource.FrozenValue,
    };

    private static AgentToolOperationParameterPayload ToParameter(AgentToolOperationParameter parameter) => new()
    {
        Name = parameter.Name ?? string.Empty,
        Location = ToParameterLocation(parameter.Location),
        Required = parameter.Required,
        Schema = ToSchema(parameter.Schema),
    };

    private static AgentToolOperationParameter FromParameter(AgentToolOperationParameterPayload parameter) => new(
        parameter.Name ?? string.Empty,
        FromParameterLocation(parameter.Location),
        parameter.Required,
        FromSchema(parameter.Schema));

    private static AgentToolOperationRequestBodyPayload ToRequestBody(AgentToolOperationRequestBody body) => new()
    {
        Required = body.Required,
        MediaType = body.MediaType ?? string.Empty,
        Schema = ToSchema(body.Schema),
    };

    private static AgentToolOperationRequestBody FromRequestBody(AgentToolOperationRequestBodyPayload body) => new(
        body.Required,
        body.MediaType ?? string.Empty,
        FromSchema(body.Schema));

    private static AgentToolOperationResponsePolicyPayload ToResponsePolicy(
        AgentToolOperationResponsePolicy policy)
    {
        var payload = new AgentToolOperationResponsePolicyPayload
        {
            TextAllowed = policy.TextAllowed,
            FileArtifactAllowed = policy.FileArtifactAllowed,
        };
        payload.MediaTypes.AddRange(policy.MediaTypes);
        return payload;
    }

    private static AgentToolOperationResponsePolicy FromResponsePolicy(
        AgentToolOperationResponsePolicyPayload? policy) => policy is null
        ? new AgentToolOperationResponsePolicy(false, false, [])
        : new AgentToolOperationResponsePolicy(
            policy.TextAllowed,
            policy.FileArtifactAllowed,
            policy.MediaTypes.ToArray());

    private static AgentToolOperationExecutionPolicyPayload ToExecutionPolicy(
        AgentToolOperationExecutionPolicy policy)
    {
        var payload = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = ToRisk(policy.Risk),
            Approval = ToApproval(policy.Approval),
            EnforcementOwner = ToEnforcementOwner(policy.EnforcementOwner),
        };
        payload.AllowedExecutionModes.AddRange(policy.AllowedExecutionModes.Select(ToExecutionMode));
        return payload;
    }

    private static AgentToolOperationExecutionPolicy FromExecutionPolicy(
        AgentToolOperationExecutionPolicyPayload? policy) => policy is null
        ? AgentToolOperationExecutionPolicy.Unspecified
        : new AgentToolOperationExecutionPolicy(
            FromRisk(policy.Risk),
            FromApproval(policy.Approval),
            FromEnforcementOwner(policy.EnforcementOwner),
            policy.AllowedExecutionModes.Select(FromExecutionMode).ToArray());

    private static AgentToolOperationValueSchemaPayload ToSchema(AgentToolOperationValueSchema schema)
    {
        var payload = new AgentToolOperationValueSchemaPayload
        {
            Kind = ToValueKind(schema.Kind),
            AdditionalPropertiesAllowed = schema.AdditionalPropertiesAllowed,
        };
        payload.Properties.AddRange(schema.Properties.Select(static property =>
            new AgentToolOperationSchemaPropertyPayload
            {
                Name = property.Name ?? string.Empty,
                Schema = ToSchema(property.Schema),
            }));
        payload.RequiredProperties.AddRange(schema.RequiredProperties);
        payload.AllowedValues.AddRange(schema.AllowedValues);
        if (schema.Items is not null)
            payload.Items = ToSchema(schema.Items);
        return payload;
    }

    private static AgentToolOperationValueSchema FromSchema(AgentToolOperationValueSchemaPayload? schema) => new(
        FromValueKind(schema?.Kind ?? AgentToolOperationValueKindPayload.Unspecified),
        schema?.Properties.Select(static property => new AgentToolOperationSchemaProperty(
            property.Name ?? string.Empty,
            FromSchema(property.Schema))).ToArray() ?? [],
        new HashSet<string>(schema?.RequiredProperties ?? [], StringComparer.Ordinal),
        schema?.Items is null ? null : FromSchema(schema.Items),
        schema?.AllowedValues.ToArray() ?? [],
        schema?.AdditionalPropertiesAllowed ?? false);

    private static AgentToolOperationAuthorizationBasisPayload ToAuthorizationBasis(
        AgentToolOperationAuthorizationBasis value) => value switch
        {
            AgentToolOperationAuthorizationBasis.PublishedContract =>
                AgentToolOperationAuthorizationBasisPayload.PublishedContract,
            AgentToolOperationAuthorizationBasis.ExplicitRequest =>
                AgentToolOperationAuthorizationBasisPayload.ExplicitRequest,
            AgentToolOperationAuthorizationBasis.PlatformContract =>
                AgentToolOperationAuthorizationBasisPayload.PlatformContract,
            _ => AgentToolOperationAuthorizationBasisPayload.Unspecified,
        };

    private static AgentToolOperationAuthorizationBasis FromAuthorizationBasis(
        AgentToolOperationAuthorizationBasisPayload value) => value switch
        {
            AgentToolOperationAuthorizationBasisPayload.PublishedContract =>
                AgentToolOperationAuthorizationBasis.PublishedContract,
            AgentToolOperationAuthorizationBasisPayload.ExplicitRequest =>
                AgentToolOperationAuthorizationBasis.ExplicitRequest,
            AgentToolOperationAuthorizationBasisPayload.PlatformContract =>
                AgentToolOperationAuthorizationBasis.PlatformContract,
            _ => default,
        };

    private static AgentToolOperationRiskPayload ToRisk(AgentToolOperationRisk value) => value switch
    {
        AgentToolOperationRisk.ReadOnly => AgentToolOperationRiskPayload.ReadOnly,
        AgentToolOperationRisk.Write => AgentToolOperationRiskPayload.Write,
        AgentToolOperationRisk.Destructive => AgentToolOperationRiskPayload.Destructive,
        _ => AgentToolOperationRiskPayload.Unspecified,
    };

    private static AgentToolOperationRisk FromRisk(AgentToolOperationRiskPayload value) => value switch
    {
        AgentToolOperationRiskPayload.ReadOnly => AgentToolOperationRisk.ReadOnly,
        AgentToolOperationRiskPayload.Write => AgentToolOperationRisk.Write,
        AgentToolOperationRiskPayload.Destructive => AgentToolOperationRisk.Destructive,
        _ => AgentToolOperationRisk.Unspecified,
    };

    private static AgentToolOperationApprovalPayload ToApproval(AgentToolOperationApproval value) => value switch
    {
        AgentToolOperationApproval.None => AgentToolOperationApprovalPayload.None,
        AgentToolOperationApproval.Required => AgentToolOperationApprovalPayload.Required,
        _ => AgentToolOperationApprovalPayload.Unspecified,
    };

    private static AgentToolOperationApproval FromApproval(AgentToolOperationApprovalPayload value) => value switch
    {
        AgentToolOperationApprovalPayload.None => AgentToolOperationApproval.None,
        AgentToolOperationApprovalPayload.Required => AgentToolOperationApproval.Required,
        _ => AgentToolOperationApproval.Unspecified,
    };

    private static AgentToolOperationEnforcementOwnerPayload ToEnforcementOwner(
        AgentToolOperationEnforcementOwner value) => value switch
        {
            AgentToolOperationEnforcementOwner.Aevatar => AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AgentToolOperationEnforcementOwner.NyxId => AgentToolOperationEnforcementOwnerPayload.NyxId,
            _ => AgentToolOperationEnforcementOwnerPayload.Unspecified,
        };

    private static AgentToolOperationEnforcementOwner FromEnforcementOwner(
        AgentToolOperationEnforcementOwnerPayload value) => value switch
        {
            AgentToolOperationEnforcementOwnerPayload.Aevatar => AgentToolOperationEnforcementOwner.Aevatar,
            AgentToolOperationEnforcementOwnerPayload.NyxId => AgentToolOperationEnforcementOwner.NyxId,
            _ => AgentToolOperationEnforcementOwner.Unspecified,
        };

    private static AgentToolOperationExecutionModePayload ToExecutionMode(
        AgentToolOperationExecutionMode value) => value switch
        {
            AgentToolOperationExecutionMode.Interactive => AgentToolOperationExecutionModePayload.Interactive,
            AgentToolOperationExecutionMode.Durable => AgentToolOperationExecutionModePayload.Durable,
            _ => AgentToolOperationExecutionModePayload.Unspecified,
        };

    private static AgentToolOperationExecutionMode FromExecutionMode(
        AgentToolOperationExecutionModePayload value) => value switch
        {
            AgentToolOperationExecutionModePayload.Interactive => AgentToolOperationExecutionMode.Interactive,
            AgentToolOperationExecutionModePayload.Durable => AgentToolOperationExecutionMode.Durable,
            _ => AgentToolOperationExecutionMode.Unspecified,
        };

    private static AgentToolOperationParameterLocationPayload ToParameterLocation(
        AgentToolOperationParameterLocation value) => value switch
        {
            AgentToolOperationParameterLocation.Path => AgentToolOperationParameterLocationPayload.Path,
            AgentToolOperationParameterLocation.Query => AgentToolOperationParameterLocationPayload.Query,
            AgentToolOperationParameterLocation.Header => AgentToolOperationParameterLocationPayload.Header,
            _ => AgentToolOperationParameterLocationPayload.Unspecified,
        };

    private static AgentToolOperationParameterLocation FromParameterLocation(
        AgentToolOperationParameterLocationPayload value) => value switch
        {
            AgentToolOperationParameterLocationPayload.Path => AgentToolOperationParameterLocation.Path,
            AgentToolOperationParameterLocationPayload.Query => AgentToolOperationParameterLocation.Query,
            AgentToolOperationParameterLocationPayload.Header => AgentToolOperationParameterLocation.Header,
            _ => AgentToolOperationParameterLocation.Unspecified,
        };

    private static AgentToolOperationValueKindPayload ToValueKind(AgentToolOperationValueKind value) => value switch
    {
        AgentToolOperationValueKind.String => AgentToolOperationValueKindPayload.String,
        AgentToolOperationValueKind.Integer => AgentToolOperationValueKindPayload.Integer,
        AgentToolOperationValueKind.Number => AgentToolOperationValueKindPayload.Number,
        AgentToolOperationValueKind.Boolean => AgentToolOperationValueKindPayload.Boolean,
        AgentToolOperationValueKind.Object => AgentToolOperationValueKindPayload.Object,
        AgentToolOperationValueKind.Array => AgentToolOperationValueKindPayload.Array,
        _ => AgentToolOperationValueKindPayload.Unspecified,
    };

    private static AgentToolOperationValueKind FromValueKind(AgentToolOperationValueKindPayload value) => value switch
    {
        AgentToolOperationValueKindPayload.String => AgentToolOperationValueKind.String,
        AgentToolOperationValueKindPayload.Integer => AgentToolOperationValueKind.Integer,
        AgentToolOperationValueKindPayload.Number => AgentToolOperationValueKind.Number,
        AgentToolOperationValueKindPayload.Boolean => AgentToolOperationValueKind.Boolean,
        AgentToolOperationValueKindPayload.Object => AgentToolOperationValueKind.Object,
        AgentToolOperationValueKindPayload.Array => AgentToolOperationValueKind.Array,
        _ => AgentToolOperationValueKind.Unspecified,
    };
}
