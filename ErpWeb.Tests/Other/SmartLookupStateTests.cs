using ErpWeb.UI.Components.Common.Lookups;

namespace ErpWeb.Tests.Other;

[Trait(TestCategories.Name, TestCategories.Shared)]
public class SmartLookupStateTests
{
    [Fact]
    public void Delayed_resolve_commits_valid_value()
    {
        var state = new SmartLookupState();
        state.BeginUserEdit("ITEM001", resolveNow: false);
        Assert.Equal(SmartLookupStatus.Editing, state.Status);

        var request = state.BeginResolve();
        Assert.NotNull(request);
        Assert.False(request.Value.IsClear);
        Assert.Equal("ITEM001", request.Value.Text);

        Assert.True(state.TryApplyResolved(request.Value.Sequence, "ITEM001", "ITEM001", "Widget"));
        Assert.Equal(SmartLookupStatus.Committed, state.Status);
        Assert.Equal("ITEM001", state.CommittedValue);
        Assert.Equal("Widget", state.SecondaryText);
        Assert.Equal("ITEM001", state.InputText);
    }

    [Fact]
    public void Enter_resolves_immediately()
    {
        var state = new SmartLookupState();
        var request = state.BeginUserEdit("CUST0001", resolveNow: true);
        Assert.NotNull(request);
        Assert.Equal(SmartLookupStatus.Resolving, state.Status);
        Assert.Equal("CUST0001", request.Value.Text);

        Assert.True(state.TryApplyResolved(request.Value.Sequence, "CUST0001", "CUST0001", "ABC Trading"));
        Assert.Equal(SmartLookupStatus.Committed, state.Status);
    }

    [Fact]
    public void Escape_restores_committed_value()
    {
        var state = new SmartLookupState();
        SeedCommitted(state, "C00001", "C00001", "Original Co");

        state.BeginUserEdit("C00999", resolveNow: false);
        Assert.Equal("C00999", state.InputText);
        Assert.Equal(SmartLookupStatus.Editing, state.Status);

        state.CancelEdit();
        Assert.Equal("C00001", state.InputText);
        Assert.Equal("C00001", state.CommittedValue);
        Assert.Equal(SmartLookupStatus.Committed, state.Status);
        Assert.Null(state.ErrorMessage);
    }

    [Fact]
    public void Old_request_result_is_ignored()
    {
        var state = new SmartLookupState();
        var first = state.BeginUserEdit("ABC", resolveNow: true);
        Assert.NotNull(first);

        var second = state.BeginUserEdit("ABC001", resolveNow: true);
        Assert.NotNull(second);
        Assert.True(second.Value.Sequence > first.Value.Sequence);

        Assert.False(state.TryApplyResolved(first.Value.Sequence, "ABC", "ABC", null));
        Assert.Equal(SmartLookupStatus.Resolving, state.Status);

        Assert.True(state.TryApplyResolved(second.Value.Sequence, "ABC001", "ABC001", null));
        Assert.Equal("ABC001", state.CommittedValue);
    }

    [Fact]
    public void Not_found_delayed_clear_is_ignored_after_newer_text()
    {
        var state = new SmartLookupState();
        SeedCommitted(state, "SUP001", "SUP001", "Vendor A");

        var bad = state.BeginUserEdit("SUP999", resolveNow: true);
        Assert.NotNull(bad);
        Assert.True(state.TryApplyNotFound(bad.Value.Sequence, "Supplier not found"));
        Assert.Equal(SmartLookupStatus.NotFound, state.Status);

        // User keeps typing before restore fires.
        state.BeginUserEdit("SUP9999", resolveNow: false);
        Assert.Equal(SmartLookupStatus.Editing, state.Status);

        Assert.False(state.TryRestoreAfterNotFound(bad.Value.Sequence));
        Assert.Equal("SUP9999", state.InputText);
        Assert.Equal("SUP001", state.CommittedValue);
    }

    [Fact]
    public void Invalid_replacement_does_not_clear_committed_value()
    {
        var state = new SmartLookupState();
        SeedCommitted(state, "C00001", "C00001", "ABC Trading");

        var bad = state.BeginUserEdit("C00999", resolveNow: true);
        Assert.NotNull(bad);
        Assert.True(state.TryApplyNotFound(bad.Value.Sequence, "Customer not found"));

        Assert.Equal("C00001", state.CommittedValue);
        Assert.True(state.TryRestoreAfterNotFound(bad.Value.Sequence));
        Assert.Equal("C00001", state.InputText);
        Assert.Equal("C00001", state.CommittedValue);
        Assert.Equal(SmartLookupStatus.Committed, state.Status);
    }

    [Fact]
    public void Explicit_clear_empties_committed_value()
    {
        var state = new SmartLookupState();
        SeedCommitted(state, "ITEM001", "ITEM001", "Widget");

        var clear = state.BeginUserEdit(string.Empty, resolveNow: true);
        Assert.NotNull(clear);
        Assert.True(clear.Value.IsClear);

        Assert.True(state.TryApplyExplicitClear(clear.Value.Sequence));
        Assert.Null(state.CommittedValue);
        Assert.Equal(string.Empty, state.InputText);
        Assert.Equal(SmartLookupStatus.Empty, state.Status);
    }

    [Fact]
    public void Not_found_with_no_prior_value_clears_input_only()
    {
        var state = new SmartLookupState();
        var bad = state.BeginUserEdit("MISSING", resolveNow: true);
        Assert.NotNull(bad);
        Assert.True(state.TryApplyNotFound(bad.Value.Sequence, "Not found"));
        Assert.True(state.TryRestoreAfterNotFound(bad.Value.Sequence));

        Assert.Null(state.CommittedValue);
        Assert.Equal(string.Empty, state.InputText);
        Assert.Equal(SmartLookupStatus.Empty, state.Status);
    }

    [Fact]
    public void Search_seed_uses_pending_typed_text()
    {
        var state = new SmartLookupState();
        SeedCommitted(state, "ITEM001", "ITEM001", null);
        state.BeginUserEdit("ITEM", resolveNow: false);
        Assert.Equal("ITEM", state.GetSearchSeedText());
    }

    private static void SeedCommitted(
        SmartLookupState state,
        string value,
        string display,
        string? secondary)
    {
        state.SyncFromExternal(value, display, secondary);
        Assert.Equal(SmartLookupStatus.Committed, state.Status);
    }
}
