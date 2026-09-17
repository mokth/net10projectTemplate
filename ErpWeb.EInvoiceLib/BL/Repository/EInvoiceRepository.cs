using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.BL.DataContext;
using ErpWeb.EInvoiceLib.BL.Entity;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Model.Document;
using System.Data.Common;


namespace ErpWeb.EInvoiceLib.BL.Repository
{
    public class EInvoiceRepository : IDisposable, IEInvoiceRepository
    {
        //private readonly EInvoiceContext _context;
        
        private readonly ILogger<EInvoiceRepository> _logger;
        private readonly IDbContextFactory<EInvoiceContext> _dbFactory;

        public EInvoiceRepository(IDbContextFactory<EInvoiceContext> dbFactory,
            ILogger<EInvoiceRepository> logger)
        {
            _dbFactory = dbFactory;

            _logger = logger;
           // _context = _dbFactory.CreateDbContext();
        }

        public void Dispose()
        {
           // _context?.Dispose();

        }

        protected async Task<EInvoiceContext> CreateDbContext()
        {
            var db = _dbFactory.CreateDbContext();
            return db;
        }

        public async Task InsertAdTokenAsync(EInvToken token)
        {
            using (var db = await CreateDbContext())
            {
                var found = await db.EInvTokens.FirstOrDefaultAsync();
                if (found != null)
                {
                    found.scope = token.scope;
                    found.expires_in = token.expires_in;
                    found.access_token = token.access_token;
                    found.token_type = token.token_type;
                    found.created = DateTime.Now;
                }
                else
                {
                    db.EInvTokens.Add(token);
                }
                await db.SaveChangesAsync();
            }
        }

        public async Task InsertAdTokenOwnAsync(EInvTokenOwn token)
        {
            using (var db = await CreateDbContext())
            {
                var found = await db.EInvTokenOwns.FirstOrDefaultAsync();
                if (found != null)
                {
                    found.scope = token.scope;
                    found.expires_in = token.expires_in;
                    found.access_token = token.access_token;
                    found.token_type = token.token_type;
                    found.created = DateTime.Now;
                }
                else
                {
                    db.EInvTokenOwns.Add(token);
                }
                await db.SaveChangesAsync();
            }
        }

