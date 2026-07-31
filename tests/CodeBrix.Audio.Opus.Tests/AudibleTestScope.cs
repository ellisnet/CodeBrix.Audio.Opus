using System;
using System.Threading;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Serialises the tests that actually make a sound, so only one is ever heard at a time.
/// </summary>
/// <remarks>
/// The mutex name is shared with CodeBrix.Audio's own audible tests deliberately. Those suites run
/// as SEPARATE PROCESSES, and if this package's tests are run alongside them the two would play
/// over each other - which turns the motif into mush. A named mutex is the only thing that reaches
/// across processes. It also leaves a short gap on the way out so two runs in a row stay distinct
/// rather than running together.
/// </remarks>
internal sealed class AudibleTestScope : IDisposable
{
    // Plain name, no "Global\" prefix: that prefix is a Windows concept and is not valid in a
    // mutex name on Unix.
    private const string MutexName = "CodeBrix.Audio.AudibleTests";

    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TrailingSilence = TimeSpan.FromMilliseconds(450);

    private readonly Mutex mutex;
    private readonly bool held;

    /// <summary>Waits until no other audible test is sounding, then takes the floor.</summary>
    public AudibleTestScope()
    {
        try
        {
            mutex = new Mutex(false, MutexName);
            held = mutex.WaitOne(AcquireTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A previous run died holding it; we now own it.
            held = true;
        }
        catch (Exception)
        {
            // Named mutexes are unavailable on this host. Tests still run; they may overlap.
            mutex = null;
            held = false;
        }
    }

    /// <summary>Leaves a moment of silence, then lets the next audible test proceed.</summary>
    public void Dispose()
    {
        Thread.Sleep(TrailingSilence);

        if (mutex == null) return;

        if (held)
        {
            try { mutex.ReleaseMutex(); } catch (Exception) { /* best effort */ }
        }

        mutex.Dispose();
    }
}
