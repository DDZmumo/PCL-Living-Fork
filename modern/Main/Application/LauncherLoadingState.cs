// SPDX-License-Identifier: MPL-2.0

namespace PCL;

public enum LauncherLoadingState
{
    Unloaded = -1,
    Run = 0,
    Stop = 1,
    Error = 2
}

public interface ILoadingTrigger
{
    bool IsLoader { get; }
    LauncherLoadingState LoadingState { get; set; }
    event LoadingStateChangedEventHandler LoadingStateChanged;

    delegate void LoadingStateChangedEventHandler(LauncherLoadingState NewState, LauncherLoadingState OldState);
    event ProgressChangedEventHandler ProgressChanged;

    delegate void ProgressChangedEventHandler(double NewProgress, double OldProgress);
}
