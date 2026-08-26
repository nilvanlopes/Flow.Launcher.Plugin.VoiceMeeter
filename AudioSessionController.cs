using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace Flow.Launcher.Plugin.VoiceMeeter;

internal sealed record AudioApplication(string ProcessName, string DisplayName, float Volume);

internal sealed class AudioSessionController
{
    public IReadOnlyList<AudioApplication> ListApplications()
    {
        var sessions = new Dictionary<string, AudioApplication>(StringComparer.OrdinalIgnoreCase);
        using var deviceEnumerator = new MMDeviceEnumerator();

        foreach (var device in deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessionsForDevice = device.AudioSessionManager.Sessions;
                for (var sessionIndex = 0; sessionIndex < sessionsForDevice.Count; sessionIndex++)
                {
                    var session = sessionsForDevice[sessionIndex];
                    if (!TryGetProcessName(session, out var processName))
                    {
                        continue;
                    }

                    var displayName = string.IsNullOrWhiteSpace(session.DisplayName)
                        ? processName
                        : session.DisplayName;
                    var volume = session.SimpleAudioVolume.Volume;

                    if (!sessions.ContainsKey(processName))
                    {
                        sessions[processName] = new AudioApplication(processName, displayName, volume);
                    }
                }
            }
            finally
            {
                device.Dispose();
            }
        }

        return sessions.Values.OrderBy(application => application.DisplayName).ToList();
    }

    public int ChangeVolume(string processName, float deltaDb)
    {
        var changed = 0;
        var factor = MathF.Pow(10, deltaDb / 20f);
        using var deviceEnumerator = new MMDeviceEnumerator();

        foreach (var device in deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessionsForDevice = device.AudioSessionManager.Sessions;
                for (var sessionIndex = 0; sessionIndex < sessionsForDevice.Count; sessionIndex++)
                {
                    var session = sessionsForDevice[sessionIndex];
                    if (!TryGetProcessName(session, out var currentProcessName) ||
                        !string.Equals(currentProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var currentVolume = session.SimpleAudioVolume.Volume;
                    session.SimpleAudioVolume.Volume = Math.Clamp(currentVolume * factor, 0f, 1f);
                    changed++;
                }
            }
            finally
            {
                device.Dispose();
            }
        }

        return changed;
    }

    private static bool TryGetProcessName(AudioSessionControl session, out string processName)
    {
        processName = string.Empty;
        var processId = session.GetProcessID;
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            return !string.IsNullOrWhiteSpace(processName);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
