using ErpWeb.Core.Services;

namespace ErpWeb.Core.Production;

/// <summary>
/// One refused production batch: the technical message the service already returned plus, when the
/// boundary could prove one, the operator-language explanation.
/// </summary>
/// <remarks>
/// Presentation only. It never decides whether a batch succeeded: <see cref="Message"/> being null is
/// still the single success signal, and every existing caller keeps reading it unchanged. The implicit
/// conversion from <c>string?</c> exists so the many existing <c>return "..."</c> statements keep their
/// exact text and behaviour.
/// </remarks>
internal readonly record struct ProductionPostingFailure(
    string? Message,
    ErpOperatorError? OperatorError)
{
    public static implicit operator ProductionPostingFailure(string? message) =>
        new(message, null);
}
