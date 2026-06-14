namespace TikTokArchive.Web.Services;

public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class Toast
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Message { get; init; }
    public ToastSeverity Severity { get; init; } = ToastSeverity.Info;
}

/// <summary>
/// Lightweight per-circuit toast notifications, replacing MudBlazor's ISnackbar.
/// Components render the <see cref="Toasts"/> collection and subscribe to
/// <see cref="OnChange"/>; the ToastHost component owns auto-dismissal.
/// </summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = new();

    public IReadOnlyList<Toast> Toasts => _toasts;

    public event Action? OnChange;

    public void Show(string message, ToastSeverity severity = ToastSeverity.Info)
    {
        _toasts.Add(new Toast { Message = message, Severity = severity });
        OnChange?.Invoke();
    }

    public void Remove(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0)
        {
            OnChange?.Invoke();
        }
    }
}
