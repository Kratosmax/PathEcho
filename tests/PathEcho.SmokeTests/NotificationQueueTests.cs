using PathEcho.Core.Notifications;

internal static class NotificationQueueTests
{
    internal static Task RunAsync()
    {
        ExpiresAtTheExactBoundary();
        CoalescesOnlyEqualRequests();
        EvictsByInsertionOrderAfterRenewal();
        SchedulesTheEarliestDeadlineAfterRenewal();
        PrunesBeforeEnqueueing();
        ClearsAndReusesAnEmptyQueue();
        IdleChecksDoNotRequestRendering();
        return Task.CompletedTask;
    }

    private static void ExpiresAtTheExactBoundary()
    {
        var clock = new FakeClock();
        var queue = new BackupNotificationQueue<Request>();
        Assert(queue.NextExpiration is null && !queue.Expire(clock.Now), "Empty queue has no deadline or change.");
        queue.Enqueue(new Request("游戏", true, "自动备份完成 · 12 个文件"), clock.Now);
        Assert(queue.Items.Count == 1 && queue.Items[0].Count == 1, "A new notification starts at one.");
        Assert(queue.NextExpiration == TimeSpan.FromSeconds(5), "Lifetime stays five seconds.");
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert(!queue.Expire(clock.Now), "Notification remains until its exact deadline.");
        clock.Advance(TimeSpan.FromTicks(1));
        Assert(queue.Expire(clock.Now) && queue.Items.Count == 0, "Notification expires at the deadline.");
        Assert(queue.NextExpiration is null && !queue.Expire(clock.Now), "Expiry is reported only once.");
    }

    private static void CoalescesOnlyEqualRequests()
    {
        var clock = new FakeClock();
        var queue = new BackupNotificationQueue<Request>();
        queue.Enqueue(new Request("Game", false, "读取失败"), clock.Now);
        clock.Advance(TimeSpan.FromSeconds(2));
        queue.Enqueue(new Request("Game", false, "读取失败"), clock.Now);
        Assert(queue.Items.Count == 1 && queue.Items[0].Count == 2, "Equal values coalesce across request instances.");
        Assert(queue.Items[0].ExpiresAt == TimeSpan.FromSeconds(7), "Duplicate renews the full lifetime.");
        queue.Enqueue(new Request("Game", true, "读取失败"), clock.Now);
        queue.Enqueue(new Request("Game", false, "写入失败"), clock.Now);
        queue.Enqueue(new Request("game", false, "读取失败"), clock.Now);
        Assert(queue.Items.Count == 4, "Status, detail and case-sensitive name are part of request identity.");
        Assert(queue.Items.Skip(1).All(item => item.Count == 1), "Distinct requests keep independent counts.");
    }

    private static void EvictsByInsertionOrderAfterRenewal()
    {
        var clock = new FakeClock();
        var queue = new BackupNotificationQueue<string>();
        foreach (var request in new[] { "A", "B", "C", "D" })
        {
            queue.Enqueue(request, clock.Now);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue("A", clock.Now);
        Assert(queue.Items.Select(item => item.Request).SequenceEqual(new[] { "A", "B", "C", "D" }), "Renewal does not reorder rows.");
        queue.Enqueue("E", clock.Now);
        Assert(queue.Items.Count == 4, "Queue capacity stays four.");
        Assert(queue.Items.Select(item => item.Request).SequenceEqual(new[] { "B", "C", "D", "E" }), "Overflow evicts the oldest insertion, even after renewal.");
    }

    private static void SchedulesTheEarliestDeadlineAfterRenewal()
    {
        var clock = new FakeClock();
        var queue = new BackupNotificationQueue<string>();
        queue.Enqueue("A", clock.Now);
        clock.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue("B", clock.Now);
        clock.Advance(TimeSpan.FromSeconds(3));
        queue.Enqueue("A", clock.Now);
        Assert(queue.NextExpiration == TimeSpan.FromSeconds(6), "Next wakeup follows B, not the renewed first row.");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert(queue.Expire(clock.Now) && queue.Items.Single().Request == "A", "Only the expired row is removed.");
        Assert(queue.Items[0].Count == 2 && queue.NextExpiration == TimeSpan.FromSeconds(9), "Renewed count and deadline survive another row expiry.");
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert(queue.Expire(clock.Now) && queue.Items.Count == 0 && queue.NextExpiration is null, "A delayed dispatcher wakeup drains all expired rows.");
    }

    private static void PrunesBeforeEnqueueing()
    {
        var clock = new FakeClock();
        var queue = new BackupNotificationQueue<string>();
        queue.Enqueue("A", clock.Now);
        queue.Enqueue("B", clock.Now);
        clock.Advance(TimeSpan.FromSeconds(5));
        queue.Enqueue("A", clock.Now);
        Assert(queue.Items.Count == 1 && queue.Items[0].Count == 1, "A duplicate arriving at expiry begins a new count and removes stale peers.");
        Assert(queue.NextExpiration == TimeSpan.FromSeconds(10), "Reused queue gets a fresh deadline.");
    }

    private static void ClearsAndReusesAnEmptyQueue()
    {
        var clock = new FakeClock();
        var queue = new BackupNotificationQueue<string>();
        queue.Enqueue("old", clock.Now);
        queue.Clear();
        queue.Clear();
        Assert(queue.Items.Count == 0 && queue.NextExpiration is null, "Clearing drops all retained state and is idempotent.");
        clock.Advance(TimeSpan.FromHours(1));
        queue.Enqueue("new", clock.Now);
        Assert(queue.Items.Single().Count == 1 && queue.NextExpiration == clock.Now + TimeSpan.FromSeconds(5), "Long-hidden empty queue can be presented again.");
    }

    private static void IdleChecksDoNotRequestRendering()
    {
        var clock = new FakeClock();
        var queue = new BackupNotificationQueue<string>();
        queue.Enqueue("unchanged", clock.Now);
        var original = queue.Items[0];
        var renderRequests = 1; // Initial presentation.
        for (var tick = 1; tick < 25; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            if (queue.Expire(clock.Now))
            {
                renderRequests++;
            }

            Assert(ReferenceEquals(original, queue.Items[0]), "Idle checks preserve the existing entry.");
            Assert(queue.NextExpiration == TimeSpan.FromSeconds(5), "Idle checks do not move the scheduled deadline.");
        }

        Assert(renderRequests == 1, "All 24 legacy idle tick positions require no rendering.");
        clock.Advance(TimeSpan.FromMilliseconds(200));
        if (queue.Expire(clock.Now))
        {
            renderRequests++; // Clear the visual tree before hiding.
        }

        Assert(renderRequests == 2 && queue.NextExpiration is null, "One expiry change clears the queue and leaves no idle wakeup deadline.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record Request(string GameName, bool Succeeded, string Detail);

    private sealed class FakeClock
    {
        internal TimeSpan Now { get; private set; }
        internal void Advance(TimeSpan elapsed) => Now += elapsed;
    }
}
