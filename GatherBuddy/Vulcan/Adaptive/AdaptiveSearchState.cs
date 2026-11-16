using System;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Value equality is the state key. First-step and combo identity must not be lost.</summary>
public readonly record struct AdaptiveSearchState(
    bool FirstStep, int Progress, int Quality, int Durability, int CP, Condition Condition,
    int InnerQuiet = 0, int WasteNot = 0, int Manipulation = 0, int GreatStrides = 0,
    int Innovation = 0, int Veneration = 0, int MuscleMemory = 0, int FinalAppraisal = 0,
    bool TrainedPerfectionAvailable = false, bool TrainedPerfectionActive = false,
    VulcanSkill Combo = VulcanSkill.None)
{
    public static AdaptiveSearchState Capture(StepState step, bool? standardTouchComboValid = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Index < 1 || step.MaterialMiracleActive || step.MaterialMiracleCharges != 0
            || step.HeartAndSoulActive || step.ExpedienceLeft != 0 || step.QuickInnoLeft != 0 || step.QuickInnoAvailable)
            throw new NotSupportedException("Live state contains mechanics outside the Adaptive prototype.");
        if (step.PrevComboAction == VulcanSkill.StandardTouch && !standardTouchComboValid.HasValue)
            throw new NotSupportedException("Standard Touch requires confirmed Basic→Standard combo history, not only the last action.");
        return new(step.Index == 1, step.Progress, step.Quality, step.Durability, step.RemainingCP, step.Condition,
            step.IQStacks, step.WasteNotLeft, step.ManipulationLeft, step.GreatStridesLeft,
            step.InnovationLeft, step.VenerationLeft, step.MuscleMemoryLeft, step.FinalAppraisalLeft,
            step.TrainedPerfectionAvailable, step.TrainedPerfectionActive,
            step.PrevComboAction == VulcanSkill.StandardTouch && standardTouchComboValid == false ? VulcanSkill.None : step.PrevComboAction);
    }

    internal void Validate(AdaptiveCraftSnapshot craft)
    {
        if (Progress < 0 || Quality < 0 || Quality > craft.MaxQuality || Durability > craft.MaxDurability
            || CP < 0 || CP > craft.MaxCP || Condition is < Condition.Normal or > Condition.Poor
            || InnerQuiet is < 0 or > 10 || WasteNot is < 0 or > 8 || Manipulation is < 0 or > 8
            || GreatStrides is < 0 or > 3 || Innovation is < 0 or > 4 || Veneration is < 0 or > 4
            || MuscleMemory is < 0 or > 5 || FinalAppraisal is < 0 or > 5
            || !Enum.IsDefined(Combo) || Combo is VulcanSkill.TouchCombo or VulcanSkill.TouchComboRefined)
            throw new ArgumentException("Invalid or unsupported Adaptive step state.");
    }

    internal StepState ToSimulatorStep()
    {
        var step = new StepState();
        CopyTo(step);
        return step;
    }

    internal void CopyTo(StepState step)
    {
        step.Index = FirstStep ? 1 : 2;
        step.Progress = Progress;
        step.Quality = Quality;
        step.Durability = Durability;
        step.RemainingCP = CP;
        step.Condition = Condition;
        step.IQStacks = InnerQuiet;
        step.WasteNotLeft = WasteNot;
        step.ManipulationLeft = Manipulation;
        step.GreatStridesLeft = GreatStrides;
        step.InnovationLeft = Innovation;
        step.VenerationLeft = Veneration;
        step.MuscleMemoryLeft = MuscleMemory;
        step.FinalAppraisalLeft = FinalAppraisal;
        step.TrainedPerfectionAvailable = TrainedPerfectionAvailable;
        step.TrainedPerfectionActive = TrainedPerfectionActive;
        step.PrevComboAction = Combo;
        step.CarefulObservationLeft = step.ExpedienceLeft = step.QuickInnoLeft = step.ObserveCounter = 0;
        step.HeartAndSoulActive = step.HeartAndSoulAvailable = step.PrevActionFailed = step.QuickInnoAvailable = false;
        step.MaterialMiracleCharges = 0;
        step.MaterialMiracleActive = false;
    }
}
