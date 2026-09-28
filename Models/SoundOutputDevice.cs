using System;

namespace Foot_Tracker.Models;

/// <summary>
/// §389. One place the alert sounds can be sent - a headset, the speakers,
/// an HDMI screen - as the Sound Settings window lists them and as the
/// preference file keeps the chosen one.
///
/// <see cref="Backend"/> names the family the <see cref="Id"/> belongs to,
/// because the id means nothing outside it: a PulseAudio / PipeWire sink
/// name (<c>alsa_output.usb-Logitech...analog-stereo</c>) is what paplay's
/// <c>--device</c> and pw-play's <c>--target</c> want, a raw ALSA PCM
/// (<c>plughw:CARD=Headset,DEV=0</c>) is what aplay's <c>-D</c> wants, and
/// a winmm product name is what Windows' waveOut devices are found by. The
/// preference file stores <see cref="Pin"/>, backend and id in one string,
/// beside a label for the window to show while the device is unplugged.
///
/// Names rather than numbers on every platform: PulseAudio's sink numbers
/// and winmm's device numbers both shuffle when something is plugged in or
/// unplugged, and the whole point is a choice that survives that.
/// </summary>
public sealed record SoundOutputDevice(string Backend, string Id, string Label)
{
    public const string PulseBackend = "pulse";
    public const string AlsaBackend = "alsa";
    public const string WindowsBackend = "winmm";

    /// <summary>The "no pin" choice: whatever the operating system sends
    /// sound to. An empty backend, so it never matches a real device.</summary>
    public static readonly SoundOutputDevice SystemDefault = new(string.Empty, string.Empty, "System default");

    /// <summary>Windows only: the winmm device number the device had when it
    /// was listed - what waveOutOpen takes. -1 for a saved pin (numbers are
    /// not saved) and on every other platform. Re-listed at play time, so a
    /// stale number is never opened.</summary>
    public int Index { get; init; } = -1;

    public bool IsSystemDefault => Backend.Length == 0;

    /// <summary>What the preference file stores: "pulse:alsa_output...",
    /// "alsa:plughw:CARD=...", "winmm:Headset Earphone (Logitech...". Empty
    /// for the system default. Split at the FIRST colon when read back -
    /// an ALSA id has colons of its own.</summary>
    public string Pin => IsSystemDefault ? string.Empty : $"{Backend}:{Id}";

    /// <summary>The same device, whatever its label or number today.</summary>
    public bool Matches(SoundOutputDevice other) =>
        string.Equals(Backend, other.Backend, StringComparison.Ordinal)
        && string.Equals(Id, other.Id, StringComparison.Ordinal);

    /// <summary>A saved pin back into a device; null for an empty or
    /// malformed one, which plays on the system default.</summary>
    public static SoundOutputDevice? Parse(string? pin, string? label)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return null;

        int colon = pin.IndexOf(':');

        if (colon <= 0 || colon == pin.Length - 1)
            return null;

        string backend = pin[..colon];
        string id = pin[(colon + 1)..];

        return new SoundOutputDevice(backend, id, string.IsNullOrWhiteSpace(label) ? id : label.Trim());
    }

    // The ComboBox in Sound Settings shows the record itself.
    public override string ToString() => Label;
}
