using System.Runtime.Versioning;

namespace OwlMount.Core.Windows.Backends;

/// <summary>
/// Abstracts the VFS backend (WinFsp or ProjFS) so <see cref="Program"/> can drive
/// either without depending on backend-specific types.
/// </summary>
[SupportedOSPlatform("windows")]
public interface IOwlMountBackend : IDisposable
{
    /// <summary>Human-readable name of this backend, e.g. <c>WinFsp</c> or <c>ProjFS</c>.</summary>
    string Name { get; }

    /// <summary>
    /// <c>true</c> when the backing provider is read-only or a read-only flag was forced.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Starts the backend and mounts the virtualized filesystem at
    /// <paramref name="mountPoint"/> (e.g. <c>M:</c>).
    /// The implementation should write any failure details to <see cref="Console.Error"/>
    /// before returning <c>false</c>.
    /// </summary>
    /// <returns><c>true</c> on success; <c>false</c> if mounting failed.</returns>
    bool Start(string mountPoint, string volumeLabel);

    /// <summary>Stops the backend and unmounts.</summary>
    void Stop();

    /// <summary>
    /// Fires when the backend stops on its own initiative (e.g. WinFsp
    /// <c>DispatcherStopped</c> when the user ejects the drive from Explorer).
    /// <para>
    /// Not all backends can detect an external unmount; implementations that cannot
    /// should document this and leave the event permanently unraised.
    /// </para>
    /// </summary>
    event EventHandler? Stopped;
}
