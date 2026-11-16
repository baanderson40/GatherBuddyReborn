using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan.Adaptive;

public sealed record AdaptivePublishedPlan(IReadOnlyList<VulcanSkill> Actions, int FinalQuality,
    bool TargetReached, int ActionCount, int ModeledDuration)
{
    public bool Executable => false;
    public string Usage => "Past live decision proposal; never an execution lease or observed final state";
    internal static AdaptivePublishedPlan? Capture(AdaptivePlan? plan) => plan == null || plan.Actions.Count > 64 ? null
        : new(Array.AsReadOnly(plan.Actions.ToArray()), plan.FinalState.Quality, plan.TargetReached, plan.Actions.Count, plan.Duration);
    internal static AdaptivePublishedPlan? Detach(AdaptivePublishedPlan? plan) => plan == null || plan.Actions.Count > 64
        || plan.ActionCount != plan.Actions.Count || plan.FinalQuality < 0 || plan.ModeledDuration < 0 ? null
        : plan with { Actions = Array.AsReadOnly(plan.Actions.ToArray()) };
}

/// <summary>Framework-owned coordinator notification, keyed by full structural identity. No extra replay.</summary>
public sealed record AdaptiveCoordinatorDiagnostic(AdaptiveDecisionIdentity Identity, string Kind, string Detail,
    AdaptiveSearchStatus? SearchStatus, AdaptiveSearchDiagnostics? Diagnostics, AdaptiveDecisionPhase? PublicationPhase,
    AdaptivePublishedPlan? Plan, string? CaptureWarning = null, VulcanSkill ProposedAction = VulcanSkill.None, TimeSpan? CapturedAt = null)
{
    internal static string Text(string value) => value.Length <= 1024 ? value : value[..1024] + " [bounded]";
    internal static AdaptiveSearchDiagnostics? Detach(AdaptiveSearchDiagnostics? diagnostics)
    {
        if (diagnostics?.Seeding is not { } seeding) return diagnostics;
        if (seeding.Candidates.Count > 2) return null; // Malformed diagnostic only; never change search/dispatch behavior.
        return diagnostics with { Seeding = seeding with
        {
            Candidates = Array.AsReadOnly(seeding.Candidates.Select(item => item with
            { Source = Text(item.Source), Status = Text(item.Status), Reason = Text(item.Reason) }).ToArray()),
            IncumbentSource = seeding.IncumbentSource == null ? null : Text(seeding.IncumbentSource),
            Comparison = Text(seeding.Comparison),
        } };
    }
}

public sealed record AdaptiveLiveRootRecord(long RootOrdinal, AdaptiveDecisionIdentity Identity,
    long ConfirmationRevision, int ConfirmedActionOrdinal, AdaptiveObservedFrame NativeFrame, TimeSpan SubmittedAt)
{
    public string StateSource => "NativeFrame contains direct reads; Identity.State combo/remaining-use availability derive only from correlated history";
    public TimeSpan? SearchDispatchedAt { get; init; }
    public TimeSpan? ResultObservedAt { get; init; }
    public TimeSpan? PublishedAt { get; init; }
    public AdaptiveSearchStatus? SearchStatus { get; init; }
    public string SearchOutcome { get; init; } = "Queued";
    public string? Reason { get; init; }
    public AdaptiveSearchDiagnostics? Diagnostics { get; init; }
    public AdaptivePublishedPlan? Plan { get; init; }
    public string? CaptureWarning { get; init; }
    public VulcanSkill ProposedAction { get; init; }
    public string ProposedActionName => ProposedAction.ToString();
    public TimeSpan? RevokedAt { get; init; }
    public string? RevocationReason { get; init; }
    public AdaptiveLiveTransaction? Transaction { get; init; }
    public TimeSpan? DispatchInvokedAt { get; init; }
    public TimeSpan? AcknowledgementMatchedAt { get; init; }
    public TimeSpan? FullFrameConfirmedAt { get; init; }
    public TimeSpan? TerminalAdmittedAt { get; init; }
    public TimeSpan? ClosedAt { get; init; }
    public string? CloseReason { get; init; }
    public TimeSpan? SubmissionToResultObservation => ResultObservedAt - SubmittedAt;
    public TimeSpan? PublicationToDispatch => DispatchInvokedAt - PublishedAt;
    public TimeSpan? DispatchToAcknowledgement => AcknowledgementMatchedAt - DispatchInvokedAt;
    public TimeSpan? DispatchToFullFrame => FullFrameConfirmedAt - DispatchInvokedAt;
    public TimeSpan? DispatchToTerminal => TerminalAdmittedAt - DispatchInvokedAt;
}