        public async Task<EInvToken?> GetAdTokenAsync()
        {
            EInvToken? token = null;
            try
            {
                using (var db = await CreateDbContext())
                {
                    token = await db.EInvTokens.FirstOrDefaultAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting token from database");
                _logger.LogError(ex.Message, ex);
            }
            return token;
        }

        public async Task<EInvTokenOwn?> GetAdTokenOwnAsync()
        {
            EInvTokenOwn? token = null;
            try
            {
                using (var db = await CreateDbContext())
                {
                    token = await db.EInvTokenOwns.FirstOrDefaultAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting token from database");
                _logger.LogError(ex.Message, ex);
            }
            return token;
        }

        public async Task<string> GetTokenAsync()
        {
            string access_token = "";
            try
            {
                var token = await GetAdTokenAsync();
                if (token != null)
                {
                    DateTime created = token.created.Value;
                    var expires_in = token.expires_in.Value;
                    DateTime expired = created.AddSeconds(expires_in).AddMinutes(-10);
                    if (expired > DateTime.Now)
                    {
                        access_token = token.access_token ?? "";
                    }
                    else
                    {
                        _logger.LogError("token has expired...");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting token from database");
                _logger.LogError(ex.Message, ex);
            }
            return access_token;
        }

        public async Task<string> GetTokenOwnAsync()
        {
            string access_token = "";
            try
            {
                var token = await GetAdTokenOwnAsync();
                if (token != null)
                {
                    DateTime created = token.created.Value;
                    var expires_in = token.expires_in.Value;
                    DateTime expired = created.AddSeconds(expires_in).AddMinutes(-10);
                    if (expired > DateTime.Now)
                    {
                        access_token = token.access_token ?? "";
                    }
                    else
                    {
                        _logger.LogError("token has expired...");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting token from database");
                _logger.LogError(ex.Message, ex);
            }
            return access_token;
        }

        public async Task AddSubmissionAsync(SuccessSubmit submit)
        {
            try
            {
                EInvDocSubmission doc = new EInvDocSubmission();
                doc.submissionUUID = submit.submissionUID;
                doc.companyID = submit.companyID;
                doc.documentID = submit.documentID;
                doc.documentNo = submit.documentNo;
                doc.documentType = "POS";

                if (submit.acceptedDocuments.Count > 0)
                {
                    doc.uuid = submit.acceptedDocuments[0].uuid;
                    doc.internalId = submit.acceptedDocuments[0].invoiceCodeNumber;
                }
                using (var db = await CreateDbContext())
                {
                    var found = await db.EInvDocSubmissions
                        .Where(x => x.submissionUUID == doc.submissionUUID && x.uuid == doc.uuid)
                        .FirstOrDefaultAsync();
                    if (found == null)
                    {
                        doc.status = "Submitted";
                        doc.dateTimeIssued = DateTime.Now;
                        db.Add(doc);
                        await db.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error adding submission to database");
                _logger.LogError(ex.Message, ex);
            }
        }

        public async Task<EInvDocSubmission?> getSubmissionAsync(string submissionID, string uuid)
        {
            EInvDocSubmission? doc = null;
            try
            {
                using (var db = await CreateDbContext())
                {
                    doc = await db.EInvDocSubmissions
                        .Where(x => x.submissionUUID == submissionID && x.uuid == uuid)
                        .FirstOrDefaultAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting submission from database");
                _logger.LogError(ex.Message, ex);
            }
            return doc;
        }

        public async Task<EInvDocSubmission?> getSubmissionByCompIdAsync(string companyID, string documentNo, int docID)
        {
            EInvDocSubmission? doc = null;
            try
            {
                using (var db = await CreateDbContext())
                {
                    doc = await db.EInvDocSubmissions
                        .Where(x => x.companyID == companyID && x.documentNo == documentNo && x.documentID == docID)
                        .FirstOrDefaultAsync();

                    if (doc == null)
                    {
                        doc = await db.EInvDocSubmissions
                            .Where(x => x.internalId == documentNo)
                            .FirstOrDefaultAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting submission from database");
                _logger.LogError(ex.Message, ex);
            }
            return doc;
        }

        public async Task UpdateSubmissionAsync(Submission submit)
        {
            try
            {
                string uuid = "";
                if (submit.documentSummary.Count > 0)
                {
                    uuid = submit.documentSummary[0].uuid;
                }
                using (var db = await CreateDbContext())
                {
                    var found = await db.EInvDocSubmissions
                        .Where(x => x.submissionUUID == submit.submissionUid && x.uuid == uuid)
                        .FirstOrDefaultAsync();
                    if (found != null)
                    {
                        found.issuerName = submit.documentSummary[0].issuerName;
                        found.cancelDateTime = submit.documentSummary[0].cancelDateTime;
                        found.createdByUserId = submit.documentSummary[0].createdByUserId;
                        found.dateTimeIssued = submit.documentSummary[0].dateTimeIssued;
                        found.dateTimeReceived = submit.documentSummary[0].dateTimeReceived;
                        found.dateTimeValidated = submit.documentSummary[0].dateTimeValidated;
                        found.document = submit.documentSummary[0].document;
                        found.documentStatusReason = submit.documentSummary[0].documentStatusReason;
                        found.internalId = submit.documentSummary[0].internalId;
                        found.issuerName = submit.documentSummary[0].issuerName;
                        found.issuerTin = submit.documentSummary[0].issuerTin;
                        found.longId = submit.documentSummary[0].longId;
                        found.netAmount = submit.documentSummary[0].netAmount;
                        found.receiverId = submit.documentSummary[0].receiverId;
                        found.receiverName = submit.documentSummary[0].receiverName;
                        found.rejectRequestDateTime = submit.documentSummary[0].rejectRequestDateTime;
                        found.total = submit.documentSummary[0].total;
                        found.totalDiscount = submit.documentSummary[0].totalDiscount;
                        found.totalSales = submit.documentSummary[0].totalSales;
                        found.typeName = submit.documentSummary[0].typeName;
                        found.typeVersionName = submit.documentSummary[0].typeVersionName;
                        found.total = submit.documentSummary[0].total;
                        found.status = submit.documentSummary[0].status;
                        await db.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error updating submission");
                _logger.LogError(ex.Message, ex);
            }
        }
    }
}
