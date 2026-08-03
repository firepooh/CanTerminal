using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CanTerminal.App;

/// <summary>
/// The trace list, pruned out of the UI Automation tree.
///
/// WPF rebuilds and diffs an automation peer subtree after every layout pass whenever any UIA
/// client on the machine is listening — and something usually is: assistive tools, remote
/// control and screen sharing, Task Manager, IDE and test automation all register as one. The
/// trace list replaces most of its viewport ten times a second, so that diff runs against a
/// fresh set of rows every tick. Measured on a maximised 4K window it was 62-86% of the UI
/// thread, dwarfing the layout and rendering it was nested inside.
///
/// Returning null from OnCreateAutomationPeer does NOT achieve this: "no peer" means "skip me
/// and look at my children", so every row and cell below still gets one. The subtree is only
/// cut off by supplying a peer that reports no children.
///
/// Nothing is lost. A list that scrolls a thousand rows a second cannot be read through a
/// screen reader; the rest of the window (menus, toolbar, status bar, the aggregate Fixed view)
/// keeps its peers and stays accessible.
/// </summary>
public sealed class TraceListView : ListView
{
    protected override AutomationPeer OnCreateAutomationPeer() => new LeafPeer(this);

    private sealed class LeafPeer : FrameworkElementAutomationPeer
    {
        public LeafPeer(FrameworkElement owner) : base(owner) { }

        /// <summary>The whole point: no children means the walk stops here.</summary>
        protected override List<AutomationPeer> GetChildrenCore() => [];

        protected override string GetClassNameCore() => "TraceList";

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Custom;

        protected override string GetNameCore() => "CAN trace (not exposed row by row)";
    }
}
