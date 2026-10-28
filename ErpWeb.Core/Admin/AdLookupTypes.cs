namespace ErpWeb.Core.Admin;

/// <summary>
/// The controlled set of lookup families. A user may maintain the <b>values</b> under an approved
/// type, but can never invent a new <see cref="LookupType"/>: the family list is code.
///
/// <para>
/// Keeping this list closed is what stops the generic table becoming a dumping ground. Anything that
/// carries business attributes, hierarchy or operational logic stays a dedicated master.
/// </para>
/// </summary>
public static class AdLookupTypes
{
    public const string WarehouseType = "WAREHOUSE_TYPE";
    public const string MachineType = "MACHINE_TYPE";
    public const string WorkCentreClass = "WORK_CENTRE_CLASS";
    public const string SupplierType = "SUPPLIER_TYPE";
    public const string SupplierSubGroup = "SUPPLIER_SUBGROUP";

    // Reserved for the separately approved future customer migration (plan §15). Registered so the
    // metadata is stable, but deliberately NOT seeded or consumed by the initial deployment.
    public const string CustomerIndustry = "CUSTOMER_INDUSTRY";
    public const string CustomerChannel = "CUSTOMER_CHANNEL";
    public const string CustomerSource = "CUSTOMER_SOURCE";
}
