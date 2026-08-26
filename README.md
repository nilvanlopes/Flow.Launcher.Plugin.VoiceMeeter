# Flow.Launcher.Plugin.VoiceMeeter

Plugin para controlar o VoiceMeeter Potato pelo Flow Launcher.

## Estado

Implementado: conexão com a VoiceMeeter Remote API, reinício do motor, mute/unmute de fontes, ajuste de ganho de strips/buses, volume por sessão de aplicação via NAudio e menu de contexto das sources.

As sources são exibidas em ordem fixa. O estado aparece no subtítulo e é atualizado sem fechar o Flow Launcher. O histórico de uso do Flow Launcher é desativado para esses resultados, e o foco retorna à source acionada.

## Desenvolvimento

```powershell
dotnet restore
dotnet build --configuration Release
```

O plugin usa a `VoicemeeterRemote64.dll` instalada pelo VoiceMeeter. A DLL não é redistribuída pelo projeto.

No menu de contexto (seta para a direita) de uma source estão disponíveis:

- mutar/desmutar;
- aumentar o volume em 3 dB;
- diminuir o volume em 3 dB;
- resetar o ganho para 0 dB;
- definir um valor absoluto entre -60 e 12 dB.

As ações não exibem notificações. O resultado da operação é refletido na própria lista quando aplicável.

Durante o bootstrap local, as referências do NAudio apontam para DLLs já instaladas pelo plugin `Audio Device Selector`. A referência do Flow Launcher usa a API instalada em `app-2.1.3`, incluindo `Result.AddSelectedCount`, necessária para manter a ordem fixa.
