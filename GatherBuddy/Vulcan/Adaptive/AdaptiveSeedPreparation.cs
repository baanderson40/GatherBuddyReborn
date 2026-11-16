using System;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Pre-synthesis request identity. Expected quality is policy input, never a native observation.</summary>
public sealed record AdaptiveSeedPreparationContext(ulong Character, uint Job, AdaptiveCraftSnapshot Craft,
    AdaptiveCraftGoal Goal, string Policy, string RequestKey, int ExpectedInitialQuality);

public enum AdaptiveSeedLookupState { Missing, Pending, Ready, Failed, Unavailable }
public sealed record AdaptiveSeedLookup(AdaptiveSeedLookupState State, AdaptivePlanSeed? Seed = null, string Reason = "");
public sealed record AdaptiveLiveSeedStatus(string Phase, string Reason, string? RequestKey = null,
    string? Source = null, int? ActionCount = null, bool GenerationRequested = false,
    int? ExpectedInitialQuality = null, int? ObservedInitialQuality = null);

/// <summary>One bounded, host-polled attempt. No tasks, native reads, retries, or shared-solve cancellation.</summary>
public sealed class AdaptiveSeedPreparation
{
    private readonly TimeSpan _deadline;
    private TimeSpan _lastTime;
    private AdaptiveSeedPreparationContext? _context;
    private AdaptivePlanSeed? _candidate;
    private bool _requested, _done, _consumed, _cancelled;
    public AdaptiveLiveSeedStatus Status { get; private set; } = new("NotAttempted", "Raphael seed preparation not attempted.");

    public AdaptiveSeedPreparation(TimeSpan now, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _lastTime = now;
        _deadline = now + timeout;
    }

    public bool Poll(AdaptiveSeedPreparationContext context, TimeSpan now,
        Func<AdaptiveSeedPreparationContext, AdaptiveSeedLookup> lookup, Action<AdaptiveSeedPreparationContext> queueOrPromote)
    {
        if (_cancelled || _consumed) return true;
        if (_context != null && _context != context) return Fallback("Preparation context changed; using unseeded search.");
        _context ??= context;
        if (now < _lastTime) return Fallback("Preparation clock regressed; using unseeded search.");
        _lastTime = now;
        if (_done) return true; // Freeze completed candidate. Never adopt a late publication.
        if (now >= _deadline) return Fallback("Raphael preparation timeout (including queue wait); using unseeded search.");
        try
        {
            var read = lookup(context);
            switch (read.State)
            {
                case AdaptiveSeedLookupState.Ready:
                    var seed = read.Seed;
                    if (seed == null || seed.Actions.Count > 64 || seed.Provenance.RequestKey != context.RequestKey
                        || !MatchesCraft(seed, context.Craft) || seed.Provenance.InitialQuality != context.ExpectedInitialQuality)
                        return Fallback("Cached seed is unsupported, oversized, or does not match the prepared request; using unseeded search.");
                    _candidate = seed;
                    _done = true;
                    Set("Prepared", "Detached Raphael candidate prepared; actual stable starting-root replay still required.");
                    return true;
                case AdaptiveSeedLookupState.Failed:
                case AdaptiveSeedLookupState.Unavailable:
                    return Fallback($"Raphael seed unavailable: {read.Reason}; using unseeded search.");
                default:
                    if (_requested && read.State == AdaptiveSeedLookupState.Missing)
                        return Fallback("Raphael request disappeared or could not be queued; using unseeded search.");
                    if (!_requested)
                    {
                        _requested = true; // One enqueue/promotion attempt, including synchronous failure.
                        queueOrPromote(context);
                    }
                    Set("Waiting", "Waiting before synthesis for Raphael seed generation (shared queue; no duplicate solve).");
                    return false;
            }
        }
        catch (Exception error) { return Fallback($"Raphael preparation exception ({error.GetType().Name}); using unseeded search."); }
    }

    public bool Fallback(string reason)
    {
        if (_cancelled || _consumed) return true;
        _candidate = null;
        _done = true;
        Set("Unseeded", reason);
        return true;
    }

    public void Cancel()
    {
        _candidate = null;
        _cancelled = _done = true;
        Set("Cancelled", "Preparation revoked; no candidate may attach to another craft.");
    }

    public AdaptivePlanSeed? Consume(AdaptiveSeedPreparationContext actualContext)
    {
        if (_cancelled || _consumed) return null;
        if (!_done || _context != actualContext) Fallback("Preparation missing, unfinished, or stale at craft start; using unseeded search.");
        _consumed = true;
        var seed = _candidate;
        _candidate = null;
        if (seed != null) Set("AwaitingRootReplay", "Candidate handed off once; no simulated state substituted for native observations.", seed);
        return seed;
    }

    private void Set(string phase, string reason, AdaptivePlanSeed? seed = null)
    {
        seed ??= _candidate;
        Status = new(phase, Text(reason), _context == null ? null : Text(_context.RequestKey),
            seed == null ? null : Text(seed.Provenance.Source), seed?.Actions.Count, _requested, _context?.ExpectedInitialQuality);
    }

    internal static bool MatchesCraft(AdaptivePlanSeed seed, AdaptiveCraftSnapshot craft)
    {
        var p = seed.Provenance;
        return p.RecipeId == craft.RecipeId && p.Level == craft.Level && p.Craftsmanship == craft.Craftsmanship
            && p.Control == craft.Control && p.MaxCP == craft.MaxCP && p.Manipulation == craft.ManipulationUnlocked && !p.Specialist;
    }

    internal static string Text(string text) => text.Length <= 1024 ? text : text[..1024] + " [truncated]";
}
