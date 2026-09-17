using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.GenerateDoc;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.EInvoiceLib.Model.InputData;
using ErpWeb.EInvoiceLib.Repository;
using ErpWeb.EInvoiceLib.Store;
using ErpWeb.EInvoiceLib.Utility;
using Microsoft.Extensions.Configuration;
using EInvoiceAPI.Utility;

namespace ErpWeb.EInvoiceLib.SubmitDoc
{
    public class SubmitDocumentHelper : ISubmitDocumentHelper
    {
        private readonly IE_InvoiceRepository _serviceApi;
        private readonly IEInvoiceRepository _repo;
        private readonly ILogger<SubmitDocumentHelper> _logger;
        private readonly IClientSecretStore _clientSecretStore;
       

        public SubmitDocumentHelper(IE_InvoiceRepository serviceApi,
                                    IEInvoiceRepository repo,
                                    ILogger<SubmitDocumentHelper> logger,
                                    IClientSecretStore clientSecretStore)
        {
            _repo = repo;
            _clientSecretStore = clientSecretStore;
            _serviceApi = serviceApi;
            _logger = logger;
        }

        public void SetOnBehalfTin(string onBehalf)
        {

            _clientSecretStore.setOnBehalfTin(onBehalf);
        }
        public async Task<GeneralResult<bool>> ValidateTin(string tin, IDType idType, string idValue)
        {

            GeneralResult<bool> result = new GeneralResult<bool>();
            result = await _serviceApi.ValidateTin(tin, idType.ToString(), idValue);

            return result;
        }

        public async Task<GeneralResult<List<TINInfo>>> SearchTin(SearchTINInput query)
        {
            GeneralResult<List<TINInfo>> result = new GeneralResult<List<TINInfo>>();
            result = await _serviceApi.SearchTin(query);

            return result;
        }

        public async Task<GeneralResult<SuccessSubmit>> SubmitInvoices(List<DocumentHeader> infos)
        {
            GeneralResult<SuccessSubmit> result = new GeneralResult<SuccessSubmit>();

            string errorMsg = "";
            try
            {
                List<Documents> docs = generateInvoices(infos, ref errorMsg);
                SubmitDocument submit = new SubmitDocument();
                submit.documents = docs.ToList();

                var settings = new JsonSerializerSettings
                {
                    DateFormatString = "yyyy-MM-ddTH:mmZ",
                    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                    NullValueHandling = NullValueHandling.Ignore
                };
                var json = JsonConvert.SerializeObject(submit, settings);
                //File.WriteAllText("c:\\test3\\submit.json",json);
                wrieSubJsonFile(submit.documents[0].codeNumber, json);
                string suppTin = infos[0].Supplier.TinNo;
                result = await _serviceApi.SubmitDocument(submit);

                //UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
                //if (result.IsSuccess)
                //{
                //    invupdateHlp.UpdateInovicesSuccess(result.result, infos);
                //}
                //else
                //{
                //    invupdateHlp.UpdateInovicesFail(result, infos);
                //}

                //Console.WriteLine(result.error);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error generating invoice!");
                _logger.LogError(ex.Message, ex);
                result.error = ex.Message;
                result.errorCode = "100";
            }
            return result;
        }

        public async Task<GeneralResult<SuccessSubmit>> SubmitInvoicesWithTIN(List<DocumentHeader> infos,string TINNO)
        {
            GeneralResult<SuccessSubmit> result = new GeneralResult<SuccessSubmit>();

            string errorMsg = "";
            try
            {
                List<Documents> docs = generateInvoices(infos, ref errorMsg);
                SubmitDocument submit = new SubmitDocument();
                submit.documents = docs.ToList();

                var settings = new JsonSerializerSettings
                {
                    DateFormatString = "yyyy-MM-ddTH:mmZ",
                    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                    NullValueHandling = NullValueHandling.Ignore
                };
                var json = JsonConvert.SerializeObject(submit, settings);
                //File.WriteAllText("c:\\test3\\submit.json",json);
                wrieSubJsonFile(submit.documents[0].codeNumber, json);
               
                result = await _serviceApi.SubmitDocumentWithTin(submit, TINNO);

                //UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
                //if (result.IsSuccess)
                //{
                //    invupdateHlp.UpdateInovicesSuccess(result.result, infos);
                //}
                //else
                //{
                //    invupdateHlp.UpdateInovicesFail(result, infos);
                //}

                //Console.WriteLine(result.error);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error generating invoice!");
                _logger.LogError(ex.Message, ex);
                result.error = ex.Message;
                result.errorCode = "100";
            }
            return result;
        }