public sealed record AdaptiveUnlinkedLiveEvent(Guid SessionId, long Revision, long Generation, TimeSpan ObservedAt,
    string Kind, string Reason);

public sealed record AdaptiveLiveAnalysisSnapshot(long TotalRoots, long DroppedRoots, IReadOnlyList<AdaptiveLiveRootRecord> Roots,
    long TotalUnlinkedEvents, long DroppedLifecycleEvents, long CoordinatorEventsDropped,
    IReadOnlyList<AdaptiveUnlinkedLiveEvent> UnlinkedLifecycleEvents)
{
    public int RetainedRoots => Roots.Count;
    public string Usage => "Bounded past live decisions; proposals are not executable and are not observed final states";
    public string TimingSource => "Session-relative host submission/poll/publication/dispatch/confirmation times; not worker CPU start/completion or native execution duration. Plan duration is modeled.";
    public static AdaptiveLiveAnalysisSnapshot Empty { get; } = new(0, 0, Array.Empty<AdaptiveLiveRootRecord>(), 0, 0, 0, Array.Empty<AdaptiveUnlinkedLiveEvent>());
}

/// <summary>Host-thread-only bounded record keeper. Never executes, schedules, replays, or changes a decision.</summary>
public sealed class AdaptiveLiveAnalysisHistory(Guid session, TimeSpan origin)
{
    public const int Capacity = 64;
    private readonly List<AdaptiveLiveRootRecord> _roots = new();
    private readonly Queue<AdaptiveUnlinkedLiveEvent> _unlinked = new();
    private long _totalRoots, _totalUnlinked, _droppedLifecycle, _coordinatorDrops;
    private long _lastGeneration;
    public long Version { get; private set; }

    public void Submit(AdaptiveDecisionIdentity identity, long confirmationRevision, int actionOrdinal,
        AdaptiveObservedFrame frame, TimeSpan now)
    {
        if (identity.SessionId != session) { Unlinked(identity, now, "RootRejected", "Other-session identity."); return; }
        if (identity.Generation <= _lastGeneration) return;
        _lastGeneration = identity.Generation;
        if (_roots.Count == Capacity) _roots.RemoveAt(0);
        _roots.Add(new(++_totalRoots, identity, confirmationRevision, actionOrdinal, frame, now - origin));
        ++Version;
    }

    public void Observe(AdaptiveCoordinatorDiagnostic entry, TimeSpan now)
    {
        var index = Find(entry.Identity);
        if (index < 0)
        {
            Unlinked(entry.Identity, now, entry.Kind, entry.Identity.SessionId != session
                ? "Other-session lifecycle event; never attached to current craft. " + AdaptiveCoordinatorDiagnostic.Text(entry.Detail)
                : "Required root absent/evicted; no guessed linkage. " + AdaptiveCoordinatorDiagnostic.Text(entry.Detail));
            return;
        }
        var diagnostics = AdaptiveCoordinatorDiagnostic.Detach(entry.Diagnostics);
        var plan = AdaptivePublishedPlan.Detach(entry.Plan);
        entry = entry with { Detail = AdaptiveCoordinatorDiagnostic.Text(entry.Detail), Diagnostics = diagnostics, Plan = plan,
            CaptureWarning = entry.Plan != null && plan == null ? "Oversized/malformed diagnostic plan omitted; decision unchanged."
                : entry.Diagnostics != null && diagnostics == null ? "Oversized seed diagnostics omitted; decision unchanged."
                : entry.CaptureWarning == null ? null : AdaptiveCoordinatorDiagnostic.Text(entry.CaptureWarning) };
        var root = _roots[index];
        var time = (entry.CapturedAt ?? now) - origin;
        root = entry.Kind switch
        {
            "SearchDispatched" => root with { SearchDispatchedAt = time, SearchOutcome = "Searching" },
            "SearchResultObserved" => root with { ResultObservedAt = time, SearchStatus = entry.SearchStatus,
                Diagnostics = entry.Diagnostics, Reason = entry.Detail, CaptureWarning = entry.CaptureWarning },
            "SearchPublication" => root with { PublishedAt = entry.PublicationPhase == AdaptiveDecisionPhase.Ready ? time : null,
                SearchOutcome = entry.PublicationPhase?.ToString() ?? "Unpublished", SearchStatus = entry.SearchStatus,
                Plan = entry.Plan, ProposedAction = entry.ProposedAction, Diagnostics = entry.Diagnostics, Reason = entry.Detail, CaptureWarning = entry.CaptureWarning },
            "SearchResultRejected" or "SearchDispatchFailed" or "SearchQueueDiscarded" => root with { SearchOutcome = entry.Kind, Reason = entry.Detail },
            "SearchCancellationRequested" or "DecisionRevoked" when root.Transaction == null => root with { RevokedAt = time, RevocationReason = entry.Detail },
            _ => root,
        };
        if (root != _roots[index]) { _roots[index] = root; ++Version; }
    }

