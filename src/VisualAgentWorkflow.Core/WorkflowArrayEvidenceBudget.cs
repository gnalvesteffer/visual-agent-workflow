using System.Text;
using System.Text.Json;
using System.Globalization;

namespace VisualAgentWorkflow.Core;

public sealed class WorkflowArrayEvidenceBudgetExceededException(string message) : InvalidOperationException(message);

/// <summary>Bounds typed-array values retained as workflow run evidence.</summary>
public sealed class WorkflowArrayEvidenceBudget
{
    public const int MaximumAggregateBytes = 65_536;

    private readonly object sync = new();
    private readonly WorkflowRunReservation? reservation;
    private readonly WorkflowRunReservation.Account? account;
    private int usedBytes;

    public WorkflowArrayEvidenceBudget() { }

    /// <summary>
    /// Draws the aggregate bytes from <paramref name="account"/> so they are counted atomically with the run's other
    /// reservations. The account's byte cap should be <see cref="MaximumAggregateBytes"/>.
    /// </summary>
    public WorkflowArrayEvidenceBudget(WorkflowRunReservation reservation, WorkflowRunReservation.Account account)
    {
        this.reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));
        this.account = account ?? throw new ArgumentNullException(nameof(account));
    }

    public int UsedBytes { get { lock (sync) return reservation is null ? usedBytes : reservation.ArrayEvidenceBytesUsed(account!); } }

    /// <summary>Charges each typed-array value retained in evaluation or variable-state evidence.</summary>
    public void Consume(string dataType, params object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!WorkflowDataTypes.TryGetArrayElementType(dataType, out var elementType)) return;

        var bytes = 0;
        foreach (var value in values)
        {
            var json = value switch
            {
                JsonElement element when element.ValueKind == JsonValueKind.Array => element.GetRawText(),
                string text => text,
                _ => throw new ArgumentException($"Evidence for '{dataType}' must contain a JSON array value.", nameof(values))
            };
            if (!WorkflowArrayLiteral.TryNormalizeValue(elementType, json, out var compact, out var error))
                throw new ArgumentException($"Evidence for '{dataType}' is invalid: {error}", nameof(values));
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(compact));
        }

        lock (sync)
        {
            ConsumeBytes(bytes);
        }
    }

    private void ConsumeBytes(int bytes)
    {
        if (reservation is not null)
        {
            if (!reservation.TryReserve(account, new WorkflowReservationRequest(ArrayEvidenceBytes: bytes)))
                throw new WorkflowArrayEvidenceBudgetExceededException(
                    $"Typed array evidence exceeds the {MaximumAggregateBytes.ToString("N0", CultureInfo.InvariantCulture)}-byte aggregate limit.");
            return;
        }
        if (bytes > MaximumAggregateBytes - usedBytes)
            throw new WorkflowArrayEvidenceBudgetExceededException(
                $"Typed array evidence exceeds the {MaximumAggregateBytes.ToString("N0", CultureInfo.InvariantCulture)}-byte aggregate limit.");
        usedBytes += bytes;
    }
}
