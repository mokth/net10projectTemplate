namespace ErpWeb.UI.Components.Common.DataGrid;

/// <summary>
/// The ONE definition of the row-audit column set every list view must display.
///
/// <para>
/// Maps 1:1 onto the persisted audit columns (<c>Created</c>, <c>UserID</c>, <c>ModifiedDate</c>,
/// <c>ModifiedBy</c> in the legacy schema; <c>CreatedDate</c>, <c>CreatedBy</c>, <c>ModifiedDate</c>,
/// <c>ModifiedBy</c> on the entities). Every list-row DTO must expose those four properties, because
/// <c>DxGridDataColumn.FieldName</c> is resolved by property name at render time.
/// </para>
///
/// <para>
/// Usage — append the spread to the page's existing column list:
/// <code>
/// public List&lt;GridColumnData&gt; Columns() =&gt;
/// [
///     new() { Caption = "Code", FieldName = nameof(XListRow.Code), VisibleIndex = 1 },
///     ..AuditColumns.For(startVisibleIndex: 2)
/// ];
/// </code>
/// </para>
/// </summary>
public static class AuditColumns
{
    /// <summary>Captions, in the order the columns are rendered.</summary>
    public const string CreatedCaption = "Created";
    public const string CreatedByCaption = "User ID";
    public const string ModifiedCaption = "Modified Date";
    public const string ModifiedByCaption = "Modified By";

    public const string DateDisplayFormat = "dd/MM/yyyy HH:mm";

    /// <summary>Digits of <c>VisibleIndex</c> the audit block occupies (4).</summary>
    public const int Count = 4;

    /// <summary>
    /// The four audit columns, starting at <paramref name="startVisibleIndex"/>.
    /// Field names are the property names every list-row DTO must carry.
    /// </summary>
    public static GridColumnData[] For(int startVisibleIndex) =>
    [
        new()
        {
            Caption = CreatedCaption,
            FieldName = "CreatedDate",
            DataType = "date",
            DisplayFormat = DateDisplayFormat,
            Width = "140px",
            VisibleIndex = startVisibleIndex
        },
        new()
        {
            Caption = CreatedByCaption,
            FieldName = "CreatedBy",
            DataType = "string",
            Width = "110px",
            VisibleIndex = startVisibleIndex + 1
        },
        new()
        {
            Caption = ModifiedCaption,
            FieldName = "ModifiedDate",
            DataType = "date",
            DisplayFormat = DateDisplayFormat,
            Width = "140px",
            VisibleIndex = startVisibleIndex + 2
        },
        new()
        {
            Caption = ModifiedByCaption,
            FieldName = "ModifiedBy",
            DataType = "string",
            Width = "110px",
            VisibleIndex = startVisibleIndex + 3
        }
    ];
}
