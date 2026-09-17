using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Utility;

namespace ErpWeb.EInvoiceLib.Model
{
    /// <summary>
    /// Author     : TH MOK
    /// Created On : 2024-Feb-20
    /// Company    : Wincom IT Solutions SDN BHD.
    /// Description: Inital the API project for Mock test purpose
    /// </summary>
    /// 

    public class NotificationResp
    {
        //Array of notification objects
        public List<Notification> result { get; set; }
    }

    public class Notification
    {
        //Unique ID of the notification.
        public string notificationId { get; set; }
        //The date and time when notification was sent out
        public DateTime? receivedDateTime { get; set; }
        //Optional date time when notification was delivered
        public DateTime? deliveredDateTime { get; set; }
        //Id of the type of the message
        public string typeId { get; set; }
        //Type name of the message
        public string typeName { get; set; }
        //Optional: final message that was sent out - depends on the channel
        public string finalMessage { get; set; }
        //Channel used for delivery. Values: email, push
        public string channel { get; set; }
        //Channel address that was used to deliver the message eg test@test.com
        public string address { get; set; }
        //Language used for delivery.Values:ms,en
        public string language { get; set; }
        //Status of the notification delivery. Values - pending, batched, delivered, error
        public string status { get; set; }
        public List<DeliveryAttempts> deliveryAttempts { get; set; }
        //Total count of pages based on the supplied (or default) page size
        public int totalPages { get; set; }
        //Total count of matching objects
        public int totalCount { get; set; }

    }

    public class DeliveryAttempts
    {
        //Date time when delivery was attempted. 2015-02-13T14:20Z
        public DateTime? attemptDateTime { get; set; }
        //Status of the notification delivery. Values - delivered, error
        public string status { get; set; }
        //Error message in case of error in delivery
        public string statusDetails { get; set; }
    }


    public class NotificationInput
    {
        //Optional: start date and time for notifications to retrieve based on the date sent  2015-02-13T14:20Z
        public DateTime? dateFrom { get; set; }

        //Optional: end date and time for notifications to retrieve based on the date sent 2015-02-14T14:20Z
        public DateTime? dateTo { get; set; }

        //Optional: type of notifications to retrieve specified as ID of the type. See Notification types
        public string type { get; set; }

        //Optional: used to get notifications only if they were sent out in a specific language. Values:msand en
        public string language { get; set; }

        //Optional: used to get notifications of certain status only, e.g., only those that were not delivered. Values: pending, batched, delivered, error
        public string status { get; set; }

        //Optional: used to get notifications delivered over certain channel only. Values: email, push
        public string channel { get; set; }

        //Optional: number of the page to retrieve. Typically this parameter value is derived from initial parameter less call when caller learns total amount of page of certain size
        public int? pageNo { get; set; }
        //Total count of matching objects

        //Optional: number of the packages to retrieve per page. Page size cannot exceed system configured maximum page size for this API which is 100
        public int? pageSize { get; set; }

        public string getQueryString()
        {
            //GET /api/v1.0/notifications/taxpayer
            //?dateFrom={dateFrom}&
            //dateTo={dateTo}&
            //type={type}&
            //language={language}&
            //status={status}&
            //channel={channel}&
            //pageNo={pageNo}&
            //pageSize={pageSize}

            string query = "";
            if (dateFrom.HasValue)
            {
                var json = JsonDateTimeUtil.getJsonDate_WithHHmm(dateFrom.Value);
                query = query + "dateFrom=" + json + "&";
            }
            if (dateTo.HasValue)
            {
                var json = JsonDateTimeUtil.getJsonDate_WithHHmm(dateTo.Value);
                query = query + "dateTo=" + json + "&";
            }
            if (!string.IsNullOrEmpty(type))
            {
                query = query + "type=" + type + "&";
            }
            if (!string.IsNullOrEmpty(language))
            {
                query = query + "language=" + language + "&";
            }
            if (!string.IsNullOrEmpty(status))
            {
                query = query + "status=" + status + "&";
            }
            if (!string.IsNullOrEmpty(channel))
            {
                query = query + "channel=" + channel + "&";
            }
            if (pageNo.HasValue)
            {
                query = query + "pageNo=" + pageNo.Value.ToString() + "&";
            }

            if (pageSize.HasValue)
            {
                query = query + "pageSize=" + pageSize.Value.ToString() + "&";
            }

            if (query.Length > 0)
            {
                query = query.Substring(0, query.Length - 1);
            }

            return query;
        }
    }
}
