using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;

namespace Flow.Launcher.Plugin.VoiceMeeter;

internal sealed class VoiceMeeterRemoteClient : IDisposable
{
    private const string DllName = "VoicemeeterRemote64.dll";
    private static readonly string[] DllCandidates =
    [
        @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll",
        @"C:\Program Files\VB\Voicemeeter\VoicemeeterRemote64.dll"
    ];
    private bool _loggedIn;

    static VoiceMeeterRemoteClient()
    {
        NativeLibrary.SetDllImportResolver(typeof(VoiceMeeterRemoteClient).Assembly, ResolveDll);
    }

    private static IntPtr ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in DllCandidates)
        {
            if (File.Exists(candidate))
            {
                return NativeLibrary.Load(candidate);
            }
        }

        throw new DllNotFoundException($"Não foi possível localizar {DllName} no diretório do VoiceMeeter.");
    }

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_Login();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_Logout();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_IsParametersDirty();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_GetParameterFloat(
        [MarshalAs(UnmanagedType.LPStr)] string parameterName,
        out float value);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_SetParameterFloat(
        [MarshalAs(UnmanagedType.LPStr)] string parameterName,
        float value);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int VBVMR_GetParameterStringA(
        [MarshalAs(UnmanagedType.LPStr)] string parameterName,
        StringBuilder value);

    public void Connect()
    {
        if (_loggedIn)
        {
            return;
        }

        var result = VBVMR_Login();
        if (result < 0)
        {
            throw new InvalidOperationException($"VoiceMeeter Remote API login failed ({result}).");
        }

        _loggedIn = true;
        VBVMR_IsParametersDirty();
    }

    public bool TryGetFloat(string parameterName, out float value)
    {
        EnsureConnected();
        VBVMR_IsParametersDirty();
        return VBVMR_GetParameterFloat(parameterName, out value) >= 0;
    }

    public void SetFloat(string parameterName, float value)
    {
        EnsureConnected();
        var result = VBVMR_SetParameterFloat(parameterName, value);
        if (result < 0)
        {
            throw new InvalidOperationException($"VoiceMeeter rejected {parameterName} ({result}).");
        }

        VBVMR_IsParametersDirty();
    }

    public bool TryGetString(string parameterName, out string value)
    {
        EnsureConnected();
        VBVMR_IsParametersDirty();
        var buffer = new StringBuilder(512);
        var result = VBVMR_GetParameterStringA(parameterName, buffer);
        value = buffer.ToString();
        return result >= 0;
    }

    public void RestartAudioEngine()
    {
        SetFloat("Command.Restart", 1);
    }

    public void SetMute(int stripIndex, bool muted)
    {
        SetFloat($"Strip[{stripIndex}].Mute", muted ? 1 : 0);
    }

    public void ChangeStripGain(int stripIndex, float deltaDb)
    {
        if (!TryGetFloat($"Strip[{stripIndex}].Gain", out var currentGain))
        {
            throw new InvalidOperationException($"Unable to read strip {stripIndex} gain.");
        }

        SetStripGain(stripIndex, currentGain + deltaDb);
    }

    public void SetStripGain(int stripIndex, float gainDb)
    {
        SetFloat($"Strip[{stripIndex}].Gain", Math.Clamp(gainDb, -60f, 12f));
    }

    public void ChangeBusGain(int busIndex, float deltaDb)
    {
        if (!TryGetFloat($"Bus[{busIndex}].Gain", out var currentGain))
        {
            throw new InvalidOperationException($"Unable to read bus {busIndex} gain.");
        }

        SetFloat($"Bus[{busIndex}].Gain", Math.Clamp(currentGain + deltaDb, -60f, 12f));
    }

    private void EnsureConnected()
    {
        if (!_loggedIn)
        {
            Connect();
        }
    }

    public void Dispose()
    {
        if (!_loggedIn)
        {
            return;
        }

        VBVMR_Logout();
        _loggedIn = false;
    }
}
