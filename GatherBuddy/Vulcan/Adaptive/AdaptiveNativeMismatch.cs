using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Expected packet projection only; absent request fields remain unknown, never inferred.</summary>
public sealed record AdaptiveNativeExpectation(long Stream, long AfterSequence, uint RecipeId, int StartingQuality,
    uint? ActionId, int? Operation, int? StepIndex, int? Progress, int? Quality, int? Durability,
    IReadOnlyList<int> AllowedConditionPlusOne, string ConditionRule, bool? ActionSucceeded, bool? Complete);

public sealed record AdaptiveNativeMismatch(string Reason, AdaptiveNativeExpectation Expected,
    AdaptiveNativeCraftEvent Observed, IReadOnlyList<AdaptiveFieldDifference> Differences)
{
    internal static AdaptiveNativeMismatch Compare(string reason, AdaptiveNativeExpectation expected,
        AdaptiveNativeCraftEvent observed, string? protocolFailure = null)
    {
        var differences = new List<AdaptiveFieldDifference>();
        void Add<T>(string name, T wanted, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(wanted, actual))
                differences.Add(new(name, Convert.ToString(wanted, CultureInfo.InvariantCulture) ?? "null",
                    Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "null"));
        }
        void Optional<T>(string name, T? wanted, T actual) where T : struct
        {
            if (wanted.HasValue) Add(name, wanted.Value, actual);
        }
        Add("Stream", expected.Stream, observed.Stream);
        if (observed.Sequence <= expected.AfterSequence)
            differences.Add(new("Sequence", $"> {expected.AfterSequence.ToString(CultureInfo.InvariantCulture)}",
                observed.Sequence.ToString(CultureInfo.InvariantCulture)));
        Add("RecipeId", expected.RecipeId, observed.RecipeId);
        Add("StartingQuality", expected.StartingQuality, observed.StartingQuality);
        Optional("ActionId", expected.ActionId, observed.ActionId);
        Optional("Operation", expected.Operation, observed.Operation);
        Optional("StepIndex", expected.StepIndex, observed.StepIndex);
        Optional("Progress", expected.Progress, observed.Progress);
        Optional("Quality", expected.Quality, observed.Quality);
        Optional("Durability", expected.Durability, observed.Durability);
        Optional("ActionSucceeded", expected.ActionSucceeded, observed.ActionSucceeded);
        Optional("Complete", expected.Complete, observed.Complete);
        if (expected.AllowedConditionPlusOne.Count > 0 && !expected.AllowedConditionPlusOne.Contains(observed.ConditionPlusOne))
            differences.Add(new("ConditionPlusOne", string.Join(",", expected.AllowedConditionPlusOne),
                observed.ConditionPlusOne.ToString(CultureInfo.InvariantCulture)));
        if (protocolFailure != null) differences.Add(new("AcknowledgementState", "One active reported request, no prior acknowledgement, not paused", protocolFailure));
        return new(reason, expected, observed, Array.AsReadOnly(differences.ToArray()));
    }
}
