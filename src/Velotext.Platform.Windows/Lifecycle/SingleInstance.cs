// Velotext: one running instance per user session and data folder; a second start activates the first.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Velotext.Platform.Windows.Lifecycle;

/// <summary>
/// Ensures one Velotext per user session and data folder. A second start signals the first one to show
/// its window, then exits. The name includes a hash of the data folder, so an isolated test instance
/// with its own data folder can run next to the user's normal instance.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly bool _owns;
    private RegisteredWaitHandle? _wait;

    /// <summary>Claims the instance slot for <paramref name="dataDirectory"/>.</summary>
    /// <param name="dataDirectory">The data folder this instance uses.</param>
    public SingleInstance(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..16];
        _mutex = new Mutex(initiallyOwned: true, @"Local\Velotext.Instance." + hash, out _owns);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Velotext.Activate." + hash);
    }

    /// <summary>True when this process is the first instance.</summary>
    public bool IsFirstInstance => _owns;

    /// <summary>Asks the first instance to show its window.</summary>
    public void SignalFirstInstance() => _activate.Set();

    /// <summary>Calls <paramref name="onActivate"/> (on a thread-pool thread) whenever another start asks to activate.</summary>
    /// <param name="onActivate">What to do, for example show the main window.</param>
    public void ListenForActivation(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Releases the slot.</summary>
    public void Dispose()
    {
        _wait?.Unregister(null);
        if (_owns)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from another thread at shutdown; Windows frees it when the process ends.
            }
        }

        _mutex.Dispose();
        _activate.Dispose();
    }
}
