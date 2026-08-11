using System.Globalization;
using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatDomainEvidenceContract
{
    internal const string ReimbursementToolName = "reimbursement_evidence_commit";
    internal const string CandidateScreeningToolName = "candidate_screening_evidence_commit";

    internal static bool IsDomainEvidence(NyxIdChatToolCall? call) =>
        call is not null &&
        call.ToolName?.Trim() is ReimbursementToolName or CandidateScreeningToolName;

    internal static bool TryParseReimbursement(
        string? argumentsJson,
        out NyxIdChatReimbursementEvidence evidence)
    {
        evidence = null!;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? string.Empty);
            var root = document.RootElement;
            if (!IsObjectWithOnly(root,
                    "source_input_request_id",
                    "expense_category",
                    "cost_center",
                    "reimbursement_currency_instruction",
                    "guarded_tool_name",
                    "source_invoices",
                    "retained_source_ordinals",
                    "duplicate_invoices") ||
                !TryRequiredString(root, "source_input_request_id", out var sourceInputRequestId) ||
                !TryRequiredString(root, "expense_category", out var expenseCategory) ||
                !TryRequiredString(root, "cost_center", out var costCenter) ||
                !TryRequiredString(root, "reimbursement_currency_instruction",
                    out var currencyInstruction) ||
                !TryRequiredString(root, "guarded_tool_name", out var guardedToolName) ||
                guardedToolName is ReimbursementToolName or CandidateScreeningToolName ||
                !root.TryGetProperty("source_invoices", out var invoicesElement) ||
                invoicesElement.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("retained_source_ordinals", out var retainedElement) ||
                retainedElement.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("duplicate_invoices", out var duplicatesElement) ||
                duplicatesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var invoices = new List<NyxIdChatInvoiceEvidence>();
            foreach (var invoiceElement in invoicesElement.EnumerateArray())
            {
                if (!TryParseInvoice(invoiceElement, out var invoice))
                    return false;
                invoices.Add(invoice);
            }
            if (invoices.Count == 0 ||
                invoices.Select(static invoice => invoice.SourceOrdinal).Distinct().Count() !=
                invoices.Count)
            {
                return false;
            }

            var retained = new List<int>();
            foreach (var ordinalElement in retainedElement.EnumerateArray())
            {
                if (!ordinalElement.TryGetInt32(out var ordinal) || ordinal <= 0)
                    return false;
                retained.Add(ordinal);
            }
            if (retained.Count == 0 || retained.Distinct().Count() != retained.Count)
                return false;

            var duplicates = new List<NyxIdChatInvoiceDuplicateEvidence>();
            foreach (var duplicateElement in duplicatesElement.EnumerateArray())
            {
                if (!IsObjectWithOnly(
                        duplicateElement,
                        "duplicate_source_ordinal",
                        "retained_source_ordinal") ||
                    !TryPositiveInt32(duplicateElement, "duplicate_source_ordinal",
                        out var duplicateOrdinal) ||
                    !TryPositiveInt32(duplicateElement, "retained_source_ordinal",
                        out var retainedOrdinal) ||
                    duplicateOrdinal == retainedOrdinal)
                {
                    return false;
                }
                duplicates.Add(new NyxIdChatInvoiceDuplicateEvidence
                {
                    DuplicateSourceOrdinal = duplicateOrdinal,
                    RetainedSourceOrdinal = retainedOrdinal,
                });
            }
            if (duplicates.Select(static duplicate => duplicate.DuplicateSourceOrdinal)
                    .Distinct().Count() != duplicates.Count)
            {
                return false;
            }

            var byOrdinal = invoices.ToDictionary(static invoice => invoice.SourceOrdinal);
            if (retained.Any(ordinal => !byOrdinal.ContainsKey(ordinal)) ||
                duplicates.Any(duplicate =>
                    !byOrdinal.ContainsKey(duplicate.DuplicateSourceOrdinal) ||
                    !retained.Contains(duplicate.RetainedSourceOrdinal) ||
                    !InvoiceIdentityEquals(
                        byOrdinal[duplicate.DuplicateSourceOrdinal],
                        byOrdinal[duplicate.RetainedSourceOrdinal])))
            {
                return false;
            }

            var classified = retained
                .Concat(duplicates.Select(static duplicate => duplicate.DuplicateSourceOrdinal))
                .Order()
                .ToArray();
            if (!classified.SequenceEqual(byOrdinal.Keys.Order()))
                return false;
            var retainedInvoices = retained.Select(ordinal => byOrdinal[ordinal]).ToArray();
            if (retainedInvoices.Select(InvoiceIdentityKey).Distinct(StringComparer.Ordinal).Count() !=
                retainedInvoices.Length)
            {
                return false;
            }

            evidence = new NyxIdChatReimbursementEvidence
            {
                SourceInputRequestId = sourceInputRequestId,
                ExpenseCategory = expenseCategory,
                CostCenter = costCenter,
                ReimbursementCurrencyInstruction = currencyInstruction,
                GuardedToolName = guardedToolName,
            };
            evidence.SourceInvoices.AddRange(invoices.OrderBy(static invoice => invoice.SourceOrdinal));
            evidence.RetainedSourceOrdinals.AddRange(retained.Order());
            evidence.DuplicateInvoices.AddRange(duplicates.OrderBy(
                static duplicate => duplicate.DuplicateSourceOrdinal));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseCandidateScreening(
        string? argumentsJson,
        out NyxIdChatCandidateScreeningEvidence evidence)
    {
        evidence = null!;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? string.Empty);
            var root = document.RootElement;
            if (!IsObjectWithOnly(root,
                    "source_input_request_id",
                    "candidate_name",
                    "role_title",
                    "rubric",
                    "scores",
                    "total_score",
                    "tracker_table",
                    "tracker_table_id",
                    "stage",
                    "guarded_tool_name") ||
                !TryRequiredString(root, "source_input_request_id", out var sourceInputRequestId) ||
                !TryRequiredString(root, "candidate_name", out var candidateName) ||
                !TryRequiredString(root, "role_title", out var roleTitle) ||
                !TryRequiredString(root, "tracker_table", out var trackerTable) ||
                !TryRequiredString(root, "tracker_table_id", out var trackerTableId) ||
                !TryRequiredString(root, "stage", out var stage) ||
                !TryRequiredString(root, "guarded_tool_name", out var guardedToolName) ||
                guardedToolName is ReimbursementToolName or CandidateScreeningToolName ||
                !root.TryGetProperty("total_score", out var totalScoreElement) ||
                !totalScoreElement.TryGetInt32(out var totalScore) ||
                totalScore is < 0 or > 100 ||
                !root.TryGetProperty("rubric", out var rubricElement) ||
                rubricElement.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("scores", out var scoresElement) ||
                scoresElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var rubric = new List<NyxIdChatCandidateRubricCriterion>();
            foreach (var criterionElement in rubricElement.EnumerateArray())
            {
                if (!IsObjectWithOnly(
                        criterionElement,
                        "criterion_id",
                        "title",
                        "maximum_points") ||
                    !TryRequiredString(criterionElement, "criterion_id", out var criterionId) ||
                    !TryRequiredString(criterionElement, "title", out var title) ||
                    !TryPositiveInt32(criterionElement, "maximum_points", out var maximumPoints) ||
                    maximumPoints > 100)
                {
                    return false;
                }
                rubric.Add(new NyxIdChatCandidateRubricCriterion
                {
                    CriterionId = criterionId,
                    Title = title,
                    MaximumPoints = maximumPoints,
                });
            }
            if (rubric.Count == 0 || rubric.Count > 10 ||
                rubric.Select(static criterion => criterion.CriterionId)
                    .Distinct(StringComparer.Ordinal).Count() != rubric.Count ||
                rubric.Sum(static criterion => criterion.MaximumPoints) != 100)
            {
                return false;
            }

            var scores = new List<NyxIdChatCandidateCriterionScore>();
            foreach (var scoreElement in scoresElement.EnumerateArray())
            {
                if (!IsObjectWithOnly(
                        scoreElement,
                        "criterion_id",
                        "awarded_points",
                        "evidence") ||
                    !TryRequiredString(scoreElement, "criterion_id", out var criterionId) ||
                    !scoreElement.TryGetProperty("awarded_points", out var awardedElement) ||
                    !awardedElement.TryGetInt32(out var awardedPoints) ||
                    !TryRequiredString(scoreElement, "evidence", out var scoreEvidence))
                {
                    return false;
                }
                scores.Add(new NyxIdChatCandidateCriterionScore
                {
                    CriterionId = criterionId,
                    AwardedPoints = awardedPoints,
                    Evidence = scoreEvidence,
                });
            }

            var rubricById = rubric.ToDictionary(static criterion => criterion.CriterionId,
                StringComparer.Ordinal);
            if (scores.Count != rubric.Count ||
                scores.Select(static score => score.CriterionId)
                    .Distinct(StringComparer.Ordinal).Count() != scores.Count ||
                scores.Any(score =>
                    !rubricById.TryGetValue(score.CriterionId, out var criterion) ||
                    score.AwardedPoints < 0 ||
                    score.AwardedPoints > criterion.MaximumPoints) ||
                scores.Sum(static score => score.AwardedPoints) != totalScore)
            {
                return false;
            }

            evidence = new NyxIdChatCandidateScreeningEvidence
            {
                SourceInputRequestId = sourceInputRequestId,
                CandidateName = candidateName,
                RoleTitle = roleTitle,
                TotalScore = totalScore,
                TrackerTable = trackerTable,
                TrackerTableId = trackerTableId,
                Stage = stage,
                GuardedToolName = guardedToolName,
            };
            evidence.Rubric.AddRange(rubric);
            evidence.Scores.AddRange(scores);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseInvoice(
        JsonElement element,
        out NyxIdChatInvoiceEvidence invoice)
    {
        invoice = null!;
        if (!IsObjectWithOnly(
                element,
                "source_ordinal",
                "vendor",
                "invoice_number",
                "invoice_date",
                "amount") ||
            !TryPositiveInt32(element, "source_ordinal", out var sourceOrdinal) ||
            !TryRequiredString(element, "vendor", out var vendor) ||
            !TryRequiredString(element, "invoice_number", out var invoiceNumber) ||
            !TryRequiredString(element, "invoice_date", out var invoiceDate) ||
            !DateOnly.TryParseExact(
                invoiceDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _) ||
            !element.TryGetProperty("amount", out var amountElement) ||
            !IsObjectWithOnly(amountElement, "currency_code", "minor_units", "fraction_digits") ||
            !TryRequiredString(amountElement, "currency_code", out var currencyCode) ||
            currencyCode.Length != 3 || currencyCode.Any(static character =>
                character is < 'A' or > 'Z') ||
            !amountElement.TryGetProperty("minor_units", out var minorUnitsElement) ||
            !minorUnitsElement.TryGetInt64(out var minorUnits) || minorUnits <= 0 ||
            !amountElement.TryGetProperty("fraction_digits", out var fractionDigitsElement) ||
            !fractionDigitsElement.TryGetUInt32(out var fractionDigits) || fractionDigits > 6)
        {
            return false;
        }

        invoice = new NyxIdChatInvoiceEvidence
        {
            SourceOrdinal = sourceOrdinal,
            Vendor = vendor,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            Amount = new NyxIdChatMoneyValue
            {
                CurrencyCode = currencyCode,
                MinorUnits = minorUnits,
                FractionDigits = fractionDigits,
            },
        };
        return true;
    }

    private static bool InvoiceIdentityEquals(
        NyxIdChatInvoiceEvidence left,
        NyxIdChatInvoiceEvidence right) =>
        string.Equals(InvoiceIdentityKey(left), InvoiceIdentityKey(right),
            StringComparison.Ordinal);

    private static string InvoiceIdentityKey(NyxIdChatInvoiceEvidence invoice) =>
        string.Join('\u001f',
            invoice.Vendor.ToUpperInvariant(),
            invoice.InvoiceNumber.ToUpperInvariant(),
            invoice.InvoiceDate,
            invoice.Amount.CurrencyCode,
            invoice.Amount.MinorUnits.ToString(CultureInfo.InvariantCulture),
            invoice.Amount.FractionDigits.ToString(CultureInfo.InvariantCulture));

    private static bool IsObjectWithOnly(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        return element.EnumerateObject().All(property => allowed.Contains(property.Name));
    }

    private static bool TryRequiredString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }
        value = property.GetString()!.Trim();
        return value.Length <= 2048;
    }

    private static bool TryPositiveInt32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt32(out value) &&
               value > 0;
    }
}
