namespace ErpWeb.Core.Sales.Delivery;

public static class SaDeliveryTripStatuses
{
    public const string Planned = "PLANNED";
    public const string OutForDelivery = "OUT_FOR_DELIVERY";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";
}

public static class SaDeliveryStopStatuses
{
    public const string Planned = "PLANNED";
    public const string OutForDelivery = "OUT_FOR_DELIVERY";
    public const string Delivered = "DELIVERED";
    public const string PartiallyDelivered = "PARTIALLY_DELIVERED";
    public const string Failed = "FAILED";
    public const string Rescheduled = "RESCHEDULED";
    public const string Cancelled = "CANCELLED";
}

public static class SaDeliveryAttemptResults
{
    public const string Delivered = "DELIVERED";
    public const string Partial = "PARTIAL";
    public const string Failed = "FAILED";
}

public static class SaDeliveryResponsibilities
{
    public const string Driver = "DRIVER";
    public const string Warehouse = "WAREHOUSE";
    public const string Customer = "CUSTOMER";
    public const string Sales = "SALES";
    public const string Vehicle = "VEHICLE";
    public const string External = "EXTERNAL";
    public const string WeatherTraffic = "WEATHER_TRAFFIC";
    public const string Other = "OTHER";
}

public static class SaDeliveryTransportTypes
{
    public const string Own = "OWN";
    public const string ThirdParty = "THIRD_PARTY";
}

public static class SaDeliveryDriverTypes
{
    public const string Employee = "EMPLOYEE";
    public const string External = "EXTERNAL";
}

public static class SaDeliveryAttachmentTypes
{
    public const string Photo = "PHOTO";
    public const string Signature = "SIGNATURE";
    public const string Document = "DOCUMENT";
}

public static class SaDeliveryExceptionReasonCodes
{
    public const string CustomerClosed = "CUSTOMER_CLOSED";
    public const string CustomerUnavailable = "CUSTOMER_UNAVAILABLE";
    public const string CustomerRejected = "CUSTOMER_REJECTED";
    public const string WrongAddress = "WRONG_ADDRESS";
    public const string WarehouseNotReady = "WAREHOUSE_NOT_READY";
    public const string ShortGoods = "SHORT_GOODS";
    public const string DamagedGoods = "DAMAGED_GOODS";
    public const string VehicleBreakdown = "VEHICLE_BREAKDOWN";
    public const string Traffic = "TRAFFIC";
    public const string Weather = "WEATHER";
    public const string DriverIssue = "DRIVER_ISSUE";
    public const string Other = "OTHER";
}

public static class SaDeliveryNumbering
{
    public const string Module = "DT";
}
