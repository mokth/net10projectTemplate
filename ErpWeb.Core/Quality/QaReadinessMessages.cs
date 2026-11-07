namespace ErpWeb.Core.Quality;

public static class QaReadinessMessages
{
    public const string NotInstalled =
        "Quality is not installed on this database. Inspection cases cannot be saved. Goods receipt and production posting continue without a quality gate.";

    public const string Incomplete =
        "Quality schema is incomplete. Repair the quality tables before using inspection or posting a document that still has quality history.";

    public const string ProbeError =
        "Quality is temporarily unavailable. Try again or contact an administrator.";

    public const string IncomingFrozen =
        "Incoming inspection cannot authorize goods receipt yet. Quality is record-only until this gate is released.";

    public const string FinalFgFrozen =
        "Finished-goods inspection cannot authorize a finished-good receipt yet. Quality is record-only until this gate is released.";

    public const string LogicalFrozen =
        "Quality cannot place a production-lot hold yet. Inspection is record-only until this gate is released.";

    public const string PhysicalFrozen =
        "Quality cannot place or claim a warehouse hold yet. Inspection is record-only until this gate is released.";

    public const string NonGoodFrozen =
        "Quality cannot authorize Daily Production scrap/reject/hold qty yet. Inspection is record-only until this gate is released.";

    public const string CannotTurnOffWithObligations =
        "Quality gates cannot be turned off while posted allocations or active holds exist.";

    public static string FrozenEnablement(string gate) =>
        $"Cannot enable {gate}. Quality posting gates are frozen. Inspection stays record-only.";
}

public static class QaSchemaProvenance
{
    public const string Unknown = "QA_HISTORY_UNKNOWN";
    public const string PreviouslyDeployed = "PREVIOUSLY_DEPLOYED";
}
