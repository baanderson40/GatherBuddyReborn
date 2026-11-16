using System;
using System.Collections.Generic;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>Pure request-correlated alignment. Never advances on an attempt or native step count.</summary>
public sealed class AdaptiveSeedTracker
{
    private readonly AdaptivePlanSeed? _raphael;
    private readonly string _captureReason;
    private readonly List<(long Ordinal, VulcanSkill Action)> _journal = new();
    private long? _raphaelDivergedAt;
    private AdaptivePlanSeed? _adaptive;
    private long _adaptiveOrdinal;
    private long _adaptiveRevision;
    public long Ordinal { get; private set; }

    public AdaptiveSeedTracker(AdaptivePlanSeed? raphael, string captureReason)
        => (_raphael, _captureReason) = (raphael, captureReason);

    public void Correlate(VulcanSkill action)
    {
        ++Ordinal;
        if (_raphael != null && !_raphaelDivergedAt.HasValue
            && (Ordinal > _raphael.Actions.Count || _raphael.Actions[(int)Ordinal - 1] != action))
            _raphaelDivergedAt = Ordinal;
        if (_journal.Count == 256) _journal.RemoveAt(0);
        _journal.Add((Ordinal, action));
    }

    public void Remember(AdaptiveHistoricalObservation source, AdaptivePlan plan)
    {
        if (source.ActionOrdinal > Ordinal || plan.Actions.Count is < 1 or > 256) return;
        if (_adaptive != null && source.ActionOrdinal < _adaptiveOrdinal) return;
        var craft = source.Identity.Craft;
        var seed = new AdaptivePlanSeed(new("AdaptiveContinuation", $"revision:{source.Identity.ObservationRevision}",
            craft.RecipeId, craft.Level, craft.Craftsmanship, craft.Control, craft.MaxCP,
            craft.ManipulationUnlocked, false, source.Identity.State.Quality), plan.Actions);
        if (!TryCursor(seed, source.ActionOrdinal, Ordinal, out _, out _)) return;
        _adaptive = seed;
        _adaptiveOrdinal = source.ActionOrdinal;
        _adaptiveRevision = source.Identity.ObservationRevision;
    }

    public AdaptiveSeedBatch Capture(AdaptiveCraftSnapshot craft, long targetOrdinal)
    {
        var candidates = new List<AdaptiveSeedCandidate>(2);
        var availability = new List<AdaptiveSeedAvailability>(2);
        if (_raphael == null) availability.Add(new("RaphaelCache", "Unavailable", _captureReason));
        else if (!Matches(_raphael.Provenance, craft))
            availability.Add(new("RaphaelCache", "ContextMismatch", "Cached recipe/stats/unlock context differs from strict native capture."));
        else if (targetOrdinal < 0 || targetOrdinal > Ordinal || targetOrdinal >= _raphael.Actions.Count
            || _raphaelDivergedAt is { } diverged && diverged <= targetOrdinal)
            availability.Add(new("RaphaelCache", "Unaligned", "Correlated actions diverged from seed prefix or seed exhausted."));
        else
        {
            candidates.Add(new(_raphael, (int)targetOrdinal, 0));
            availability.Add(new("RaphaelCache", "AlignedCandidate", "Requires exact-root replay; cache starting quality is provenance only.", (int)targetOrdinal));
        }
        if (_adaptive == null) availability.Add(new("AdaptiveContinuation", "Unavailable", "No aligned prior validated Adaptive result."));
        else if (TryCursor(_adaptive, _adaptiveOrdinal, targetOrdinal, out var cursor, out var reason))
        {
            candidates.Add(new(_adaptive, cursor, _adaptiveRevision));
            availability.Add(new("AdaptiveContinuation", "AlignedCandidate", "Requires exact-root replay.", cursor));
        }
        else availability.Add(new("AdaptiveContinuation", "Unaligned", reason));
        return new(Array.AsReadOnly(candidates.ToArray()), Array.AsReadOnly(availability.ToArray()));
    }

    private bool TryCursor(AdaptivePlanSeed seed, long origin, long target, out int cursor, out string reason)
    {
        cursor = 0;
        reason = "Source is newer than target root, prefix diverged/exhausted, or required journal history evicted.";
        if (target < origin || target > Ordinal || target - origin >= seed.Actions.Count) return false;
        for (var ordinal = origin + 1; ordinal <= target; ++ordinal)
        {
            var index = _journal.Count == 0 ? -1 : ordinal - _journal[0].Ordinal;
            if (index < 0 || index >= _journal.Count || _journal[(int)index].Action != seed.Actions[cursor]) return false;
            ++cursor;
        }
        return true;
    }

    private static bool Matches(AdaptiveSeedProvenance source, AdaptiveCraftSnapshot craft)
        => source.RecipeId == craft.RecipeId && source.Level == craft.Level && source.Craftsmanship == craft.Craftsmanship
            && source.Control == craft.Control && source.MaxCP == craft.MaxCP && source.Manipulation == craft.ManipulationUnlocked;
}
