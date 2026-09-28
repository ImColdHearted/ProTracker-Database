using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// §389. Which output devices this machine has right now, for the Sound
/// Settings picker and for SoundNotificationService to check a saved pin
/// against before an alert.
///
/// Windows asks winmm (waveOutGetNumDevs / waveOutGetDevCapsW - the same
/// device list waveOut plays through, so a name found here can be opened).
/// Linux asks the desktop's sound server first - <c>pactl list sinks</c>
/// (PulseAudio, and PipeWire's Pulse layer), then <c>pw-dump</c> (PipeWire
/// without pulseaudio-utils) - and only with neither answering falls back to
/// the raw ALSA cards from <c>aplay -L</c>, because a card opened directly
/// while a server owns it is the wrong device or a busy one. Each tool gets
/// three seconds and a C locale, so the labels parsed are the ones printed.
/// macOS lists nothing: afplay has no device option, and the Mac's own
/// Sound settings choose the output.
///
/// Every failure here is an empty list and a log line, never an exception:
/// a device list is a convenience, and the tracker was hunting without one
/// for a year.
/// </summary>
internal static class SoundOutputDevices
{
    private const int ListingTimeoutMs = 3000;

    /// <summary>Whether the Sound Settings window offers the picker at all.</summary>
    internal static bool CanPickOnThisPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>What an empty list means, for the window's note.</summary>
    internal static string ListingHint => OperatingSystem.IsLinux()
        ? "pactl (pulseaudio-utils), pw-dump (pipewire) or aplay (alsa-utils) is needed to list them"
        : "Windows reports no output device";

    internal static IReadOnlyList<SoundOutputDevice> List()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ListWindows();

