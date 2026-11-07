using System.Text.RegularExpressions;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Tests.Other;

/// <summary>
/// The pure operator-error presenter: one deterministic mapping from a structured stock/costing
/// failure to business language. These tests pin three properties the ERP screens depend on:
/// <list type="bullet">
/// <item>every persisted code has a specific, truthful destination (never a generic "something failed");</item>
/// <item>the same code can read differently by action and structured family, but never by parsing text;</item>
/// <item>nothing a warehouse operator sees contains internal ledger/costing vocabulary, a placeholder or
/// an invented value.</item>
/// </list>
/// The presenter has no database, clock or random dependency, so these are plain in-memory tests.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Shared)]
public class ErpOperatorErrorPresenterTests
{
    // ─────────────────── Every persisted code has a real destination ───────────────────

    public static TheoryData<string> AllPersistedCodes() => new()
    {
        StockLedgerErrorCodes.LedgerDisabled,
        StockLedgerErrorCodes.StockBusy,
        StockLedgerErrorCodes.RequestIdReused,
        StockLedgerErrorCodes.DocumentAlreadyPosted,
        StockLedgerErrorCodes.StaleDocument,
        StockLedgerErrorCodes.ClosedPeriod,
        StockLedgerErrorCodes.BackdatedStockEvent,
        StockLedgerErrorCodes.CountScopeFrozen,
        StockLedgerErrorCodes.InvalidStockIdentity,
        StockLedgerErrorCodes.InsufficientBaseQty,
        StockLedgerErrorCodes.InvalidUomConversion,
        StockLedgerErrorCodes.ReversalDependency,
        StockLedgerErrorCodes.ReversalAlreadyExists,
        StockLedgerErrorCodes.LedgerMismatch,
        StockLedgerErrorCodes.LegacyLineageUnverified,
        StockLedgerErrorCodes.HistoryBeforeCutover,
        StockLedgerErrorCodes.ValuationRequired,
        StockLedgerErrorCodes.CountSnapshotChanged,
        StockLedgerErrorCodes.CostPolicyMissing,
        StockLedgerErrorCodes.CostPolicyInvalid,
        StockLedgerErrorCodes.CostMethodNotImplemented
    };

    [Theory]
    [MemberData(nameof(AllPersistedCodes))]
    public void Every_persisted_code_maps_to_a_specific_family(string code)
    {
        var family = ErpOperatorErrorPresenter.ResolveFamily(new StockOperatorFailureContext
        {
            TechnicalCode = code,
            Action = ErpErrorAction.Post
        });

        Assert.NotEqual(ErpErrorFamily.UnknownFailure, family);
    }

