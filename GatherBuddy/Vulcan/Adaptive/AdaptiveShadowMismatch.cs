using System;
using System.Collections.Generic;
using System.Globalization;

namespace GatherBuddy.Vulcan.Adaptive;

public sealed record AdaptiveFieldDifference(string Field, string Expected, string Observed);
public sealed record AdaptiveShadowMismatch(int ExpectedStepIndex, int ObservedStepIndex,
    AdaptiveSearchState ExpectedState, AdaptiveSearchState ObservedProjection, IReadOnlyList<AdaptiveFieldDifference> Differences)
{
    internal static AdaptiveShadowMismatch Compare(int expectedIndex, int observedIndex,
        AdaptiveSearchState expected, AdaptiveSearchState observed, bool conditionMatches)
    {
        var differences = new List<AdaptiveFieldDifference>();
        void Add<T>(string name, T wanted, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(wanted, actual))
                differences.Add(new(name, Convert.ToString(wanted, CultureInfo.InvariantCulture) ?? "null",
                    Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "null"));
        }
        Add("StepIndex", expectedIndex, observedIndex);
        Add(nameof(expected.FirstStep), expected.FirstStep, observed.FirstStep);
        Add(nameof(expected.Progress), expected.Progress, observed.Progress);
        Add(nameof(expected.Quality), expected.Quality, observed.Quality);
        Add(nameof(expected.Durability), expected.Durability, observed.Durability);
        Add(nameof(expected.CP), expected.CP, observed.CP);
        if (!conditionMatches) Add(nameof(expected.Condition), expected.Condition, observed.Condition);
        Add(nameof(expected.InnerQuiet), expected.InnerQuiet, observed.InnerQuiet);
        Add(nameof(expected.WasteNot), expected.WasteNot, observed.WasteNot);
        Add(nameof(expected.Manipulation), expected.Manipulation, observed.Manipulation);
        Add(nameof(expected.GreatStrides), expected.GreatStrides, observed.GreatStrides);
        Add(nameof(expected.Innovation), expected.Innovation, observed.Innovation);
        Add(nameof(expected.Veneration), expected.Veneration, observed.Veneration);
        Add(nameof(expected.MuscleMemory), expected.MuscleMemory, observed.MuscleMemory);
        Add(nameof(expected.FinalAppraisal), expected.FinalAppraisal, observed.FinalAppraisal);
        Add(nameof(expected.TrainedPerfectionAvailable), expected.TrainedPerfectionAvailable, observed.TrainedPerfectionAvailable);
        Add(nameof(expected.TrainedPerfectionActive), expected.TrainedPerfectionActive, observed.TrainedPerfectionActive);
        Add(nameof(expected.Combo), expected.Combo, observed.Combo);
        return new(expectedIndex, observedIndex, expected, observed, Array.AsReadOnly(differences.ToArray()));
    }
}
