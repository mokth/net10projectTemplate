using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Utility
{
    public class JsonDateTimeUtil
    {
        public static string getJsonDate_WithHHmm(DateTime date)
        {
            var settings = new JsonSerializerSettings
            {
                //DateFormatString = "yyyy-MM-ddTH:mm:ss.fffZ",
                DateFormatString = "yyyy-MM-ddTHH:mmZ",
                DateTimeZoneHandling = DateTimeZoneHandling.Utc
            };
            var json = JsonConvert.SerializeObject(date, settings);
            Console.WriteLine(json);

            return json.Replace("\"", "");

        }

        public static string getJsonDateTime(DateTime date)
        {
            //2022-11-25T01:59:10
            //2022-11-25T01:59:10Z 
            var settings = new JsonSerializerSettings
            {
                //DateFormatString = "yyyy-MM-ddTH:mm:ss.fffZ",
                DateFormatString = "yyyy-MM-ddTHH:mm:ssZ",
                DateTimeZoneHandling = DateTimeZoneHandling.Utc
            };
            var json = JsonConvert.SerializeObject(date, settings);

            Console.WriteLine(json);

            return json.Replace("\"", "");

        }
    }
}
