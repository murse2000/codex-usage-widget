using CodexUsageWidget;

internal static class ControlEventVerification
{
    internal static bool HasDragHandler(Control control)
        => control is IDragSource source && source.HasDragHandler;
}
