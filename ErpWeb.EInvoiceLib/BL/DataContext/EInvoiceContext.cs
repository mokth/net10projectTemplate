using Microsoft.EntityFrameworkCore;

using ErpWeb.EInvoiceLib.BL.Entity;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.EInvoiceLib.BL.DataContext
{

    public class EInvoiceContext : DbContext
    {
        public DbContextOptions<EInvoiceContext> sqloptions;
      

        public EInvoiceContext(DbContextOptions<EInvoiceContext> options) : base(options)
        {
            sqloptions = options;
        }



        public virtual DbSet<EInvToken> EInvTokens { get; set; }
        public virtual DbSet<EInvTokenOwn> EInvTokenOwns { get; set; }

        // dbo.EInvDocSubmission was deliberately REMOVED from this context (plan Phase 4): ErpWeb now
        // owns that table through ErpWeb.Model/Entities/Sales/EInvDocSubmission.cs + AppDbContext.
        // Mapping it here would create a second EF owner writing the same rows.

        //public DbSet<SaDocSubmit> SaDocSubmits { get; set; }

        //public DbSet<IvEDocumentFlow> ivEDocumentFlows { get; set; }

        //public DbSet<ivEDocumentType> ivEDocumentTypes { get; set; }

        //public DbSet<ivEDocumentTypeVersion> ivEDocumentTypeVersions { get; set; }

        //public DbSet<EInvNotification> EInvNotifications { get; set; }

        // public DbSet<EInvNotificationDelivery> EInvNotificationDeliverys { get; set; }

        // No OnModelCreating override on purpose.
        //
        // Its only content was the trigger registration for the submission table:
        //     modelBuilder.Entity<EInvDocSubmission>(entity =>
        //         entity.ToTable(tb => tb.HasTrigger("tr_EInvDocSubmission_ForInsert")));
        // Phase 0 probed sys.triggers on the live database and found NO trigger on
        // dbo.EInvDocSubmission, so that declaration was stale - and the entity is no longer mapped here
        // anyway. Re-add an override only if this context maps an entity again.
    }
}