    public void Transaction(AdaptiveDecisionIdentity identity, AdaptiveLiveTransaction transaction, TimeSpan now)
    {
        if (transaction.Error != null) transaction = transaction with { Error = AdaptiveCoordinatorDiagnostic.Text(transaction.Error) };
        var index = Find(identity);
        if (index < 0) { Unlinked(identity, now, "TransactionUnlinked", "Required root absent/evicted; transaction not guessed from action ID."); return; }
        var root = _roots[index];
        if (root.Transaction is { } prior && prior.Request != transaction.Request)
        { Unlinked(identity, now, "TransactionConflict", "Different request ticket for existing root; original link retained."); return; }
        if (root.Transaction == transaction) return; // Stable frames do not allocate/update history or move resolution times.
        var time = now - origin;
        root = root with { Transaction = transaction,
            DispatchInvokedAt = transaction.InvocationBegan ? transaction.StartedAt - origin : root.DispatchInvokedAt,
            AcknowledgementMatchedAt = root.AcknowledgementMatchedAt ?? (transaction.AcknowledgementMatched ? time : null),
            FullFrameConfirmedAt = root.FullFrameConfirmedAt ?? (transaction.FullFrameCorrelated ? time : null),
            TerminalAdmittedAt = root.TerminalAdmittedAt ?? (transaction.Outcome == AdaptiveTransactionOutcome.TerminalConfirmed ? time : null),
            ClosedAt = root.ClosedAt ?? (transaction.Outcome != AdaptiveTransactionOutcome.AwaitingOutcome ? time : null),
            CloseReason = transaction.Error,
        };
        if (root != _roots[index]) { _roots[index] = root; ++Version; }
    }

    public void RevokeUndispatched(TimeSpan now, string reason)
    {
        for (var index = 0; index < _roots.Count; ++index)
        {
            var root = _roots[index];
            if (root.Transaction != null || root.RevokedAt.HasValue) continue;
            _roots[index] = root with { RevokedAt = now - origin, RevocationReason = AdaptiveCoordinatorDiagnostic.Text(reason) };
            ++Version;
        }
    }

    public void CoordinatorDrops(long count)
    {
        if (count == _coordinatorDrops) return;
        _coordinatorDrops = count;
        ++Version;
    }

    private int Find(AdaptiveDecisionIdentity identity) => identity.SessionId != session ? -1 : _roots.FindIndex(root => root.Identity == identity);
    private void Unlinked(AdaptiveDecisionIdentity identity, TimeSpan now, string kind, string reason)
    {
        ++_totalUnlinked;
        if (_unlinked.Count == Capacity) { _unlinked.Dequeue(); ++_droppedLifecycle; }
        _unlinked.Enqueue(new(identity.SessionId, identity.ObservationRevision, identity.Generation, now - origin,
            AdaptiveCoordinatorDiagnostic.Text(kind), AdaptiveCoordinatorDiagnostic.Text(reason)));
        ++Version;
    }

    public AdaptiveLiveAnalysisSnapshot Capture() => new(_totalRoots, _totalRoots - _roots.Count,
        Array.AsReadOnly(_roots.ToArray()), _totalUnlinked, _droppedLifecycle, _coordinatorDrops, Array.AsReadOnly(_unlinked.ToArray()));
}
