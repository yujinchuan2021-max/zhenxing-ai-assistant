using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>Local inventory and initiated tasks only. A cloud listing is not an installed application.</summary>
internal static class AppCenterInventoryPolicy
{
    internal static bool ShouldIncludeCloudTool(CloudToolState state, bool hasLocalEntry)
        => hasLocalEntry || state.IsManaged || state.IsBusy || state.PendingUpdate
            || state.Status == CloudToolStatus.PendingUpdate
            || (state.HasOperationActivity && state.Status is CloudToolStatus.Failed or CloudToolStatus.Unsupported);
}
