using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Repository;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.EInvoiceLib.Store
{
    /// <summary>
    /// Resolves the MyInvois connection settings for the current request.
    /// <para>
    /// Application-level environment values come from the <c>Einvoice:*</c> configuration section.
    /// Per-company values (client id/secret, certificate, on-behalf TIN, document version) are
    /// applied through <see cref="setCompanyCredentials"/> and are held as <b>instance</b> state so
    /// that concurrent requests for different companies cannot share credentials or tokens.
    /// </para>
    /// </summary>
    public class ClientSecretStore : IClientSecretStore
    {
        private readonly ILogger<ClientSecretStore> _logger;
        private readonly IConfiguration _configuration;

        // Instance state (was static) - scoped per request for multi-company isolation.
        private string? _onbehalfTin = "";
        private string? _tokenstring = "";

        // Per-company overrides; null/blank means "use application setting".
        private string? _companySecretId;
        private string? _companySecretKey;
        private string? _companyCertPath;
        private string? _companyCertPass;
        private string? _companyOnBehalfTin;
        private string? _companyDocumentVersion;

        public ClientSecretStore(ILogger<ClientSecretStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        private string? GetSetting(string key)
        {
            try
            {
                return _configuration.GetSection(key).Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                return null;
            }
        }

        public string getSecretID()
        {
            return string.IsNullOrWhiteSpace(_companySecretId)
                ? GetSetting("Einvoice:EInv_SecretID") ?? ""
                : _companySecretId!;
        }

        public string getSecretKey()
        {
            return string.IsNullOrWhiteSpace(_companySecretKey)
                ? GetSetting("Einvoice:EInv_SecretKey") ?? ""
                : _companySecretKey!;
        }


        public void setOnBehalfTin(string tinno)
        {
            _onbehalfTin = tinno;
            _logger.LogDebug("Set Onbehalf Tin " + tinno);
        }

        public void setTokenString(string token)
        {
            _tokenstring = token;
        }

        public string getTokenString()
        {
            return _tokenstring ?? "";
        }

        public string getOnBehalfTin()
        {
            string key = "";
            try
            {
                key = GetSetting("Einvoice:EInv_onbehalfof") ?? "";
                if (key.ToLower() == "direct")
                {
                    key = "";
                }
                else
                {
                    // Per-company on-behalf TIN takes precedence, then the value set for this request.
                    if (!string.IsNullOrWhiteSpace(_companyOnBehalfTin))
                    {
                        key = _companyOnBehalfTin!;
                    }
                    else if (!string.IsNullOrEmpty(_onbehalfTin))
                    {
                        key = _onbehalfTin;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
            }
            return key;
        }

        public string getUrl()
        {
            return GetSetting("Einvoice:EInv_Url") ?? "";
        }

        public string getPortalUrl()
        {
            return GetSetting("Einvoice:EInv_portal") ?? "";
        }

        public string getCertPath()
        {
            return string.IsNullOrWhiteSpace(_companyCertPath)
                ? GetSetting("Einvoice:EInv_CertPath") ?? ""
                : _companyCertPath!;
        }

        public string getCertPass()
        {
            return string.IsNullOrWhiteSpace(_companyCertPass)
                ? GetSetting("Einvoice:EInv_CertPass") ?? ""
                : _companyCertPass!;
        }

        public string getJSonFilePath()
        {
            return GetSetting("Einvoice:EInv_JsonPath") ?? "";
        }

        public string getDocumentVersion()
        {
            string key = "1.0";
            try
            {
                key = GetSetting("Einvoice:EInv_docversion") ?? "1.0";
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = "1.0";
                }
                if (!string.IsNullOrWhiteSpace(_companyDocumentVersion))
                {
                    key = _companyDocumentVersion!;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
            }
            return key;
        }

        public void setCompanyCredentials(string? secretId,
                                          string? secretKey,
                                          string? certPath,
                                          string? certPass,
                                          string? onBehalfTin,
                                          string? documentVersion)
        {
            _companySecretId = secretId;
            _companySecretKey = secretKey;
            _companyCertPath = certPath;
            _companyCertPass = certPass;
            _companyOnBehalfTin = onBehalfTin;
            _companyDocumentVersion = documentVersion;

            if (!string.IsNullOrWhiteSpace(onBehalfTin))
            {
                _onbehalfTin = onBehalfTin;
            }

            // Never log credential values.
            _logger.LogDebug("Applied per-company e-Invoice credentials.");
        }

        public void clearCompanyCredentials()
        {
            _companySecretId = null;
            _companySecretKey = null;
            _companyCertPath = null;
            _companyCertPass = null;
            _companyOnBehalfTin = null;
            _companyDocumentVersion = null;
            _onbehalfTin = "";
            _tokenstring = "";
        }
    }
}