        public async Task<GeneralResult<SuccessSubmit>> SubmitCreditDebitNotes(List<DocumentHeader> infos)
        {
            GeneralResult<SuccessSubmit> result = new GeneralResult<SuccessSubmit>();


            try
            {
                string errorMsg = "";
                List<Documents> docs = generateCreditDebitNotes(infos, ref errorMsg);
                SubmitDocument submit = new SubmitDocument();
                submit.documents = docs.ToList();

                var settings = new JsonSerializerSettings
                {
                    DateFormatString = "yyyy-MM-ddTH:mmZ",
                    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                    NullValueHandling = NullValueHandling.Ignore
                };
                var json = JsonConvert.SerializeObject(submit, settings);

                result = await _serviceApi.SubmitDocument(submit);

                //UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
                //if (result.IsSuccess)
                //{
                //    invupdateHlp.UpdateCNSuccess(result.result, infos);
                //}
                //else
                //{
                //    invupdateHlp.UpdateCNFail(result, infos);
                //}

                //Console.WriteLine(result.error);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error generating invoice!");
                _logger.LogError(ex.Message, ex);
                result.error = ex.Message;
                result.errorCode = "100";
            }
            return result;
        }

        public async Task<GeneralResult<CancelRespone>> CancelDocument(CancelDocument doc, List<string> uuid)
        {
            GeneralResult<CancelRespone> result = new GeneralResult<CancelRespone>();
            result = await _serviceApi.CancelDocument(doc, uuid);

            //UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //if (result.IsSuccess)
            //{
            //    if (doc.docType.ToLower() == "invoice")
            //    {
            //        invupdateHlp.UpdateInoviceCancelSuccess(result.result);
            //    }
            //    else if (doc.docType.ToLower() == "creditnote")
            //    {
            //        invupdateHlp.UpdateCNDNCancelSuccess(result.result, "CREDITNOTE");
            //    }
            //    else if (doc.docType.ToLower() == "debitnote")
            //    {
            //        invupdateHlp.UpdateCNDNCancelSuccess(result.result, "DEBITNOTE");
            //    }
            //}

            return result;
        }

        public async Task<GeneralResult<CancelRespone>> RejectDocument(CancelDocument doc, List<string> uuid)
        {
            GeneralResult<CancelRespone> result = new GeneralResult<CancelRespone>();
            result = await _serviceApi.RejectDocument(doc, uuid);
            //UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //if (result.IsSuccess)
            //{
            //    if (doc.docType.ToLower() == "invoice")
            //    {
            //        invupdateHlp.UpdateInoviceCancelSuccess(result.result);
            //    }
            //    else if (doc.docType.ToLower() == "creditnote")
            //    {
            //        invupdateHlp.UpdateCNDNCancelSuccess(result.result, "CREDITNOTE");
            //    }
            //    else if (doc.docType.ToLower() == "debitnote")
            //    {
            //        invupdateHlp.UpdateCNDNCancelSuccess(result.result, "DEBITNOTE");
            //    }
            //}
            //Once a rejection request is received, a document is still considered “Valid” until the Seller “cancels” the document.
            //update the status via GetRecentDocument
            return result;
        }


