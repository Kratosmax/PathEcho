using System.Collections.ObjectModel;

namespace PathEcho.Core.Notifications;

/// <summary>Bounded notification state; callers supply monotonic elapsed time on one thread.</summary>
public sealed class BackupNotificationQueue<T> where T : notnull
{
    public const int Capacity = 4;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly List<BackupNotificationEntry<T>> _items = [];
    private readonly ReadOnlyCollection<BackupNotificationEntry<T>> _view;

    public BackupNotificationQueue() => _view = _items.AsReadOnly();

    public IReadOnlyList<BackupNotificationEntry<T>> Items => _view;

    // A duplicate renews its deadline in place, so the first row need not expire first.
    public TimeSpan? NextExpiration
    {
        get
        {
            TimeSpan? next = null;
            foreach (var item in _items)
            {
                if (next is null || item.ExpiresAt < next.Value)
                {
                    next = item.ExpiresAt;
                }
            }

            return next;
        }
    }

    public void Enqueue(T request, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(request);
        Expire(now);
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (EqualityComparer<T>.Default.Equals(item.Request, request))
            {
                _items[index] = item with { Count = item.Count + 1, ExpiresAt = now + Lifetime };
                return;
            }
        }

        if (_items.Count == Capacity)
        {
            _items.RemoveAt(0);
        }

        _items.Add(new BackupNotificationEntry<T>(request, 1, now + Lifetime));
    }

    /// <returns>True only when visible queue contents changed.</returns>
    public bool Expire(TimeSpan now)
    {
        var changed = false;
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            if (_items[index].ExpiresAt <= now)
            {
                _items.RemoveAt(index);
                changed = true;
            }
        }

        return changed;
    }

    public void Clear() => _items.Clear();
}

public sealed record BackupNotificationEntry<T>(T Request, int Count, TimeSpan ExpiresAt);
