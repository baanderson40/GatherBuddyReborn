using System;
using System.Runtime.InteropServices;

namespace GatherBuddy.FishTimer.Parser;

/// <summary>Rollback only completed resources; never retry a partially executed native hook factory.</summary>
internal static class HookInitialization
{
    internal static (TFirst First, TSecond Second) CreatePair<TFirst, TSecond>(
        Func<TFirst> firstFactory, Func<TSecond> secondFactory, Action<string, Exception> report)
        where TFirst : IDisposable where TSecond : IDisposable
    {
        var first = firstFactory();
        try { return (first, secondFactory()); }
        catch (Exception ex) when (IsManagedFailure(ex))
        {
            Cleanup(first.Dispose, "first hook constructor rollback", report);
            throw;
        }
    }

    internal static void Cleanup(Action action, string stage, Action<string, Exception> report)
    {
        try { action(); }
        catch (Exception ex) when (IsManagedFailure(ex))
        {
            // A cleanup/logging failure must not hide the primary constructor/enable error
            // or prevent the next independent resource from being released.
            try { report(stage, ex); }
            catch (Exception logError) when (IsManagedFailure(logError)) { }
        }
    }

    internal static bool IsManagedFailure(Exception ex)
        => ex is not (AccessViolationException or SEHException);
}