        public async Task<GeneralResult<RecentDocument>> GetRecentDocument(RecentDocumentInput query)
        {
            GeneralResult<RecentDocument> result = new GeneralResult<RecentDocument>();
            result = await _serviceApi.getRecentDocument(query);
            //if (result.IsSuccess)
            //{
            //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    invupdateHlp.UpdateSubmission(result.result);
            //    //if (query.documentType == "01")
            //    //{
            //    //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    //    invupdateHlp.UpdateInoviceStatus(result.result);
            //    //}
            //    //else if (query.documentType == "02" || query.documentType == "03")
            //    //{
            //    //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    //    invupdateHlp.UpdateCNDNStatus(result.result);
            //    //}
            //    //else
            //    //{
            //    //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    //    invupdateHlp.UpdateDocumentStatus(result.result);
            //    //}
            //}
            return result;
        }

        public async Task<GeneralResult<Submission>> GetSubmission(string submissionUid)
        {
            GeneralResult<Submission> result = new GeneralResult<Submission>();
            result = await _serviceApi.getSubmission(submissionUid);
            //if (result.IsSuccess)
            //{
            //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    invupdateHlp.UpdateSubmmissionStatus(result.result);
            //}
            return result;
        }

        public async Task<GeneralResult<Submission>> GetCDNSubmission(string submissionUid)
        {
            GeneralResult<Submission> result = new GeneralResult<Submission>();
            result = await _serviceApi.getSubmission(submissionUid);
            //if (result.IsSuccess)
            //{
            //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    invupdateHlp.UpdateSubmmissionCDNStatus(result.result);
            //}
            return result;
        }

        public async Task<GeneralResult<DocumentInfo>> GetDocument(string uuid)
        {
            GeneralResult<DocumentInfo> result = new GeneralResult<DocumentInfo>();
            result = await _serviceApi.getDocument(uuid);
            //if (result.IsSuccess)
            //{
            //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    invupdateHlp.UpdateInoviceStatusEx(result.result);
            //}

            return result;
        }

        public async Task<GeneralResult<DocumentValidatation>> GetDocumentDetail(string uuid)
        {
            GeneralResult<DocumentValidatation> result = new GeneralResult<DocumentValidatation>();
            result = await _serviceApi.getDocumentDetail(uuid);

            return result;
        }

        public async Task<GeneralResult<RecentDocument>> SearchDocument(SearchDocumentInput query)
        {
            GeneralResult<RecentDocument> result = new GeneralResult<RecentDocument>();
            result = await _serviceApi.searchDocument(query);
            //if (result.IsSuccess)
            //{
            //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    invupdateHlp.UpdateInoviceStatus(result.result);
            //}
            return result;
        }

        public async Task<GeneralResult<NotificationResp>> GetNotification(NotificationInput query)
        {
            GeneralResult<NotificationResp> result = new GeneralResult<NotificationResp>();
            result = await _serviceApi.getNotification(query);
            //if (result.IsSuccess)
            //{
            //    UpdateInvoiceTbHelper invupdateHlp = new UpdateInvoiceTbHelper();
            //    invupdateHlp.UpdateNotifications(result.result);
            //}
            return result;
        }

