using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

/// <summary>
/// Mapping for the legacy physical table <c>dbo.EInvDocSubmission</c>.
///
/// <para>
/// The <b>column shape</b> (names, widths, store types) lives on
/// <see cref="EInvDocSubmission"/> as attributes, because that entity is a deliberate 1:1 mirror of a
/// legacy table verified against <c>docs/einvoice-history-phase0-findings.txt</c>. This class owns what
/// belongs to the model rather than the table: the key, the uniqueness contract and the read indexes.
/// </para>
/// </summary>
public class EInvDocSubmissionConfiguration : IEntityTypeConfiguration<EInvDocSubmission>
{
    /// <summary>
    /// The authority for "one row per submitted document within one submission" (plan R1/R4/D-9).
    /// A batched <c>SubmitManyAsync</c> shares one <c>submissionUUID</c> and can mix CN + DN, which is
    /// exactly why the key needs <c>documentType</c> + <c>documentNo</c> as well.
    /// <para>
    /// The database enforces this; the writer never assumes it. A duplicate-key race is recovered
    /// deterministically (plan D-10) rather than surfaced to the operator.
    /// </para>
    /// </summary>
    public const string SubmissionKeyIndexName = "UX_EInvDocSubmission_Submission";

    /// <summary>
    /// Serves "the current row for a document is the highest <c>ID</c> for that document" (plan R5).
    /// A re-submission after REJECTED/CANCELLED creates an extra row, so a reader that wants the latest
    /// state must not assume a single row per document number.
    /// </summary>
    public const string DocumentIndexName = "IX_EInvDocSubmission_Document";

    public void Configure(EntityTypeBuilder<EInvDocSubmission> builder)
    {
        builder.ToTable("EInvDocSubmission", "dbo");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        // DELIBERATELY NO HasTrigger(...).
        //
        // The legacy ErpWeb.EInvoiceLib context declared HasTrigger("tr_EInvDocSubmission_ForInsert").
        // Phase 0 probed sys.triggers for this table and found NO trigger, so that declaration is stale
        // for this database: declaring it here would corrupt EF's generated SQL (an OUTPUT clause is
        // illegal on a triggered table) for no reason.
        //
        // If a trigger is ever added to this table, HasTrigger MUST be declared here or every INSERT
        // fails with Msg 334. scripts/discover-einvdocsubmission.sql re-checks this - run it after any
        // DBA change to the table.

        builder.HasIndex(e => new { e.CompanyId, e.SubmissionUuid, e.DocumentType, e.DocumentNo })
            .IsUnique()
            .HasDatabaseName(SubmissionKeyIndexName);

        builder.HasIndex(e => new { e.CompanyId, e.DocumentType, e.DocumentNo })
            .HasDatabaseName(DocumentIndexName);
    }
}
