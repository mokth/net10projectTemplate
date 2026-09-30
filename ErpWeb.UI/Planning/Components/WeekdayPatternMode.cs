namespace ErpWeb.UI.Planning.Components;

public enum WeekdayPatternMode
{
    /// <summary>Machine calendar: explicit work and off sets (mutual exclusion).</summary>
    WorkAndOff = 0,

    /// <summary>Company calendar: only offs tracked; other days are work.</summary>
    OffOnly = 1
}