            if (OperatingSystem.IsLinux())
                return ListLinux();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sound output devices could not be listed.");
        }

        return Array.Empty<SoundOutputDevice>();
    }

    // ---------------------------------------------------------------- Windows

    // WAVEOUTCAPSW, as winmm fills it: the product name is a fixed 32-wide
    // field, so a long name arrives cut to 31 characters - "Headset Earphone
    // (2- Logitech G" is what every winmm program shows, and it is the name
    // saved, so it is also the name matched.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetDevCapsW(IntPtr deviceId, out WaveOutCaps caps, uint size);

    private static IReadOnlyList<SoundOutputDevice> ListWindows()
    {
        var devices = new List<SoundOutputDevice>();
        uint count = waveOutGetNumDevs();
        uint size = (uint)Marshal.SizeOf<WaveOutCaps>();

        for (uint i = 0; i < count; i++)
        {
            if (waveOutGetDevCapsW(new IntPtr((long)i), out WaveOutCaps caps, size) != 0)
                continue;

            string name = (caps.ProductName ?? string.Empty).Trim();

            // Two devices cut to the same 31 characters cannot be told apart
            // by name; the first keeps it, the way a saved pin will find it.
            if (name.Length == 0 || devices.Exists(d => string.Equals(d.Id, name, StringComparison.Ordinal)))
                continue;

            devices.Add(new SoundOutputDevice(SoundOutputDevice.WindowsBackend, name, name) { Index = (int)i });
        }

        return devices;
    }

    // ------------------------------------------------------------------ Linux

    private static IReadOnlyList<SoundOutputDevice> ListLinux()
    {
        List<SoundOutputDevice> devices = ListPulse();

        if (devices.Count > 0)
            return devices;

        devices = ListPipeWire();

        if (devices.Count > 0)
            return devices;

        return ListAlsa();
    }

    /// <summary>pactl list sinks: blocks of "Sink #N" with "Name:" and
    /// "Description:" lines under them. The name is the id every Pulse
    /// client accepts (paplay --device, PULSE_SINK, mpv's pulse/ prefix),
    /// and under PipeWire it is the node name pw-play's --target takes too.</summary>
    private static List<SoundOutputDevice> ListPulse()
    {
        var devices = new List<SoundOutputDevice>();

        if (!Run("pactl", new[] { "list", "sinks" }, out string output))
            return devices;

        // One block per sink, cut at the lines that start one.
        foreach (string block in Regex.Split(output, @"(?m)^Sink #"))
        {
            string? name = null;
            string? description = null;

            foreach (string raw in block.Split('\n'))
            {
                string line = raw.Trim();

                if (line.StartsWith("Name: ", StringComparison.Ordinal))
                    name ??= line["Name: ".Length..].Trim();
                else if (line.StartsWith("Description: ", StringComparison.Ordinal))
                    description ??= line["Description: ".Length..].Trim();
            }

            if (!string.IsNullOrEmpty(name))
                devices.Add(new SoundOutputDevice(SoundOutputDevice.PulseBackend, name, string.IsNullOrEmpty(description) ? name : description));
        }

        return devices;
    }

    /// <summary>pw-dump: one JSON array of every PipeWire object. The sinks
    /// are the nodes whose media.class is Audio/Sink; node.name is the id
    /// and node.description the label. Objects the user may not inspect
    /// come with "info": null, hence the kind checks before every lookup.</summary>
    private static List<SoundOutputDevice> ListPipeWire()
    {
        var devices = new List<SoundOutputDevice>();

        if (!Run("pw-dump", Array.Empty<string>(), out string output))
            return devices;

        using JsonDocument document = JsonDocument.Parse(output);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return devices;

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (!item.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "PipeWire:Interface:Node")
                continue;

            if (!item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object)
                continue;

            if (!info.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object)
                continue;

            if (!props.TryGetProperty("media.class", out JsonElement mediaClass) || mediaClass.ValueKind != JsonValueKind.String
                || mediaClass.GetString() != "Audio/Sink")
                continue;

            string? name = Text(props, "node.name");

            if (string.IsNullOrEmpty(name))
                continue;

            string label = Text(props, "node.description") ?? Text(props, "node.nick") ?? name;
            devices.Add(new SoundOutputDevice(SoundOutputDevice.PulseBackend, name, label));
        }

        return devices;
    }

    private static string? Text(JsonElement props, string key) =>
        props.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>aplay -L: a PCM name on each unindented line, its description
    /// indented under it. Only the plughw: entries are offered - one per
    /// card and device, with ALSA's own format conversion, so a 44.1 kHz
    /// clip plays on a card that only does 48. (hw: would refuse it; the
    /// dmix/surround/iec958 variants are the same cards again.)</summary>
    private static List<SoundOutputDevice> ListAlsa()
    {
        var devices = new List<SoundOutputDevice>();

        if (!Run("aplay", new[] { "-L" }, out string output))
            return devices;

        // One block per PCM, cut at the lines that start in column one.
        foreach (string block in Regex.Split(output, @"(?m)^(?=\S)"))
        {
            string[] lines = block.Split('\n');
            string name = lines[0].Trim();

            if (!name.StartsWith("plughw:", StringComparison.Ordinal))
                continue;

            string? description = null;

            for (int i = 1; i < lines.Length && description is null; i++)
            {
                string line = lines[i].Trim();

                if (line.Length > 0)
                    description = line;
            }

            devices.Add(new SoundOutputDevice(SoundOutputDevice.AlsaBackend, name, description is null ? name : $"{description}  [{name}]"));
        }

        return devices;
    }

    /// <summary>Runs a listing tool from the PATH and returns its stdout;
    /// false when it is absent, fails, or takes longer than the limit. Both
    /// pipes are read from the start so a chatty tool (pw-dump is hundreds
    /// of kilobytes) cannot fill one and stall.</summary>
    private static bool Run(string executable, string[] arguments, out string output)
    {
        output = string.Empty;
        string? path = SoundNotificationService.FindOnPath(executable);

        if (path is null)
            return false;

        try
        {
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            // pactl translates its labels; the parser above reads English.
            startInfo.Environment["LC_ALL"] = "C";

            using Process? process = Process.Start(startInfo);

            if (process is null)
                return false;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ListingTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone; nothing else to do about it.
                }

                Log.Information("Sound output devices: {Tool} did not answer within {Ms}ms - not used.", executable, ListingTimeoutMs);
                return false;
            }

            process.WaitForExit();
            output = stdout.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                Log.Information(
                    "Sound output devices: {Tool} exited with code {Code} - not used{Detail}.",
                    executable, process.ExitCode, Detail(stderr.GetAwaiter().GetResult()));

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Sound output devices: {Tool} could not be run - not used.", executable);
            return false;
        }
    }

    private static string Detail(string stderr)
    {
        string text = stderr.Trim();

        if (text.Length == 0)
            return string.Empty;

        if (text.Length > 200)
            text = text[..200] + "...";

        return " (" + text + ")";
    }
}
