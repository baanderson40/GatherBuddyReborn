using System;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Immutable game-sheet provenance. Ratings are not simulator quality units.</summary>
public sealed record AdaptiveCollectabilityEvidence(uint ItemId, string Source, int LowRating, int MidRating, int HighRating)
{
    public int QualityPerRating => 10;

    public (int Low, int Mid, int High) QualityThresholds(int maximumQuality)
    {
        if (ItemId == 0 || string.IsNullOrWhiteSpace(Source) || Source.Length > 256
            || LowRating <= 0 || MidRating < LowRating || HighRating < MidRating
            || (long)HighRating * QualityPerRating > maximumQuality)
            throw new ArgumentException("Missing, unordered or out-of-range collectability game data.");
        return (checked(LowRating * QualityPerRating), checked(MidRating * QualityPerRating), checked(HighRating * QualityPerRating));
    }
}

/// <summary>Reject ambiguous metadata instead of selecting the first conflicting reward context.</summary>
internal sealed class AdaptiveCollectabilityAccumulator(uint itemId, int maximumQuality)
{
    private int _matches;
    internal AdaptiveCollectabilityEvidence? Result { get; private set; }
    internal void Add(AdaptiveCollectabilityEvidence candidate)
    {
        if (++_matches > 16) throw new NotSupportedException("Too many collectability reward matches; explicit context required.");
        if (candidate.ItemId != itemId) throw new ArgumentException("Collectability result item mismatch.");
        candidate.QualityThresholds(maximumQuality);
        if (Result != null && (Result.LowRating != candidate.LowRating || Result.MidRating != candidate.MidRating || Result.HighRating != candidate.HighRating))
            throw new NotSupportedException("Conflicting collectability reward rows; explicit turn-in context required.");
        Result ??= candidate;
    }
}
