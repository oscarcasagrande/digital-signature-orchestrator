namespace Orchestrator.Domain.StateMachines;

public sealed class InvalidTransitionException(string machine, string from, string to)
    : Exception($"Invalid {machine} transition {from} -> {to}")
{
    public string Machine { get; } = machine;
    public string From { get; } = from;
    public string To { get; } = to;
}

public static class BusinessStateMachine
{
    public static readonly IReadOnlySet<BusinessStatus> Terminal = new HashSet<BusinessStatus>
    {
        BusinessStatus.COMPLETED, BusinessStatus.FAILED, BusinessStatus.CANCELLED,
        BusinessStatus.EXPIRED, BusinessStatus.REJECTED
    };

    private static readonly Dictionary<BusinessStatus, BusinessStatus[]> Forward = new()
    {
        [BusinessStatus.CREATED] = [BusinessStatus.DOCUMENT_RECEIVED],
        [BusinessStatus.DOCUMENT_RECEIVED] = [BusinessStatus.VALIDATING],
        [BusinessStatus.VALIDATING] = [BusinessStatus.READY_FOR_SIGNATURE],
        [BusinessStatus.READY_FOR_SIGNATURE] = [BusinessStatus.SIGNATURE_IN_PROGRESS],
        [BusinessStatus.SIGNATURE_IN_PROGRESS] = [BusinessStatus.PARTIALLY_SIGNED, BusinessStatus.SIGNED, BusinessStatus.REJECTED],
        [BusinessStatus.PARTIALLY_SIGNED] = [BusinessStatus.SIGNED, BusinessStatus.REJECTED],
        [BusinessStatus.SIGNED] = [BusinessStatus.FINALIZING],
        [BusinessStatus.FINALIZING] = [BusinessStatus.COMPLETED],
    };

    public static bool IsTerminal(BusinessStatus s) => Terminal.Contains(s);

    public static bool CanTransition(BusinessStatus from, BusinessStatus to)
    {
        if (IsTerminal(from)) return false;
        if (to is BusinessStatus.FAILED or BusinessStatus.CANCELLED or BusinessStatus.EXPIRED) return true;
        return Forward.TryGetValue(from, out var next) && next.Contains(to);
    }

    public static void Ensure(BusinessStatus from, BusinessStatus to)
    {
        if (!CanTransition(from, to)) throw new InvalidTransitionException("business", from.ToString(), to.ToString());
    }
}

public static class OperationalStateMachine
{
    private static readonly Dictionary<OperationalStatus, OperationalStatus[]> Map = new()
    {
        [OperationalStatus.READY] = [OperationalStatus.PROCESSING, OperationalStatus.SUSPENDED, OperationalStatus.MANUAL_ACTION],
        [OperationalStatus.PROCESSING] = [OperationalStatus.READY, OperationalStatus.RETRY_PENDING, OperationalStatus.SUSPENDED, OperationalStatus.DLQ, OperationalStatus.MANUAL_ACTION],
        [OperationalStatus.RETRY_PENDING] = [OperationalStatus.PROCESSING, OperationalStatus.READY, OperationalStatus.SUSPENDED, OperationalStatus.DLQ, OperationalStatus.MANUAL_ACTION],
        [OperationalStatus.SUSPENDED] = [OperationalStatus.READY],
        [OperationalStatus.MANUAL_ACTION] = [OperationalStatus.READY],
        [OperationalStatus.DLQ] = [OperationalStatus.READY, OperationalStatus.MANUAL_ACTION],
    };

    public static bool CanTransition(OperationalStatus from, OperationalStatus to) =>
        Map.TryGetValue(from, out var next) && next.Contains(to);

    public static void Ensure(OperationalStatus from, OperationalStatus to)
    {
        if (!CanTransition(from, to)) throw new InvalidTransitionException("operational", from.ToString(), to.ToString());
    }
}