        private List<Documents> generateInvoices(List<DocumentHeader> submitdocs, ref string errorMsg)
        {
            List<Documents> docs = new List<Documents>();
            try
            {
                foreach (var submitDoc in submitdocs)
                {
                    Documents doc = new Documents();
                    submitDoc.DocumentVersion = _clientSecretStore.getDocumentVersion();
                    //GenerateInvoice createInv = new GenerateInvoice(dtInvHeader, dtInvDetail, dtTax, dtAccBrn, dtComp, drCustomer[0], dtIvMas);
                    GenerateInvoice createInv = new GenerateInvoice(submitDoc);
                    createInv.Generate();
                    var inv = createInv.InvoiceRoot;
                    
                    //string data = File.ReadAllText(@"c:\test\jsons\lhdn.txt");
                    //inv= JsonConvert.DeserializeObject<Root>(data);

                    EInvSignatureHelper hlp = new EInvSignatureHelper(_clientSecretStore);
                    if (_clientSecretStore.getDocumentVersion() == "1.1")
                    {
                        hlp.StartProcess(inv, DateTime.Now);
                    }
                  
                    string content = HashUtility.SerializeJson(inv);


                    doc.codeNumber = submitDoc.DocumentNo;
                    doc.format = "JSON";
                    string contentb64 = HashUtility.StringToBase64(content);

                    //string contentIndented = hlp.SerializeJsonIndented(inv);
                    string contentIndented = HashUtility.SerializeJsonIndented(inv);
                    wrieJsonFile(submitDoc, contentIndented);
                    //string hash = Utility.HashString(contentb64);
                    string hash = HashUtility.HashString(content);
                    doc.document = contentb64;
                    doc.documentHash = hash;
                    docs.Add(doc);
                }

                //StoreJSonFile(invJson, "INV", info.docNo);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                _logger.LogError(ex.Message, ex);
                throw new Exception(ex.Message, ex);
            }

            return docs;
        }

        void wrieJsonFile(DocumentHeader submitDoc, string contentIndented)
        {
            try
            {
                string jpath = _clientSecretStore.getJSonFilePath();
                if (!Directory.Exists(jpath))
                {
                    Directory.CreateDirectory(jpath);
                }
                string jfilename = Path.Combine(jpath, submitDoc.DocumentNo + "_inv_json.json");
                File.WriteAllText(jfilename, contentIndented);
            }
            catch (Exception ex)
            {

                _logger.LogError(ex.Message, ex);

            }

        }

        void wrieSubJsonFile(string invno, string contentIndented)
        {
            try
            {
                string jpath = _clientSecretStore.getJSonFilePath();
                if (!Directory.Exists(jpath))
                {
                    Directory.CreateDirectory(jpath);
                }
                string jfilename = Path.Combine(jpath, "Subimission_"+invno + ".json");
                File.WriteAllText(jfilename, contentIndented);
            }
            catch (Exception ex)
            {

                _logger.LogError(ex.Message, ex);

            }

        }

        private List<Documents> generateCreditDebitNotes(List<DocumentHeader> submitdocs, ref string errorMsg)
        {
            List<Documents> docs = new List<Documents>();


            foreach (var submitDoc in submitdocs)
            {
                Documents doc = new Documents();

                submitDoc.DocumentVersion = _clientSecretStore.getDocumentVersion();
                //GenerateInvoice createInv = new GenerateInvoice(dtInvHeader, dtInvDetail, dtTax, dtAccBrn, dtComp, drCustomer[0], dtIvMas);
                GenerateCreditNote createCN = new GenerateCreditNote(submitDoc);
                createCN.Generate();
                var inv = createCN.InvoiceRoot;

                EInvSignatureHelper hlp = new EInvSignatureHelper(_clientSecretStore);
                if (_clientSecretStore.getDocumentVersion() == "1.1")
                {
                    hlp.StartProcess(inv, DateTime.Now);
                }
                //string content = hlp.SerializeJson(inv);
                string content = HashUtility.SerializeJson(inv);
                doc.codeNumber = submitDoc.DocumentNo;
                doc.format = "JSON";
                string contentb64 = HashUtility.StringToBase64(content);
                //string hash = Utility.HashString(contentb64);
                string hash = HashUtility.HashString(content);
                doc.document = contentb64;
                doc.documentHash = hash;
                docs.Add(doc);
            }


            return docs;
        }

        public void StoreJSonFile(string content, string docType, string docNo)
        {
            try
            {
                string path = _clientSecretStore.getJSonFilePath();
                string filename = string.Format("{0}_{1}_{2}.json", docType, docNo, DateTime.Now.Ticks);
                string fullPath = Path.Combine(path, filename);
                File.WriteAllText(fullPath, content);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error Saving jsong file! No big deal");
                _logger.LogError(ex.Message, ex);

            }

        }
    }



}
