using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GatherBuddy.Gui;

internal sealed class VulcanDebugWindow : Window
{
    internal const string WindowId = "Vulcan Debug###VulcanDebugWindow";
    private readonly Action _drawPanels;
    private bool _requestFocus;
    private bool _wasFocused;

    public VulcanDebugWindow(Action drawPanels) : base(WindowId)
    {
        _drawPanels = drawPanels;
        IsOpen = false;
        Size = VulcanUiScaling.Scaled(900f, 600f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = VulcanUiScaling.Scaled(500f, 300f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Flags |= ImGuiWindowFlags.NoScrollbar;
    }

    public void OpenOrRestore()
    {
        IsOpen = true;
        _requestFocus = true;
    }

    public override void PreDraw()
    {
        if (!_requestFocus) return;
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
        ImGui.SetNextWindowFocus();
        _requestFocus = false;
    }

    public override void Draw()
    {
        using var theme = VulcanUiStyle.PushTheme();
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (focused) GatherBuddy.ControllerSupport?.UpdateFocusedWindow(WindowId);
        else if (_wasFocused) GatherBuddy.ControllerSupport?.UpdateFocusedWindow(null);
        _wasFocused = focused;
        _drawPanels();
        GatherBuddy.ControllerSupport?.UpdateEndOfFrame();
    }

    public override void OnClose()
    {
        if (_wasFocused) GatherBuddy.ControllerSupport?.UpdateFocusedWindow(null);
        _wasFocused = false;
    }
}
