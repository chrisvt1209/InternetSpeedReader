using ThroughputMonitor.Core;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent.Commands;

/// <summary>
/// Applies label / category / note to the running session. With no session running the labels wait
/// for the next session to start (for a limited time, so a forgotten label cannot end up on an unrelated session).
/// </summary>
public sealed class SessionMarker(TimeSpan pendingLifetime)
{
    private sealed record Labels(string? Label, string? Category, string? Note);

    private Labels? _pending;
    private DateTime _pendingExpiresUtc;

    public bool HasPending(DateTime nowUtc) => _pending is not null && nowUtc < _pendingExpiresUtc;

    /// <param name="touched">The session that was changed, or null when the labels are waiting for a future session.</param>
    public AgentResponse Mark(AgentRequest request, Session? current, DateTime nowUtc, out Session? touched)
    {
        touched = null;
        var labels = new Labels(
            Clean(request.Label, AgentIpc.MaxLabelChars),
            Clean(request.Category, AgentIpc.MaxCategoryChars),
            Clean(request.Note, AgentIpc.MaxNoteChars));

        if (labels.Label is null && labels.Category is null && labels.Note is null)
            return new AgentResponse(false, "Nothing to mark: give a label, --category or --note.");

        if (current is not null)
        {
            Apply(current, labels);
            touched = current;
            return new AgentResponse(true,
                $"Labelled the running session (started {current.StartUtc.ToLocalTime():HH:mm:ss}): {Summary(current)}");
        }

        // Several marks before a session starts are merged; later values win per field.
        var existing = HasPending(nowUtc) ? _pending! : new Labels(null, null, null);
        _pending = new Labels(labels.Label ?? existing.Label, labels.Category ?? existing.Category, labels.Note ?? existing.Note);
        _pendingExpiresUtc = nowUtc + pendingLifetime;
        return new AgentResponse(true,
            $"No session is running. These labels will go to the next session that starts within {pendingLifetime.TotalMinutes:F0} minutes.");
    }

    /// <summary>Gives the waiting labels (if still valid) to a session that just started. Returns true when applied.</summary>
    public bool OnSessionStarted(Session session, DateTime nowUtc)
    {
        bool apply = HasPending(nowUtc);
        if (apply) Apply(session, _pending!);
        _pending = null;
        return apply;
    }

    public static string Summary(Session s)
    {
        var parts = new List<string>();
        if (s.Label is not null) parts.Add($"label \"{s.Label}\"");
        if (s.Category is not null) parts.Add($"category \"{s.Category}\"");
        if (s.Note is not null) parts.Add("note set");
        return string.Join(", ", parts);
    }

    private static void Apply(Session s, Labels l)
    {
        if (l.Label is not null) s.Label = l.Label;
        if (l.Category is not null) s.Category = l.Category;
        if (l.Note is not null) s.Note = l.Note;
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }
}
