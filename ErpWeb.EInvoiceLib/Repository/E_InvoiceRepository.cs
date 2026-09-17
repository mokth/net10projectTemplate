using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.BL.Entity;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.EInvoiceLib.Model.Login;

namespace ErpWeb.EInvoiceLib.Repository
{
    public class E_InvoiceRepository : IE_InvoiceRepository
    {
        private readonly ILogger<E_InvoiceRepository> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IClientSecretStore _clientSecretStore;
        private readonly IEInvoiceRepository _repo;
        
        string _token = "";
        string _baseUrl = "";
        public E_InvoiceRepository(ILogger<E_InvoiceRepository> logger,
                                   IClientSecretStore clientSecretStore,
                                   IEInvoiceRepository repo,
                                   IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _repo = repo;
            _clientSecretStore = clientSecretStore;
            _httpClientFactory = httpClientFactory;
            _baseUrl = _clientSecretStore.getUrl();
        }

        private void getbaseURl()
        {
            if (_baseUrl == null || _baseUrl == "")
            {
                _baseUrl = _clientSecretStore.getUrl();
            }
        }

        /// <param name="body"></param>
        /// This should be the Tax Identification Number (TIN) of the taxpayer the intermediary is presenting
        /// <param name="onbehalfof"></param>
        /// <returns></returns>
        public async Task<GeneralResult<string>> Login(string onbehalfof)
        {

            GeneralResult<string> result = new GeneralResult<string>();
            try
            {

                var client = _httpClientFactory.CreateClient();

                //client.DefaultRequestHeaders.Add("onbehalfof", onbehalfof);
                getbaseURl();
                var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/connect/token");
                var collection = new List<KeyValuePair<string, string>>();
                collection.Add(new KeyValuePair<string, string>("client_id", _clientSecretStore.getSecretID()));
                collection.Add(new KeyValuePair<string, string>("client_secret", _clientSecretStore.getSecretKey()));
                collection.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));
                collection.Add(new KeyValuePair<string, string>("scope", "InvoicingAPI"));
                collection.Add(new KeyValuePair<string, string>("onbehalfof", onbehalfof));
                var content = new FormUrlEncodedContent(collection);
                request.Content = content;

                _logger.LogDebug(_baseUrl + "/connect/token");


                // var response = await client.SendAsync(request);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.SendAsync(request);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.InnerException.Message, ex.InnerException);
                    throw new Exception(ex.InnerException.Message, ex.InnerException);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    LoginSuccess sucess = JsonConvert.DeserializeObject<LoginSuccess>(responseBody);
                    await PersistToken(sucess);
                    result.IsSuccess = true;
                    result.result = _token;
                }
                else
                {
                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        BadLoginResponse fail = JsonConvert.DeserializeObject<BadLoginResponse>(responseBody);
                        result.error = fail.error + " " + fail.error_description;
                    }
                    else
                    {
                        ErrorRespone fail = new ErrorRespone();
                        HttpErrorCode.GetError((int)response.StatusCode);
                        int code = (int)response.StatusCode;
                        fail.details = new List<ErrorDetail>()
                        {
                            new ErrorDetail()
                            {
                                 code =code.ToString(),
                                 details = response.StatusCode.ToString()
                            }
                        };
                        //il.details = response.StatusCode.ToString();

                        fail.code = code.ToString();
                    }

