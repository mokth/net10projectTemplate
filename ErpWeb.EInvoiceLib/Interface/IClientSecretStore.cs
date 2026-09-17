namespace ErpWeb.EInvoiceLib.Interface
{
    public interface IClientSecretStore
    {

        string getCertPass();
        string getCertPath();
        string getJSonFilePath();
        string getOnBehalfTin();
        string getPortalUrl();
        string getSecretID();
        string getSecretKey();
        string getUrl();
        string getDocumentVersion();

       void setOnBehalfTin(string tinno);
       void setTokenString(string token);
        string getTokenString();

        /// <summary>
        /// Applies the per-company e-Invoice credentials for the current request. Values passed as
        /// null/blank fall back to the application-level <c>Einvoice:*</c> settings.
        /// <para>
        /// The store is registered as scoped, so these values never leak between concurrent
        /// requests or companies.
        /// </para>
        /// </summary>
        void setCompanyCredentials(string? secretId,
                                   string? secretKey,
                                   string? certPath,
                                   string? certPass,
                                   string? onBehalfTin,
                                   string? documentVersion);

        /// <summary>
        /// Clears any per-company overrides, falling back to the application-level settings.
        /// </summary>
        void clearCompanyCredentials();

    }
}
