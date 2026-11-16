using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>Opt-in read-only callback tap. Never patches payloads, sends actions, or calls game code except Original.</summary>
internal sealed unsafe class AdaptiveNativeCraftCapture : IDisposable
{
    // Layout/signature provenance and risks: docs/adaptive-native-confirmation.md.
    private delegate void Update(nint self, nint a2, nint a3, nint payload);
    private Hook<Update>? _hook;
    private readonly object _gate = new();
    private readonly AdaptiveNativeEvidenceBuffer _events = new();
    private long _stream, _sequence;
    private uint _recipe;
    private int _startingQuality;
    private bool _prepared, _active, _lost, _advanced, _attached;
    internal string Status { get; private set; } = "Native capture not started.";

    internal bool TryEnable()
    {
        try
        {
            _hook = Dalamud.Interop.HookFromSignature<Update>(
                "48 89 5C 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 30 80 A1", Detour);
            _hook.Enable();
            Status = "Experimental native capture enabled; next fresh StartPrepare/StartInfo required. Layout NOT native-validated.";
            return true;
        }
        catch (Exception error)
        {
            _hook?.Dispose();
            _hook = null;
            Status = $"Native capture unavailable: {error.GetType().Name}; execution remains disabled.";
            return false;
        }
    }

    internal bool TryAttach(uint recipe, int initialQuality, out AdaptiveNativeCaptureContext? context)
    {
        lock (_gate)
        {
            context = null;
            if (_hook == null || !_active || _lost || _advanced || _attached || recipe != _recipe || initialQuality != _startingQuality) return false;
            // No action may have happened before the managed fresh-start attachment.
            if (_events.Count != 0) return false;
            context = new(_stream, _recipe, _startingQuality, _sequence);
            _attached = true;
            return true;
        }
    }

    internal AdaptiveNativeCraftEvent[] Drain(out bool lost)
    {
        lock (_gate)
        {
            var result = _events.Drain(out lost);
            lost |= _lost;
            return result;
        }
    }

    private void Detour(nint self, nint a2, nint a3, nint payload)
    {
        // Read before Original can mutate payload; no pointers escape this call.
        try
        {
            var framework = EventFramework.Instance();
            if (!Dalamud.Framework.IsInFrameworkUpdateThread) { lock (_gate) _lost = true; }
            else if (payload != 0 && framework != null && (nint)framework->GetEventHandlerById(0x000a0001) == self)
            {
                lock (_gate)
                {
                    var op = Marshal.ReadInt32(payload);
                    if (op == 1)
                    {
                        ++_stream;
                        ++_sequence;
                        _lost = !_events.ResetForFreshStream();
                        if (_active) Enqueue(new(_stream, _sequence, op, 0, 0)); // Existing attached session must reject new stream.
                        _active = false;
                        _prepared = true;
                        _recipe = 0;
                        _advanced = false;
                        _attached = false;
                    }
                    else if (op == 2 && _prepared)
                    {
                        _recipe = unchecked((ushort)Marshal.ReadInt16(payload, 4));
                        _startingQuality = Marshal.ReadInt32(payload, 8);
                        _active = _recipe != 0 && _startingQuality >= 0;
                        _prepared = false;
                        ++_sequence;
                    }
                    else if (_active)
                    {
                        ++_sequence;
                        if (op is 9 or 10)
                        {
                            _advanced = true;
                            if (AdaptiveNativeCraftEvent.TryParseAdvance(new ReadOnlySpan<byte>((void*)payload, 0x48),
                                _stream, _sequence, _recipe, _startingQuality, out var evidence)) Enqueue(evidence!);
                            else _lost = true;
                        }
                        else if (op is 4 or 6 or 12)
                        {
                            Enqueue(new(_stream, _sequence, op, _recipe, _startingQuality));
                            _active = false;
                        }
                        else if (op != 3) _lost = true; // Unknown/duplicate start/reagent operations are not guessed safe.
                    }
                }
            }
        }
        catch (Exception) { lock (_gate) _lost = true; }
        // Exactly once, outside capture handling/lock; never substitute native return/control flow.
        _hook!.Original(self, a2, a3, payload);
    }

    private void Enqueue(AdaptiveNativeCraftEvent evidence)
    {
        _events.Add(evidence);
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
        lock (_gate) { _active = _prepared = false; _events.Drain(out _); }
    }
}