                    result.IsSuccess = false;

                }
                //Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        //Wincom ERP shld use this function to login
        //This API is used to authenticate the ERP system associated with a specific taxpayer calling and issue access token
        //which allows ERP system to access those protected APIs.
        public async Task<GeneralResult<string>> Login()
        {

            GeneralResult<string> result = new GeneralResult<string>();
            try
            {

                var client = _httpClientFactory.CreateClient();

                getbaseURl();
                var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/connect/token");
                var collection = new List<KeyValuePair<string, string>>();
                collection.Add(new KeyValuePair<string, string>("client_id", _clientSecretStore.getSecretID()));
                collection.Add(new KeyValuePair<string, string>("client_secret", _clientSecretStore.getSecretKey()));
                collection.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));
                collection.Add(new KeyValuePair<string, string>("scope", "InvoicingAPI"));
                //collection.Add(new KeyValuePair<string, string>("onbehalfof", ClientSecretStore.getOnBehalfTin()));
                var content = new FormUrlEncodedContent(collection);
                var onbehalf = _clientSecretStore.getOnBehalfTin();
                if (!string.IsNullOrEmpty(onbehalf))
                {
                    request.Headers.Add("onbehalfof", _clientSecretStore.getOnBehalfTin());
                }
                request.Content = content;
                _logger.LogDebug(_baseUrl + "/connect/token  clientID " + _clientSecretStore.getSecretID());
                _logger.LogDebug(_baseUrl + "/connect/token  with onbehalfof " + _clientSecretStore.getOnBehalfTin());


                try
                {

                    HttpResponseMessage response = new HttpResponseMessage();
                    try
                    {
                        var task = client.SendAsync(request);
                        task.Wait();
                        response = task.Result;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(ex.InnerException.Message, ex.InnerException);
                        throw new Exception(ex.InnerException.Message, ex.InnerException);
                    }

                    //_logger.LogDebug("login data 3");
                    string responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug(responseBody);
                    if (response.IsSuccessStatusCode)
                    {
                        LoginSuccess sucess = JsonConvert.DeserializeObject<LoginSuccess>(responseBody);
                     
                        //await PersistToken(sucess);
                        result.result = sucess.access_token;
                        result.IsSuccess = true;
                    }
                    else
                    {
                        if (response.StatusCode == HttpStatusCode.BadRequest)
                        {
                            BadLoginResponse fail = JsonConvert.DeserializeObject<BadLoginResponse>(responseBody);
                            result.error = fail.error + " " + fail.error_description;
                        }
                        else
                        {
                            ErrorRespone fail = new ErrorRespone();
                            HttpErrorCode.GetError((int)response.StatusCode);
                            int code = (int)response.StatusCode;
                            fail.details = new List<ErrorDetail>()
                            {
                            new ErrorDetail()
                            {
                                 code =code.ToString(),
                                 details = response.StatusCode.ToString()
                            }
                            };
                            //  fail.details = response.StatusCode.ToString();
                            // int code = (int)response.StatusCode;
                            fail.code = code.ToString();
                        }

                        result.IsSuccess = false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message, ex);
                    result.IsSuccess = false;
                    result.error = ex.Message;
                }
                //Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        public async Task<GeneralResult<string>> LoginEx(string onbehalf)
        {

            GeneralResult<string> result = new GeneralResult<string>();
            try
            {

                var client = _httpClientFactory.CreateClient();

                getbaseURl();
                var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/connect/token");
                var collection = new List<KeyValuePair<string, string>>();
                collection.Add(new KeyValuePair<string, string>("client_id", _clientSecretStore.getSecretID()));
                collection.Add(new KeyValuePair<string, string>("client_secret", _clientSecretStore.getSecretKey()));
                collection.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));
                collection.Add(new KeyValuePair<string, string>("scope", "InvoicingAPI"));
                //collection.Add(new KeyValuePair<string, string>("onbehalfof", ClientSecretStore.getOnBehalfTin()));
                var content = new FormUrlEncodedContent(collection);
                //var onbehalf = _clientSecretStore.getOnBehalfTin();
                if (!string.IsNullOrEmpty(onbehalf))
                {
                    request.Headers.Add("onbehalfof", onbehalf);
                }
                request.Content = content;
                _logger.LogDebug(_baseUrl + "/connect/token  clientID " + _clientSecretStore.getSecretID());
                _logger.LogDebug(_baseUrl + "/connect/token  with onbehalfof " + onbehalf);


                try
                {

                    HttpResponseMessage response = new HttpResponseMessage();
                    try
                    {
                        var task = client.SendAsync(request);
                        task.Wait();
                        response = task.Result;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(ex.InnerException.Message, ex.InnerException);
                        throw new Exception(ex.InnerException.Message, ex.InnerException);
                    }

                    //_logger.LogDebug("login data 3");
                    string responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug(responseBody);
                    if (response.IsSuccessStatusCode)
                    {
                        LoginSuccess sucess = JsonConvert.DeserializeObject<LoginSuccess>(responseBody);

                        //await PersistToken(sucess);
                        result.result = sucess.access_token;
                        result.IsSuccess = true;
                    }
                    else
                    {
                        if (response.StatusCode == HttpStatusCode.BadRequest)
                        {
                            BadLoginResponse fail = JsonConvert.DeserializeObject<BadLoginResponse>(responseBody);
                            result.error = fail.error + " " + fail.error_description;
                        }
                        else
                        {
                            ErrorRespone fail = new ErrorRespone();
                            HttpErrorCode.GetError((int)response.StatusCode);
                            int code = (int)response.StatusCode;
                            fail.details = new List<ErrorDetail>()
                            {
                            new ErrorDetail()
                            {
                                 code =code.ToString(),
                                 details = response.StatusCode.ToString()
                            }
                            };
                            //  fail.details = response.StatusCode.ToString();
                            // int code = (int)response.StatusCode;
                            fail.code = code.ToString();
                        }

                        result.IsSuccess = false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message, ex);
                    result.IsSuccess = false;
                    result.error = ex.Message;
                }
                //Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        public async Task<string> GetToken()
        {
            //TokenDbHelper dbHelper = new TokenDbHelper();

            //if (_token == null || _token == "")
            //{
            //    _logger.LogDebug("Token is empty, get from DB.");
            //    _token = await _repo.GetToken();
            //    //_token = _httpContent.HttpContext.Session.GetString("token");
            //}

            //temp take out

            // 20251230
            //_token = _repo.GetToken();
            //_token = _clientSecretStore.getTokenString();
           // if (_token == null || _token == "")
            //{
                _logger.LogDebug("Token is empty, get from API.");
            //login again
            string myToken = "";
                var result = await Login();
                if (!result.IsSuccess)
                {
                    _logger.LogDebug("Fail to token from API!");
                myToken = "";
            }
            else
            {
                myToken = result.result;
            }
            // }

            return myToken;
        }

        public async Task<string> GetTokenWithTIN(string tinno)
        {
            //TokenDbHelper dbHelper = new TokenDbHelper();

            //if (_token == null || _token == "")
            //{
            //    _logger.LogDebug("Token is empty, get from DB.");
            //    _token = await _repo.GetToken();
            //    //_token = _httpContent.HttpContext.Session.GetString("token");
            //}

            //temp take out

            // 20251230
            //_token = _repo.GetToken();
            //_token = _clientSecretStore.getTokenString();
            // if (_token == null || _token == "")
            //{
            _logger.LogDebug("Token is empty, get from API.");
            //login again
            string myToken = "";
            var result = await LoginEx(tinno);
            if (!result.IsSuccess)
            {
                _logger.LogDebug("Fail to token from API!");
                myToken = "";
            }
            else
            {
                myToken = result.result;
            }
            // }

            return myToken;
        }



        async Task PersistToken(LoginSuccess jwtresp) //string token)
        {
            //TokenDbHelper dbHelper = new TokenDbHelper();
            try
            {
                //JWTTokenInfo jwttoken = JsonConvert.DeserializeObject<JWTTokenInfo>(jwtresp.access_token);
                //jwtresp.access_token = jwttoken.auth_token;
                EInvToken token = new EInvToken();
                token.access_token = jwtresp.access_token;
                token.expires_in = jwtresp.expires_in;
                token.scope = jwtresp.scope;
                token.token_type = jwtresp.token_type;
                token.created = DateTime.Now;
                //temp take out

                // 20251230 ** no need to store token in DB, let it every time to generate new token
                //_repo.InsertAdToken(token);

                _token = jwtresp.access_token;
                _clientSecretStore.setTokenString(_token);
            }
            catch (Exception ex)
            {
                _logger.LogError("Fail to persist JWT data!");
                _logger.LogError(ex.Message, ex);
            }
        }


        #region Platform API

        /// <summary>
        /// This API allows taxpayer's systems to retrieve list of document types published by the MyInvois System.
        /// </summary>
        /// <returns></returns>
        public async Task<GeneralResult<AllDocumentType>> getAllDocumentType()
        {

            GeneralResult<AllDocumentType> result = new GeneralResult<AllDocumentType>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                _logger.LogDebug(_baseUrl + "/api/v1.0/documenttypes");
                //HttpResponseMessage response = await client.GetAsync(_baseUrl+ "/api/v1.0/documenttypes");
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(_baseUrl + "/api/v1.0/documenttypes");
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    AllDocumentType docs = JsonConvert.DeserializeObject<AllDocumentType>(responseBody);
                    result.IsSuccess = true;
                    result.result = docs;
                }
                else
                {
                    ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        result.error = fail.code + " " + fail.details;
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// This API allows taxpayer's ERP system to retrieve the details of single document type that contains structure definitions of the document.
        /// </summary>
        /// Unique ID of existing document type
        /// <param name="docID"></param>
        /// <returns></returns>
        public async Task<GeneralResult<DocumentTypeInfo>> getDocumentType(int docID)
        {

            GeneralResult<DocumentTypeInfo> result = new GeneralResult<DocumentTypeInfo>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documenttypes/{0}", docID);
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);

                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(url);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    DocumentTypeInfo doctype = JsonConvert.DeserializeObject<DocumentTypeInfo>(responseBody);
                    result.IsSuccess = true;
                    result.result = doctype;
                }
                else
                {
                    ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        result.error = fail.code + " " + fail.details;
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            return result;
        }

        // This API allows taxpayer's ERP system to retrieve the details of document type version that contains structure definitions of the documents
        /// Unique ID of existing document type
        /// <param name="docID"></param>
        /// 
        /// Unique ID of existing document type version that is published or deactivated
        /// <param name="vid"></param>
        /// <returns></returns>
        public async Task<GeneralResult<DocumentTypeVersion>> getDocumenTypeVersion(int docID, int vid)
        {

            GeneralResult<DocumentTypeVersion> result = new GeneralResult<DocumentTypeVersion>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documenttypes/{0}/version/{1}", docID, vid);
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(url);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    DocumentTypeVersion docs = JsonConvert.DeserializeObject<DocumentTypeVersion>(responseBody);
                    result.IsSuccess = true;
                    result.result = docs;
                }
                else
                {
                    ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        result.error = fail.code + " " + fail.details;
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// This API allows taxpayer's ERP system to retrieve the details of document type version that contains structure definitions of the documents.
        /// </summary>
        /// Optional: start date and time for notifications to retrieve based on the date sent
        /// <param name="dateFrom"></param>
        /// 
        /// Optional: end date and time for notifications to retrieve based on the date sent
        /// <param name="dateTo"></param>
        /// 
        /// Optional: type of notifications to retrieve specified as ID of the type. See Notification types
        /// <param name="type"></param>
        /// 
        /// Optional: used to get notifications only if they were sent out in a specific language. Values:msand en
        /// <param name="language"></param>
        /// 
        /// Optional: used to get notifications of certain status only, e.g., only those that were not delivered. Values: pending, batched, delivered, error
        /// <param name="status"></param>
        /// 
        /// Optional: used to get notifications delivered over certain channel only. Values: email, push
        /// <param name="channel"></param>
        /// 
        /// Optional: number of the page to retrieve. Typically this parameter value is derived from initial parameter less call when caller learns total amount of page of certain size
        /// <param name="pageNo"></param>
        /// 
        /// Optional: number of the packages to retrieve per page. Page size cannot exceed system configured maximum page size for this API which is 100
        /// <param name="pageSize"></param>
        /// <returns></returns>
        public async Task<GeneralResult<NotificationResp>> getNotification(NotificationInput query)
        {
            GeneralResult<NotificationResp> result = new GeneralResult<NotificationResp>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/notifications/taxpayer?{0}", query.getQueryString());
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    response = await client.GetAsync(url);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    NotificationResp docs = JsonConvert.DeserializeObject<NotificationResp>(responseBody);
                    result.IsSuccess = true;
                    result.result = docs;
                }
                else
                {
                    ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        result.error = fail.code + " " + fail.details;
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        #endregion Platform API


        #region Inovice API
        /// <summary>
        /// This API allows taxpayer's ERP system to validate specific Tax Identification Number (TIN) before adding this number to an invoice and issuing the invoice.
        /// </summary>
        /// The Tax Identification Number to get the validity of the tin.
        /// <param name="tin"></param>
        /// 
        /// (NRIC, Passport number, Business registration number, army number)
        /// <param name="idType"></param>
        /// 
        /// The actual value of the ID Type selected. For example, if NRIC selected as ID Type, then pass the NRIC value here.
        /// BRN example: 201901234567
        //  NRIC example: 770625015324
        //  Passport number example: A12345678
        //  Army number example: 551587706543
        /// <param name="idValue"></param>
        /// <returns></returns>
        public async Task<GeneralResult<bool>> ValidateTin(string tin, string idType, string idValue)
        {

            GeneralResult<bool> result = new GeneralResult<bool>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/taxpayer/validate/{0}?idType={1}&idValue={2}", tin, idType, idValue);
                _logger.LogDebug(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(url);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    result.IsSuccess = true;
                }
                else
                {
                    result.IsSuccess = false;
                    result.error = "Invalid TIN.";
                }
                //Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// This API allows taxpayer's ERP system to search for a Tax Identification Number (TIN)
        /// by taxpayer name and/or registration identity.
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        public async Task<GeneralResult<List<TINInfo>>> SearchTin(SearchTINInput query)
        {
            GeneralResult<List<TINInfo>> result = new GeneralResult<List<TINInfo>>();
            try
            {
                var parameters = new List<string>();
                if (!string.IsNullOrEmpty(query?.taxpayerName))
                {
                    parameters.Add("taxpayerName=" + Uri.EscapeDataString(query.taxpayerName));
                }
                if (!string.IsNullOrEmpty(query?.idType))
                {
                    parameters.Add("idType=" + Uri.EscapeDataString(query.idType));
                }
                if (!string.IsNullOrEmpty(query?.idValue))
                {
                    parameters.Add("idValue=" + Uri.EscapeDataString(query.idValue));
                }

                if (parameters.Count == 0)
                {
                    result.IsSuccess = false;
                    result.error = "At least one search parameter (taxpayerName, idType or idValue) is required.";
                    return result;
                }

                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/taxpayer/search?{0}", string.Join("&", parameters));
                _logger.LogDebug(url);

                HttpResponseMessage response;
                try
                {
                    response = await client.GetAsync(url);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    List<TINInfo> tinList = JsonConvert.DeserializeObject<List<TINInfo>>(responseBody);
                    result.IsSuccess = true;
                    result.result = tinList ?? new List<TINInfo>();
                }
                else
                {
                    ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        result.error = fail.code + " " + fail.details;
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// This API allows taxpayer to submit one or more signed documents to MyInvois System.
        /// </summary>
        /// <param name="doc"></param>
        /// <returns></returns>
        public async Task<GeneralResult<SuccessSubmit>> SubmitDocument(SubmitDocument doc)
        {

            GeneralResult<SuccessSubmit> result = new GeneralResult<SuccessSubmit>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(doc);

                var data = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
                //test
                //string jpath = ClientSecretStore.getJSonFilePath();
                //string filename = Path.Combine(jpath, doc.documents[0].codeNumber + "_sub.json");
                //var taskread = data.ReadAsStringAsync();
                //taskread.Wait();
                //File.WriteAllText(filename, taskread.Result);
                
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = _baseUrl + "/api/v1.0/documentsubmissions/";
                _logger.LogDebug(url);
                // HttpResponseMessage response = await client.PostAsync(url, data);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.PostAsync(url, data);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    SuccessSubmit resp = JsonConvert.DeserializeObject<SuccessSubmit>(responseBody);
                    result.result = resp;
                    result.IsSuccess = true;
                    if (resp.acceptedDocuments.Count > 0)
                    {
                        result.IsSuccess = true;
                    }
                    else
                    {
                        result.IsSuccess = false;
                    }

                    if (resp.rejectedDocuments.Count > 0)
                    {
                        result.error = resp.rejectedDocuments[0].error.message;
                        if (resp.rejectedDocuments[0].error.details.Count > 0)
                        {
                            result.error = resp.rejectedDocuments[0].error.details[0].message;
                        }
                    }
                }
                else
                {
                    ErrorSubmit resperror = JsonConvert.DeserializeObject<ErrorSubmit>(responseBody);
                    if (resperror != null)
                    {
                        result.IsSuccess = false;
                        result.errorCode = response.StatusCode.ToString();
                        if (resperror.error.details.Count > 0)
                        {
                            result.error = resperror.error.details[0].message;
                        }
                        else
                        {
                            result.error = resperror.error.code;
                        }
                    }
                    else
                    {

                        result.IsSuccess = false;
                        int statusCode = (int)response.StatusCode;
                        result.errorCode = statusCode.ToString();
                        _logger.LogDebug(statusCode.ToString());
                        switch (statusCode)
                        {
                            case 400:
                                //BadStructure  Returned when there is a structural error with the submission message. Either is not having the Documents included in a single Document tag or there are other elements that are not allowed.
                                //MaximumSizeExceeded Returned when the size of the submission exceeds allowed limit. Details including supported size in the Error object in the body of the return message. It is expected that calling system creates smaller submissions and submit them one by one.
                                result.error = "BadStructure || MaximumSizeExceeded";

                                break;
                            case 403:
                                //Returned when submitter of the documents is trying to submit them on behalf of the other taxpayer. Also returned when intermediary is submitting documents on behalf of taxpayer, but they do not have such a permission. Details in the Error object in the body of the return message.
                                result.error = "IncorrectSubmitter";
                                break;
                            case 422:
                                //Returned when an identical submission is detected based on the previous submissions sent by the same taxpayer within the past 10 minutes. Issuer can try submitting the same payload again based on the returned value in the response header Retry-After in seconds. Detection works on hashing the request payload and compare it with previous submissions.
                                result.error = "DuplicateSubmission";
                                break;
                            default:
                                result.error = "Unknow error";
                                break;
                        }
                    }
                }

                // Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        public async Task<GeneralResult<SuccessSubmit>> SubmitDocumentWithTin(SubmitDocument doc,string TINNO)
        {

            GeneralResult<SuccessSubmit> result = new GeneralResult<SuccessSubmit>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(doc);

                var data = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
                //test
                //string jpath = ClientSecretStore.getJSonFilePath();
                //string filename = Path.Combine(jpath, doc.documents[0].codeNumber + "_sub.json");
                //var taskread = data.ReadAsStringAsync();
                //taskread.Wait();
                //File.WriteAllText(filename, taskread.Result);

                var token = await GetTokenWithTIN(TINNO);
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = _baseUrl + "/api/v1.0/documentsubmissions/";
                _logger.LogDebug(url);
                // HttpResponseMessage response = await client.PostAsync(url, data);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.PostAsync(url, data);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    SuccessSubmit resp = JsonConvert.DeserializeObject<SuccessSubmit>(responseBody);
                    result.result = resp;
                    result.IsSuccess = true;
                    if (resp.acceptedDocuments.Count > 0)
                    {
                        result.IsSuccess = true;
                    }
                    else
                    {
                        result.IsSuccess = false;
                    }

                    if (resp.rejectedDocuments.Count > 0)
                    {
                        result.error = resp.rejectedDocuments[0].error.message;
                        if (resp.rejectedDocuments[0].error.details.Count > 0)
                        {
                            result.error = resp.rejectedDocuments[0].error.details[0].message;
                        }
                    }
                }
                else
                {
                    ErrorSubmit resperror = JsonConvert.DeserializeObject<ErrorSubmit>(responseBody);
                    if (resperror != null)
                    {
                        result.IsSuccess = false;
                        result.errorCode = response.StatusCode.ToString();
                        if (resperror.error.details.Count > 0)
                        {
                            result.error = resperror.error.details[0].message;
                        }
                        else
                        {
                            result.error = resperror.error.code;
                        }
                    }
                    else
                    {

                        result.IsSuccess = false;
                        int statusCode = (int)response.StatusCode;
                        result.errorCode = statusCode.ToString();
                        _logger.LogDebug(statusCode.ToString());
                        switch (statusCode)
                        {
                            case 400:
                                //BadStructure  Returned when there is a structural error with the submission message. Either is not having the Documents included in a single Document tag or there are other elements that are not allowed.
                                //MaximumSizeExceeded Returned when the size of the submission exceeds allowed limit. Details including supported size in the Error object in the body of the return message. It is expected that calling system creates smaller submissions and submit them one by one.
                                result.error = "BadStructure || MaximumSizeExceeded";

                                break;
                            case 403:
                                //Returned when submitter of the documents is trying to submit them on behalf of the other taxpayer. Also returned when intermediary is submitting documents on behalf of taxpayer, but they do not have such a permission. Details in the Error object in the body of the return message.
                                result.error = "IncorrectSubmitter";
                                break;
                            case 422:
                                //Returned when an identical submission is detected based on the previous submissions sent by the same taxpayer within the past 10 minutes. Issuer can try submitting the same payload again based on the returned value in the response header Retry-After in seconds. Detection works on hashing the request payload and compare it with previous submissions.
                                result.error = "DuplicateSubmission";
                                break;
                            default:
                                result.error = "Unknow error";
                                break;
                        }
                    }
                }

                // Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }
        /// <summary>
        /// This API allows issuer taxpayer to cancel previously issued document either self-induced cancellation or by accepting a rejection request made by the buyer.
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="uuid"></param>
        /// <returns></returns>
        public async Task<GeneralResult<CancelRespone>> CancelDocument(CancelDocument doc, List<string> uuid)
        {

            GeneralResult<CancelRespone> result = new GeneralResult<CancelRespone>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(doc);
                var data = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");

                string uid = string.Join(",", uuid.ToArray());
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documents/state/{0}/state", uid);
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                _logger.LogDebug(url);
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

                //HttpResponseMessage response = await client.PutAsync(url,data);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.PutAsync(url, data);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    CancelRespone resp = JsonConvert.DeserializeObject<CancelRespone>(responseBody);
                    result.result = resp;
                    result.IsSuccess = true;
                }
                else
                {
                    ErrorSubmit fail = JsonConvert.DeserializeObject<ErrorSubmit>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        if (fail.error.details.Count > 0)
                        {
                            result.error = fail.error.details[0].message;
                        }
                        else
                        {
                            result.error = "Fail to cancel document!";
                        }
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }


                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// This API allows a buyer that received an invoice to reject it and request the supplier to cancel it.
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="uuid"></param>
        /// <returns></returns>
        public async Task<GeneralResult<CancelRespone>> RejectDocument(CancelDocument doc, List<string> uuid)
        {

            GeneralResult<CancelRespone> result = new GeneralResult<CancelRespone>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(doc);
                var data = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");

                string uid = string.Join(",", uuid.ToArray());
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documents/state/{0}/state", uid);
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                _logger.LogDebug(url);
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

                //HttpResponseMessage response = await client.PutAsync(url, data);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.PutAsync(url, data);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    CancelRespone resp = JsonConvert.DeserializeObject<CancelRespone>(responseBody);
                    result.result = resp;
                    result.IsSuccess = true;
                }
                else
                {
                    result.IsSuccess = false;
                    ErrorSubmit fail = JsonConvert.DeserializeObject<ErrorSubmit>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        if (fail.error.details.Count > 0)
                        {
                            result.error = fail.error.details[0].message;
                        }
                        else
                        {
                            result.error = "Fail to cancel document!";
                        }
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                    //int statusCode = (int)response.StatusCode;
                    //switch (statusCode)
                    //{
                    //    case 400:
                    //        //OperationPeriodOver  Returned when user is trying to reject document when the limit of rejection has already run out.
                    //        //IncorrectState   Returned when caller is trying to reject the document that is not in valid state. State transition in this case is not allowed.
                    //        //ActiveReferencingDocuments Returned when caller is trying to reject the document that is being referenced by other documents, e.g., credit note referencing invoice that is correcting the problem already. If rejection is still required, the action to take is to reject first the referencing document and only then this one.
                    //        result.error = "OperationPeriodOver || IncorrectState || ActiveReferencingDocuments";
                    //        break;
                    //    case 403:
                    //        //Returned when valid taxpayer system is trying to perform operation on a document not received by them.
                    //        result.error = "Forbidden";
                    //        break;

                    //    default:
                    //        result.error = "Unknow error";
                    //        break;
                    //}

                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// This API allows taxpayer's systems to search the documents sent or received which are available on the MyInvois System using various filters. 
        /// This API will only return documents that are issued within the last 30 days.
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        public async Task<GeneralResult<RecentDocument>> getRecentDocument(RecentDocumentInput query)
        {


            GeneralResult<RecentDocument> result = new GeneralResult<RecentDocument>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                getbaseURl();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

                string url = string.Format(_baseUrl + "/api/v1.0/documents/recent?{0}", query.getQueryString());
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(url);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
                if (response.IsSuccessStatusCode)
                {
                    RecentDocument docs = JsonConvert.DeserializeObject<RecentDocument>(responseBody, settings);
                    result.IsSuccess = true;
                    result.result = docs;
                }
                else
                {
                    _logger.LogDebug(response.StatusCode.ToString());
                    ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody, settings);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        result.error = fail.code + " " + fail.details;
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                }
                // Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// This API returns information on documents submitted during a single submission by taxpayer.
        /// </summary>
        /// Unique ID of the document submission to retrieve.
        /// <param name="submissionUid"></param>
        /// <returns></returns>
        public async Task<GeneralResult<Submission>> getSubmission(string submissionUid)
        {

            GeneralResult<Submission> result = new GeneralResult<Submission>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documentsubmissions/{0}", submissionUid);
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    response = await client.GetAsync(url);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    Submission docs = JsonConvert.DeserializeObject<Submission>(responseBody);
                    result.IsSuccess = true;
                    result.result = docs;
                }
                else
                {
                    ErrorSubmit fail = JsonConvert.DeserializeObject<ErrorSubmit>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        if (fail.error.details.Count > 0)
                        {
                            result.error = fail.error.code + " " + fail.error.details[0].message;
                        }
                        else
                        {
                            result.error = fail.error.code;
                        }
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }

                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// This API allows taxpayers to retrieve document source in XML or JSON format along with the additional tax authority metadata.
        /// </summary>
        /// Unique ID of the document to retrieve.	
        /// <param name="uuid"></param>
        /// <returns></returns>
        public async Task<GeneralResult<DocumentInfo>> getDocument(string uuid)
        {

            GeneralResult<DocumentInfo> result = new GeneralResult<DocumentInfo>();
            try
            {

                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documents/{0}/raw", uuid);
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(url);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }
                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    DocumentInfo docs = JsonConvert.DeserializeObject<DocumentInfo>(responseBody);
                    result.IsSuccess = true;
                    result.result = docs;
                }
                else
                {
                    ErrorSubmit fail = JsonConvert.DeserializeObject<ErrorSubmit>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        if (fail.error.details.Count > 0)
                        {
                            result.error = fail.error.code + " " + fail.error.details[0].message;
                        }
                        else
                        {
                            result.error = fail.error.code;
                        }
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                    //ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    //result.IsSuccess = false;
                    //result.error = fail.code + " " + fail.details;
                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            return result;
        }


        /// <summary>
        /// This API allows taxpayers to retrieve a single document's full details including validation results.
        /// </summary>
        /// Unique ID of the document to retrieve.	
        /// <param name="uuid"></param>
        /// <returns></returns>
        public async Task<GeneralResult<DocumentValidatation>> getDocumentDetail(string uuid)
        {

            GeneralResult<DocumentValidatation> result = new GeneralResult<DocumentValidatation>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documents/{0}/details", uuid);
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(url);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    DocumentValidatation docs = JsonConvert.DeserializeObject<DocumentValidatation>(responseBody);
                    result.IsSuccess = true;
                    result.result = docs;
                }
                else
                {
                    ErrorSubmit fail = JsonConvert.DeserializeObject<ErrorSubmit>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        if (fail.error.details.Count > 0)
                        {
                            result.error = fail.error.code + " " + fail.error.details[0].message;
                        }
                        else
                        {
                            result.error = fail.error.code;
                        }
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                    //ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    //result.IsSuccess = false;
                    //result.error = fail.code + " " + fail.details;
                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }

            return result;
        }


        /// <summary>
        /// This API allows taxpayer's systems to search the documents sent or received which are available on the MyInvois System using various filters.
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        public async Task<GeneralResult<RecentDocument>> searchDocument(SearchDocumentInput query)
        {


            GeneralResult<RecentDocument> result = new GeneralResult<RecentDocument>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await GetToken();
                if (token == "")
                {
                    result.IsSuccess = false;
                    result.error = "Invalid JWT Token! Token is blank.";
                    return result;
                }
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
                getbaseURl();
                string url = string.Format(_baseUrl + "/api/v1.0/documents/search?{0}", query.getQueryString());
                _logger.LogDebug(url);
                //HttpResponseMessage response = await client.GetAsync(url);
                HttpResponseMessage response = new HttpResponseMessage();
                try
                {
                    var task = client.GetAsync(url);
                    task.Wait();
                    response = task.Result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex.Message, ex);
                    throw new HttpRequestException(ex.Message, ex);
                }
                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(responseBody);
                if (response.IsSuccessStatusCode)
                {
                    RecentDocument doc = JsonConvert.DeserializeObject<RecentDocument>(responseBody);
                    result.IsSuccess = true;
                    result.result = doc;
                }
                else
                {
                    ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    result.IsSuccess = false;
                    if (fail != null)
                    {
                        result.error = fail.code + " " + fail.details;
                    }
                    else
                    {
                        result.error = HttpErrorCode.GetError((int)response.StatusCode);
                    }
                    //ErrorRespone fail = JsonConvert.DeserializeObject<ErrorRespone>(responseBody);
                    //result.IsSuccess = false;
                    //result.error = fail.code + " " + fail.details;
                }
                Console.WriteLine(responseBody);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, ex);
                result.IsSuccess = false;
                result.error = ex.Message;
            }
            return result;
        }

    }

    #endregion Inovice API

}