    [Theory]
    [MemberData(nameof(AllPersistedCodes))]
    public void Every_persisted_code_produces_specific_operator_text(string code)
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = code,
            Action = ErpErrorAction.Post,
            Origin = ErpErrorOrigin.Inventory
        });

        Assert.False(string.IsNullOrWhiteSpace(error.Title));
        Assert.False(string.IsNullOrWhiteSpace(error.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(error.NextStep));
        Assert.NotEqual("The action could not be completed.", error.Explanation);
        AssertNoForbiddenVocabulary(error);
        Assert.DoesNotContain(code, error.ToOperatorText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_context_is_safe_and_still_truthful()
    {
        var error = ErpOperatorErrorPresenter.Present(null);

        Assert.Equal("Cannot complete this action.", error.Title);
        Assert.Equal("The action could not be completed.", error.Explanation);
        Assert.False(string.IsNullOrWhiteSpace(error.NextStep));
        Assert.False(error.MayRetryAfterRefresh);
        Assert.False(error.HasReference);
        AssertNoForbiddenVocabulary(error);
    }

    [Fact]
    public void Empty_context_never_renders_a_placeholder()
    {
        var text = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext()).ToOperatorText();

        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.DoesNotContain("}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("null", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────── The split code: action and phase decide the meaning ───────────────────

    [Fact]
    public void Insufficient_base_quantity_reads_by_action_not_by_code_alone()
    {
        var rollback = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Rollback,
            DocumentKind = ErpDocumentKind.GoodsReceipt,
            DocumentNumber = "1"
        });
        var post = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Post,
            DocumentKind = ErpDocumentKind.MiscellaneousIssue,
            DocumentNumber = "7"
        });

        Assert.Equal(ErpErrorFamily.HistoricalNegativeStock, ErpOperatorErrorPresenter.ResolveFamily(
            new StockOperatorFailureContext
            {
                TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
                Action = ErpErrorAction.Rollback
            }));
        Assert.Equal(ErpErrorFamily.StockShortageNow, ErpOperatorErrorPresenter.ResolveFamily(
            new StockOperatorFailureContext
            {
                TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
                Action = ErpErrorAction.Post
            }));

        Assert.Contains("historical", rollback.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("available stock", post.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(rollback.Explanation, post.Explanation);
    }

    [Fact]
    public void Replay_phase_is_reflected_in_guidance_and_code()
    {
        ErpOperatorError Build(ErpRejectionPhase phase) => ErpOperatorErrorPresenter.Present(
            new StockOperatorFailureContext
            {
                TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
                Action = ErpErrorAction.Rollback,
                Origin = ErpErrorOrigin.Inventory,
                RejectionPhase = phase,
                DocumentKind = ErpDocumentKind.GoodsReceipt,
                DocumentNumber = "1"
            });

        var removing = Build(ErpRejectionPhase.Removing);
        var rewinding = Build(ErpRejectionPhase.Rewinding);
        var replaying = Build(ErpRejectionPhase.Replaying);

        Assert.Equal("INVENTORY.HISTORICAL_NEGATIVE_STOCK_REMOVING", removing.PresentationCode);
        Assert.Equal("INVENTORY.HISTORICAL_NEGATIVE_STOCK_REWINDING", rewinding.PresentationCode);
        Assert.Equal("INVENTORY.HISTORICAL_NEGATIVE_STOCK_REPLAYING", replaying.PresentationCode);
        Assert.Equal(3, new[] { removing, rewinding, replaying }.Select(x => x.NextStep).Distinct().Count());
        Assert.Equal(3, new[] { removing, rewinding, replaying }.Select(x => x.Explanation).Distinct().Count());
    }

    [Fact]
    public void Structured_family_wins_over_the_code_default()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.ValuationRequired,
            Family = ErpErrorFamily.CostStateNeedsReview,
            Action = ErpErrorAction.Post
        });

        Assert.Equal("STOCK.COST_STATE_NEEDS_REVIEW", error.PresentationCode);
        Assert.Contains("do not agree", error.Explanation, StringComparison.Ordinal);
    }

    // ─────────────────── Headline names only what was proven ───────────────────

    [Fact]
    public void Headline_names_a_known_document()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Rollback,
            DocumentKind = ErpDocumentKind.GoodsReceipt,
            DocumentNumber = "1"
        });

        Assert.Equal("Cannot roll back Goods Receipt 1.", error.Title);
    }

    [Fact]
    public void Unknown_document_kind_never_leaks_a_stored_kind_or_guid()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.UnsupportedReplay,
            Action = ErpErrorAction.Post,
            DocumentNumber = "GR-1"
        });

        Assert.Equal("Cannot post document GR-1.", error.Title);
        Assert.DoesNotContain("_", error.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_document_without_number_stays_readable()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.ClosedPeriod,
            Action = ErpErrorAction.Post,
            DocumentKind = ErpDocumentKind.PurchaseInvoice
        });

        Assert.Equal("Cannot post this purchase invoice.", error.Title);
    }

    // ─────────────────── Details: shown only when proven ───────────────────

    [Fact]
    public void Quantities_carry_the_base_uom_and_inventory_scale()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Post,
            Origin = ErpErrorOrigin.Inventory,
            ItemCode = "FG-100",
            ItemDescription = "Blue widget",
            Uom = "KG",
            Available = 12.5m,
            Required = 1234.56789m,
            Shortage = 1222.06789m
        });

        Assert.Equal("12.5 KG", Value(error, "Available"));
        Assert.Equal("1,234.5679 KG", Value(error, "Required"));
        Assert.Equal("1,222.0679 KG", Value(error, "Shortage"));
        Assert.Equal("FG-100 — Blue widget", Value(error, "Item"));
    }

    [Fact]
    public void Unproven_quantities_are_omitted_not_guessed()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Post
        });

        Assert.Null(Value(error, "Available"));
        Assert.Null(Value(error, "Required"));
        Assert.Null(Value(error, "Shortage"));
    }

    [Fact]
    public void Cost_rows_are_hidden_without_cost_rights()
    {
        var context = new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.MissingReceiptCost,
            Action = ErpErrorAction.Post,
            ExtraDetails = [new ErpOperatorErrorDetail("Unit cost", "12.50", IsMonetary: true)]
        };

        Assert.Null(Value(ErpOperatorErrorPresenter.Present(context), "Unit cost"));

        var withRights = ErpOperatorErrorPresenter.Present(context with { CanViewCost = true });
        Assert.Equal("12.50", Value(withRights, "Unit cost"));
    }

    [Fact]
    public void Long_or_multiline_producer_values_are_collapsed_and_capped()
    {
        var description = "Widget\r\nwith a very long description " + new string('x', 300);
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.MissingReceiptCost,
            Action = ErpErrorAction.Post,
            ItemDescription = description
        });

        var item = Assert.Single(error.Details);
        Assert.DoesNotContain('\n', item.Value);
        Assert.DoesNotContain('\r', item.Value);
        Assert.True(item.Value.Length <= ErpOperatorErrorPresenter.MaxValueLength);
    }

    [Fact]
    public void Detail_and_related_document_counts_are_bounded()
    {
        var extras = Enumerable.Range(1, 40)
            .Select(i => new ErpOperatorErrorDetail($"Field {i}", $"Value {i}"))
            .ToArray();
        var links = Enumerable.Range(1, 20)
            .Select(i => new ErpRelatedDocumentHint("Stock Transfer", $"ST-{i}", "/inventory/transfer"))
            .ToArray();

        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.ReconciliationRequired,
            Action = ErpErrorAction.Post,
            ExtraDetails = extras,
            RelatedDocuments = links
        });

        Assert.True(error.Details.Count <= ErpOperatorErrorPresenter.MaxDetails);
        Assert.Equal(
            ErpOperatorErrorPresenter.MaxRelatedDocuments,
            error.RelatedDocuments.Count);
    }

    [Fact]
    public void Affected_transaction_is_named_only_when_ownership_was_proven()
    {
        var unknown = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.HistoricalNegativeStock,
            Action = ErpErrorAction.Rollback,
            AffectedTransactionDate = new DateTime(2026, 10, 2)
        });
        Assert.Null(Value(unknown, "Affected transaction"));
        Assert.Equal("02/10/2026", Value(unknown, "Affected transaction date"));

        var proven = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.HistoricalNegativeStock,
            Action = ErpErrorAction.Rollback,
            AffectedDocumentKind = ErpDocumentKind.DeliveryOrder,
            AffectedDocumentNumber = "DO-55",
            AffectedTransactionDate = new DateTime(2026, 10, 2)
        });
        Assert.Equal("Delivery Order DO-55, dated 02/10/2026", Value(proven, "Affected transaction"));
        Assert.Contains("02/10/2026", proven.Explanation, StringComparison.Ordinal);
    }

    // ─────────────────── Dates ───────────────────

    [Fact]
    public void Closed_period_reports_the_date_and_the_closed_through_date()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.ClosedPeriod,
            Action = ErpErrorAction.Post,
            DocumentKind = ErpDocumentKind.StockAdjustment,
            DocumentNumber = "8",
            DocumentDate = new DateTime(2026, 9, 30),
            ClosedThrough = new DateTime(2026, 10, 31)
        });

        Assert.Contains("30/09/2026", error.Explanation, StringComparison.Ordinal);
        Assert.Contains("31/10/2026", error.Explanation, StringComparison.Ordinal);
        Assert.Equal("30/09/2026", Value(error, "Document date"));
    }

    [Fact]
    public void Missing_dates_never_render_the_default_date()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.ClosedPeriod,
            Action = ErpErrorAction.Post,
            DocumentDate = default
        });

        Assert.DoesNotContain("0001", error.ToOperatorText(), StringComparison.Ordinal);
        Assert.Equal("This date falls in a closed inventory period.", error.Explanation);
    }

    // ─────────────────── Advisory retry and reference ───────────────────

    [Fact]
    public void Retry_hint_is_limited_to_transient_families()
    {
        foreach (var family in Enum.GetValues<ErpErrorFamily>())
        {
            var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
            {
                Family = family,
                Action = ErpErrorAction.Post,
                Retryable = true
            });

            var expected = family is ErpErrorFamily.StockBusy or ErpErrorFamily.StaleDocument;
            Assert.Equal(expected, error.MayRetryAfterRefresh);
        }
    }

    [Fact]
    public void Reference_is_shown_only_when_issued()
    {
        var without = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.UnsupportedReplay,
            Action = ErpErrorAction.Post
        });
        Assert.False(without.HasReference);
        Assert.DoesNotContain("Reference:", without.ToOperatorText(), StringComparison.Ordinal);

        var with = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.UnsupportedReplay,
            Action = ErpErrorAction.Post,
            ReferenceId = " GRN-ERR-000123 "
        });
        Assert.True(with.HasReference);
        Assert.Equal("GRN-ERR-000123", with.ReferenceId);
        Assert.Contains("Reference: GRN-ERR-000123.", with.ToOperatorText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Same_context_always_produces_the_same_text()
    {
        var context = new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Rollback,
            Origin = ErpErrorOrigin.Inventory,
            RejectionPhase = ErpRejectionPhase.Replaying,
            DocumentKind = ErpDocumentKind.GoodsReceipt,
            DocumentNumber = "1",
            ItemCode = "RM-1",
            Warehouse = "MAIN",
            Uom = "KG",
            Available = 3m,
            Required = 5m,
            Shortage = 2m,
            AffectedTransactionDate = new DateTime(2026, 10, 2),
            ReferenceId = "REF-1"
        };

        var first = ErpOperatorErrorPresenter.Present(context);
        var second = ErpOperatorErrorPresenter.Present(context);

        Assert.Equal(first.ToOperatorText(), second.ToOperatorText());
        Assert.Equal(first.PresentationCode, second.PresentationCode);
        Assert.Equal(first.Details.Select(x => x.Label), second.Details.Select(x => x.Label));
    }

    // ─────────────────── Wording contracts ───────────────────

    [Fact]
    public void Presentation_code_is_a_stable_locale_key()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Post,
            Origin = ErpErrorOrigin.Inventory
        });

        Assert.Equal("INVENTORY.STOCK_SHORTAGE_NOW", error.PresentationCode);
        Assert.Matches(new Regex("^[A-Z]+(\\.[A-Z_]+)+$"), error.PresentationCode);
    }

    [Fact]
    public void Production_shortage_names_production()
    {
        var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = StockLedgerErrorCodes.InsufficientBaseQty,
            Action = ErpErrorAction.Post,
            Origin = ErpErrorOrigin.Production,
            ItemCode = "RM-77"
        });

        Assert.Contains("Production", error.Explanation, StringComparison.Ordinal);
        Assert.Contains("RM-77", error.Explanation, StringComparison.Ordinal);
        Assert.Equal("PRODUCTION.STOCK_SHORTAGE_NOW", error.PresentationCode);
    }

    [Fact]
    public void Already_processed_wording_matches_which_record_was_duplicated()
    {
        string Explain(string code) => ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = code,
            Action = ErpErrorAction.Post
        }).Explanation;

        Assert.Equal("This document is already posted.", Explain(StockLedgerErrorCodes.DocumentAlreadyPosted));
        Assert.Equal("This transaction has already been reversed.", Explain(StockLedgerErrorCodes.ReversalAlreadyExists));
        Assert.Equal("This request may already have been processed.", Explain(StockLedgerErrorCodes.RequestIdReused));
    }

    [Fact]
    public void Every_family_produces_a_complete_message()
    {
        var generic = "The action could not be completed.";
        foreach (var family in Enum.GetValues<ErpErrorFamily>())
        {
            var error = ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
            {
                Family = family,
                Action = ErpErrorAction.Post,
                Origin = ErpErrorOrigin.Inventory,
                DocumentKind = ErpDocumentKind.GoodsReceipt,
                DocumentNumber = "1"
            });

            Assert.False(string.IsNullOrWhiteSpace(error.Title));
            Assert.False(string.IsNullOrWhiteSpace(error.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(error.NextStep));
            Assert.StartsWith("Cannot post Goods Receipt 1.", error.Title, StringComparison.Ordinal);
            if (family != ErpErrorFamily.UnknownFailure)
            {
                Assert.False(
                    error.Explanation == generic,
                    $"Family {family} produced only the generic explanation.");
            }
            AssertNoForbiddenVocabulary(error);
        }
    }

    [Fact]
    public void Safe_collapses_whitespace_and_returns_null_for_blank()
    {
        Assert.Null(ErpOperatorErrorPresenter.Safe("   ", 20));
        Assert.Null(ErpOperatorErrorPresenter.Safe(null, 20));
        Assert.Equal("a b", ErpOperatorErrorPresenter.Safe("  a \t\r\n b  ", 20));
    }

    // ─────────────────── helpers ───────────────────

    private static string? Value(ErpOperatorError error, string label) =>
        error.Details.FirstOrDefault(x => x.Label == label)?.Value;

    private static readonly Regex ForbiddenVocabulary = new(
        @"\b(slices?|sliced|piles?|epochs?|lineage|sealed|watermarks?|rowversion|tails?|events?|" +
        @"hash(es|ed)?|canonical\w*|exceptions?|NullReference\w*|SQL|FIFO|LIFO|WAC|ledger|" +
        @"replay(s|ed|ing)?|stack\s*trace|posting\s*id|history\s*id|tenant\s*id)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void AssertNoForbiddenVocabulary(ErpOperatorError error)
    {
        var text = string.Join(
            " ",
            new[] { error.Title, error.Explanation, error.NextStep }
                .Concat(error.Details.Select(x => $"{x.Label}: {x.Value}")));

        var match = ForbiddenVocabulary.Match(text);
        Assert.False(match.Success, $"Operator text contains '{match.Value}': {text}");
    }
}
