using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.VoiceMeeter;

public sealed class VoiceMeeterPlugin : IPlugin, IContextMenu, IResultUpdated, IDisposable
{
    private const byte VirtualKeyDown = 0x28;
    private const uint KeyEventUp = 0x0002;
    private const float VolumeStepDb = 3f;
    private const string IconPath = "Images\\voicemeeter.png";
    private PluginInitContext? _context;
    private VoiceMeeterRemoteClient? _remote;
    private AudioSessionController? _audioSessions;
    private Query? _activeSourceQuery;

    public event ResultUpdatedEventHandler? ResultsUpdated;

    public void Init(PluginInitContext context)
    {
        _context = context;
        _remote = new VoiceMeeterRemoteClient();
        _audioSessions = new AudioSessionController();
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not int index || index is < 0 or >= 8)
        {
            return new List<Result>();
        }

        var muted = IsStripMuted(index);
        var state = muted ? "mutada" : "ativa";
        return new List<Result>
        {
            ActionResult(
                muted ? "Desmutar source" : "Mutar source",
                $"Estado atual: {state}",
                () => ToggleStripMute(index),
                closeAfterAction: false,
                contextData: index,
                addSelectedCount: false),
            ActionResult(
                "Aumentar volume",
                "+3 dB",
                () => ChangeStripGain(index, VolumeStepDb),
                closeAfterAction: false,
                contextData: index,
                addSelectedCount: false),
            ActionResult(
                "Diminuir volume",
                "-3 dB",
                () => ChangeStripGain(index, -VolumeStepDb),
                closeAfterAction: false,
                contextData: index,
                addSelectedCount: false),
            ActionResult(
                "Resetar volume",
                "Definir ganho em 0 dB",
                () => SetStripGain(index, 0f),
                closeAfterAction: false,
                contextData: index,
                addSelectedCount: false),
            ActionResult(
                "Definir volume",
                "Enter para digitar um valor entre -60 e 12 dB",
                () => NavigateTo($"vm source {index} set "),
                closeAfterAction: false,
                contextData: index,
                addSelectedCount: false)
        };
    }

    public List<Result> Query(Query query)
    {
        var search = query.Search?.Trim() ?? string.Empty;
        try
        {
            EnsureRemote();
            var results = BuildResults(search);
            if (IsSourceListQuery(search))
            {
                _activeSourceQuery = query;
            }

            return results;
        }
        catch (DllNotFoundException)
        {
            return Error("VoiceMeeterRemote64.dll não encontrada", "Verifique se o VoiceMeeter Potato está instalado.");
        }
        catch (Exception exception)
        {
            return Error("VoiceMeeter indisponível", exception.Message);
        }
    }

    private List<Result> BuildResults(string search)
    {
        var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return new List<Result>
            {
                ActionResult("Reiniciar o áudio", "vm restart", RestartAudioEngine),
                ActionResult("Controlar fontes", "Abrir lista de fontes", () => NavigateTo("vm source"), closeAfterAction: false),
                ActionResult("Controlar buses", "Abrir lista de buses", () => NavigateTo("vm bus"), closeAfterAction: false)
            };
        }

        return tokens[0].ToLowerInvariant() switch
        {
            "restart" or "reiniciar" => new List<Result>
            {
                ActionResult("Reiniciar o áudio", "Reinicia o motor de áudio do VoiceMeeter", RestartAudioEngine)
            },
            "source" or "sources" or "fonte" or "fontes" => BuildStripResults(tokens),
            "bus" or "buses" => BuildBusResults(tokens),
            "app" or "apps" or "application" or "applications" or "aplicacao" or "aplicacoes" => BuildApplicationResults(tokens),
            _ => new List<Result>
            {
                new Result
                {
                    Title = "Comando não reconhecido",
                    SubTitle = "Use: restart, source ou bus",
                    IcoPath = IconPath
                }
            }
        };
    }

    private List<Result> BuildStripResults(string[] tokens)
    {
        var index = ParseIndex(tokens, 1);
        if (index is null)
        {
            return Enumerable.Range(0, 8)
                .Select(i => BuildStripResult(i))
                .ToList();
        }

        var operation = tokens.Length > 2 ? tokens[2].ToLowerInvariant() : "toggle";
        return operation switch
        {
            "+" or "up" or "mais" => new List<Result> { ActionResult($"Fonte {index}: aumentar volume", "+3 dB", () => ChangeStripGain(index.Value, VolumeStepDb)) },
            "-" or "down" or "menos" => new List<Result> { ActionResult($"Fonte {index}: reduzir volume", "-3 dB", () => ChangeStripGain(index.Value, -VolumeStepDb)) },
            "set" or "definir" => BuildSetStripGainResults(index.Value, tokens),
            _ => new List<Result> { BuildStripResult(index.Value) }
        };
    }

    private List<Result> BuildSetStripGainResults(int index, string[] tokens)
    {
        if (tokens.Length <= 3 || !float.TryParse(tokens[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gain))
        {
            return new List<Result>
            {
                ActionResult(
                    $"Fonte {index}: definir volume",
                    "Digite um valor entre -60 e 12 dB e pressione Enter",
                    () => NavigateTo($"vm source {index} set "),
                    closeAfterAction: false)
            };
        }

        var clampedGain = Math.Clamp(gain, -60f, 12f);
        return new List<Result>
        {
            ActionResult(
                $"Fonte {index}: definir volume em {clampedGain:0.#} dB",
                "Pressione Enter para aplicar",
                () => SetStripGain(index, clampedGain))
        };
    }

    private List<Result> BuildBusResults(string[] tokens)
    {
        var index = ParseIndex(tokens, 1);
        if (index is null)
        {
            return Enumerable.Range(0, 8)
                .Select(i => BuildBusResult(i))
                .ToList();
        }

        var operation = tokens.Length > 2 ? tokens[2].ToLowerInvariant() : "+";
        var delta = operation is "-" or "down" or "menos" ? -VolumeStepDb : VolumeStepDb;
        return new List<Result>
        {
            ActionResult($"Bus {index.Value + 1}: {(delta > 0 ? "aumentar" : "reduzir")} volume", $"{delta:+#;-#;0} dB", () => ChangeBusGain(index.Value, delta))
        };
    }

    private List<Result> BuildApplicationResults(string[] tokens)
    {
        var applications = _audioSessions!.ListApplications();
        if (applications.Count == 0)
        {
            return Error("Nenhuma sessão de áudio encontrada", "Inicie uma aplicação que esteja reproduzindo áudio e tente novamente.");
        }

        if (ParseIndex(tokens, 1) is not { } index || index >= applications.Count)
        {
            return applications.Select((application, applicationIndex) =>
                ActionResult(
                    $"Aplicação {applicationIndex}: {application.DisplayName}",
                    $"Processo: {application.ProcessName} · Volume: {application.Volume:P0}. Enter aumenta 3 dB.",
                    () => ChangeApplicationVolume(application, VolumeStepDb)))
                .ToList();
        }

        var selected = applications[index];
        var operation = tokens.Length > 2 ? tokens[2].ToLowerInvariant() : "+";
        var delta = operation is "-" or "down" or "menos" ? -VolumeStepDb : VolumeStepDb;
        return new List<Result>
        {
            ActionResult(
                $"{selected.DisplayName}: {(delta > 0 ? "aumentar" : "reduzir")} volume",
                $"Processo: {selected.ProcessName} · {delta:+#;-#;0} dB",
                () => ChangeApplicationVolume(selected, delta))
        };
    }

    private Result BuildStripResult(int index)
    {
        var label = GetStripLabel(index);
        var device = GetStripDevice(index);
        var muted = TryGetStripMute(index, out var currentMute) && currentMute;
        var gain = _remote!.TryGetFloat($"Strip[{index}].Gain", out var gainValue) ? $"{gainValue:0.#} dB" : "ganho indisponível";
        // Keep the title stable. Flow Launcher uses the result title as part of
        // its usage ranking; changing it between "ativa" and "mutada" makes
        // the item receive a different usage score and move in the list.
        var state = muted ? "mutada" : "ativa";
        return ActionResult($"{index + 1:00} · {label}", $"Estado: {state} · Dispositivo: {device} · Ganho: {gain}. Enter alterna mute/unmute.", () =>
        {
            ToggleStripMute(index);
        }, closeAfterAction: false, score: 1000 - index, addSelectedCount: false, contextData: index);
    }

    private string GetStripLabel(int index)
    {
        if (_remote!.TryGetString($"Strip[{index}].Label", out var label) && !string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        return $"Fonte {index}";
    }

    private string GetStripDevice(int index)
    {
        if (_remote!.TryGetString($"Strip[{index}].device.name", out var device) && !string.IsNullOrWhiteSpace(device))
        {
            return device.Trim();
        }

        return "Nenhum dispositivo físico atribuído";
    }

    private bool IsStripMuted(int index)
    {
        return TryGetStripMute(index, out var muted) && muted;
    }

    private bool TryGetStripMute(int index, out bool muted)
    {
        if (_remote!.TryGetFloat($"Strip[{index}].Mute", out var muteValue))
        {
            // VoiceMeeter defines Mute=1 as on/muted and Mute=0 as off/active.
            muted = muteValue > 0.5f;
            return true;
        }

        muted = false;
        return false;
    }

    private void WaitForStripMute(int index, bool expectedMuted)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (TryGetStripMute(index, out var muted) && muted == expectedMuted)
            {
                return;
            }

            Thread.Sleep(25);
        }
    }

    private void PublishSourceResults()
    {
        if (_activeSourceQuery is null || ResultsUpdated is null)
        {
            return;
        }

        ResultsUpdated(this, new ResultUpdatedEventArgs
        {
            Results = Enumerable.Range(0, 8).Select(BuildStripResult).ToList(),
            Query = _activeSourceQuery,
            Token = CancellationToken.None
        });
    }

    private static void RestoreSelection(int index)
    {
        // Flow Launcher resets selection to zero after IResultUpdated replaces
        // the result collection. Its public plugin API has no selected-index
        // property, so restore it with Down only while Flow is foreground.
        if (index <= 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            Thread.Sleep(100);
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return;
            }

            GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!process.ProcessName.Equals("Flow.Launcher", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            for (var step = 0; step < index; step++)
            {
                keybd_event(VirtualKeyDown, 0, 0, UIntPtr.Zero);
                keybd_event(VirtualKeyDown, 0, KeyEventUp, UIntPtr.Zero);
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private static bool IsSourceListQuery(string search)
    {
        var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 1 && tokens[0].ToLowerInvariant() is "source" or "sources" or "fonte" or "fontes";
    }

    private Result BuildBusResult(int index)
    {
        var gain = _remote!.TryGetFloat($"Bus[{index}].Gain", out var gainValue) ? $"{gainValue:0.#} dB" : "ganho indisponível";
        return ActionResult($"Bus {index + 1}", $"Ganho atual: {gain}. Enter aumenta 3 dB.", () => ChangeBusGain(index, VolumeStepDb));
    }

    private void RestartAudioEngine()
    {
        _remote!.RestartAudioEngine();
    }

    private void ChangeStripGain(int index, float deltaDb)
    {
        _remote!.ChangeStripGain(index, deltaDb);
        PublishSourceResults();
    }

    private void SetStripGain(int index, float gainDb)
    {
        _remote!.SetStripGain(index, gainDb);
        PublishSourceResults();
    }

    private void ToggleStripMute(int index)
    {
        var currentlyMuted = IsStripMuted(index);
        var newMuted = !currentlyMuted;
        _remote!.SetMute(index, newMuted);
        WaitForStripMute(index, newMuted);
        PublishSourceResults();
        RestoreSelection(index);
    }

    private void ChangeBusGain(int index, float deltaDb)
    {
        _remote!.ChangeBusGain(index, deltaDb);
    }

    private void ChangeApplicationVolume(AudioApplication application, float deltaDb)
    {
        var changed = _audioSessions!.ChangeVolume(application.ProcessName, deltaDb);
        if (changed == 0)
        {
            return;
        }
    }

    private void EnsureRemote()
    {
        _remote ??= new VoiceMeeterRemoteClient();
        _remote.Connect();
    }

    private static int? ParseIndex(string[] tokens, int position)
    {
        if (tokens.Length <= position || !int.TryParse(tokens[position], out var index))
        {
            return null;
        }

        return index is >= 0 and < 8 ? index : null;
    }

    private static Result ActionResult(string title, string subtitle, Action action, bool closeAfterAction = true, int score = 0, bool addSelectedCount = true, object? contextData = null) => new()
    {
        Title = title,
        SubTitle = subtitle,
        IcoPath = IconPath,
        Score = score,
        AddSelectedCount = addSelectedCount,
        ContextData = contextData,
        Action = _ =>
        {
            action();
            return closeAfterAction;
        }
    };

    private static List<Result> Error(string title, string subtitle) => new()
    {
        new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = IconPath
        }
    };

    private void NavigateTo(string search)
    {
        _context?.API.ChangeQuery(search, true);
    }

    public void Dispose()
    {
        _remote?.Dispose();
        _remote = null;
    }
}
