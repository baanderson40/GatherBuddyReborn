namespace GatherBuddy.Crafting;

/// <summary>Adaptive selection authorizes execution; readiness and macro safety still gate startup.</summary>
internal static class AdaptiveSolverSelection
{
    internal static VulcanSolverMode ResolveMode(VulcanSolverMode global, SolverOverrideMode recipeOverride)
        => recipeOverride switch
        {
            SolverOverrideMode.StandardSolver => VulcanSolverMode.StandardSolver,
            SolverOverrideMode.RaphaelSolver => VulcanSolverMode.PureRaphael,
            SolverOverrideMode.ProgressOnlySolver => VulcanSolverMode.ProgressOnly,
            SolverOverrideMode.AdaptiveSolver => VulcanSolverMode.Adaptive,
            _ => global,
        };

    internal static bool CanStart(VulcanSolverMode mode, bool quickSynthesis,
        bool ownsCraft, bool hasMacro, out string reason)
    {
        reason = "";
        if (quickSynthesis || mode != VulcanSolverMode.Adaptive) return true;
        if (ownsCraft)
            reason = "Adaptive is preparing or owns a craft; finish or cancel it before starting another.";
        else if (hasMacro)
            reason = "Adaptive cannot override a selected or recipe-associated macro without a declared goal. Use a legacy solver for that macro, or remove the association.";
        return reason.Length == 0;
    }
}
