namespace VSDirector.Runtime;

/// <summary>
/// Enters a lock object read from a game field by reflection. Optimum retypes several of
/// the game's lock fields (for example <c>ClientMain.dirtyChunksLock</c>) to
/// <see cref="System.Threading.Lock"/>. A plain C# <c>lock (object)</c> on such an instance
/// takes its Monitor, which is a different lock from the one the game enters with
/// <c>Lock.EnterScope()</c>, so the guarded collection is no longer protected.
/// </summary>
internal static class DirectorGameLock
{
    internal static Scope Enter(object gate) => new(gate);

    internal readonly ref struct Scope
    {
        private readonly object? monitor;
        private readonly Lock.Scope lockScope;

        internal Scope(object gate)
        {
            ArgumentNullException.ThrowIfNull(gate);
            if (gate is Lock typed)
            {
                monitor = null;
                lockScope = typed.EnterScope();
            }
            else
            {
                monitor = gate;
                lockScope = default;
                Monitor.Enter(gate);
            }
        }

        public void Dispose()
        {
            if (monitor is not null)
            {
                Monitor.Exit(monitor);
            }
            else
            {
                lockScope.Dispose();
            }
        }
    }
}
