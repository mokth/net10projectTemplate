using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model
{
    public class HttpErrorCode
    {
        public static string GetError(int errorCode)
        {
            string error = "";
            switch (errorCode)
            {
                case 400: error = "400	Bad Request"; break;
                case 401: error = "401	Unauthorized"; break;
                case 402: error = "402	Payment Required"; break;
                case 403: error = "403	Forbidden"; break;
                case 404: error = "404	Not Found"; break;
                case 405: error = "405	Method Not Allowed"; break;
                case 406: error = "406	Not Acceptable"; break;
                case 407: error = "407	Proxy Authentication Required"; break;
                case 408: error = "408	Request Timeout"; break;
                case 409: error = "409	Conflict"; break;
                case 410: error = "410	Gone"; break;
                case 411: error = "411	Length Required"; break;
                case 412: error = "412	Precondition Failed"; break;
                case 413: error = "413	Payload Too Large"; break;
                case 414: error = "414	URI Too Long"; break;
                case 415: error = "415	Unsupported Media Type"; break;
                case 416: error = "416	Range Not Satisfiable"; break;
                case 417: error = "417	Expectation Failed"; break;
                case 418: error = "418	I'm a Teapot"; break;
                case 421: error = "421	Misdirected Request"; break;
                case 422: error = "422	Unprocessable Entity"; break;
                case 423: error = "423	Locked"; break;
                case 424: error = "424	Failed Dependency"; break;
                case 425: error = "425	Too Early"; break;
                case 426: error = "426	Upgrade Required"; break;
                case 428: error = "428	Precondition Required"; break;
                case 429: error = "429	Too Many Requests"; break;
                case 431: error = "431	Request Header Fields Too Large"; break;
                case 451: error = "451	Unavailable For Legal Reasons"; break;
                case 500: error = "Internal Server Error"; break;
                case 501: error = "Not Implemented"; break;
                case 502: error = "Bad Gateway"; break;
                case 503: error = "Service Unavailable"; break;
                case 504: error = "Gateway Timeout"; break;


            }

            return error;
        }
    }
}
