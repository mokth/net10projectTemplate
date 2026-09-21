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

        // The four dbo.EInvDocSubmission methods were REMOVED from this repository (plan Phase 4):
        // AddSubmissionAsync, getSubmissionAsync, getSubmissionByCompIdAsync and UpdateSubmissionAsync.
        //
        // Why removing them is both safe and desirable:
        //   * They read/wrote the same physical table through a DIFFERENT, narrower EF model
        //     (BL/Entity/SaDocSubmission.cs), and AddSubmissionAsync hard-coded documentType = "POS".
        //   * getSubmissionByCompIdAsync carried a fallback that matched internalId == documentNo, which
        //     is not a valid document identity.
        //   * ErpWeb now owns the table through AppDbContext + EInvoiceSubmissionWriter, with the unique
        //     index (companyID, submissionUUID, documentType, documentNo) as the single authority.
        //
        // Token access (above) and the LHDN API client are deliberately untouched.
    }
}
