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

        public virtual DbSet<EInvDocSubmission> EInvDocSubmissions { get; set; }

        //public DbSet<SaDocSubmit> SaDocSubmits { get; set; }

        //public DbSet<IvEDocumentFlow> ivEDocumentFlows { get; set; }

        //public DbSet<ivEDocumentType> ivEDocumentTypes { get; set; }

        //public DbSet<ivEDocumentTypeVersion> ivEDocumentTypeVersions { get; set; }

        //public DbSet<EInvNotification> EInvNotifications { get; set; }

        // public DbSet<EInvNotificationDelivery> EInvNotificationDeliverys { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            //trigger
            modelBuilder.Entity<EInvDocSubmission>(entity =>
            {
                entity.ToTable(tb => tb.HasTrigger("tr_EInvDocSubmission_ForInsert"));
            });
        }

        //modelBuilder.Entity<Customer>()
        //    .ToTable(tb => tb.HasTrigger("Trigger1").HasTrigger("Trigger2"));
    }
}
