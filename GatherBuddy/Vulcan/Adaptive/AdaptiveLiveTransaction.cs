using System;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Dispatch result describes host control flow, never native acceptance or action success.</summary>
public enum AdaptiveDispatchResult { NotStarted, AwaitingOutcome, Stopped }
public enum AdaptiveTransactionOutcome { AwaitingOutcome, Confirmed, TerminalConfirmed, Unconfirmed, ProtocolFailure, Exception, Stopped }

/// <summary>Immutable last-transaction diagnostic; no reusable lease or predicted outcome is exported.</summary>
public sealed record AdaptiveLiveTransaction(AdaptiveShadowRequest Request, VulcanSkill Action, uint NativeActionId,
    int Ordinal, TimeSpan StartedAt, bool InvocationBegan, bool? NativeReturn,
    AdaptiveTransactionOutcome Outcome = AdaptiveTransactionOutcome.AwaitingOutcome, string? Error = null,
    bool AcknowledgementMatched = false, bool FullFrameCorrelated = false)
{
    public string ActionName => Action.ToString();
    public string OutcomeName => Outcome.ToString();
}
