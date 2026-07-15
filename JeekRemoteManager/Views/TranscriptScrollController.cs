namespace JeekRemoteManager.Views;

/// <summary>Owns the transcript's two navigation states without knowing anything about
/// Markdown, message controls, or layout implementation.</summary>
public sealed class TranscriptScrollController
{
    public bool IsFollowingLatest { get; private set; } = true;

    public void FollowLatest() => IsFollowingLatest = true;

    public void BeginManualNavigation() => IsFollowingLatest = false;

    public void CompleteManualNavigation(bool isAtBottom) => IsFollowingLatest = isAtBottom;

    public bool ShouldFollowLayoutChange(double extentDeltaY, double viewportDeltaY) =>
        IsFollowingLatest && (extentDeltaY > 0.5 || viewportDeltaY < -0.5);
}
