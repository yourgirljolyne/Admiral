namespace Admiral;

/// <summary>
/// One file operation at a time inside Admiral. The keyboard's file buffers are global: two of our parts
/// reading or writing at once (the card, a screen switch, loading the keys) would trample each other's buffer 0.
/// Property reads and writes don't use a buffer and don't need the gate.
/// </summary>
internal static class DeviceGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await Gate.WaitAsync();
        try { return await action(); }
        finally { Gate.Release(); }
    }

    public static Task RunAsync(Func<Task> action) => RunAsync(async () => { await action(); return true; });
}
