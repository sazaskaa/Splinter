# Splinter

Criei o Splinter para resolver um incômodo do meu dia a dia. Toda vez que eu queria jogar PS5 e continuar usando o PC no monitor secundário, precisava arrastar as janelas, mudar o modo de exibição do Windows e trocar a entrada do monitor principal. Quando voltava ao PC, precisava reorganizar as janelas à mão.

Agora, com um clique, o Splinter guarda a posição das janelas, deixa o PC só no monitor secundário e muda o principal para o HDMI do PS5. Com outro clique, pelo secundário, ele volta o monitor principal para DisplayPort, restaura a área de trabalho estendida e devolve as janelas que continuam abertas ao monitor e à posição em que estavam.

## Compatibilidade

A versão atual foi feita para o meu setup com Windows de 64 bits, um **Asus VG279Q** (`AUS2782`) como monitor principal e um **AOC T2355e** (`AOC2355`) à esquerda. O PC usa DisplayPort no Asus, e o PS5 usa HDMI no mesmo monitor. A troca automática depende do suporte a DDC/CI, inclusive para receber o comando de volta enquanto o HDMI está selecionado.

Os identificadores dos monitores e a posição do secundário estão definidos no código. Para adaptar o Splinter a outro setup, veja [`Splinter.cs`](Codigo-fonte/Splinter.cs) e [`MonitorInput.cs`](Codigo-fonte/MonitorInput.cs).

## Como usar

1. Comece com as duas telas estendidas e o Asus na entrada DisplayPort.
2. Abra `Splinter.exe` para ativar o modo PS5.
3. Quando quiser voltar ao PC, abra o mesmo executável pelo monitor secundário.

## Como compilar

Com o compilador do .NET Framework de 64 bits disponível no Windows, rode na raiz do repositório:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Codigo-fonte\Compilar.ps1
```

O script gera `Codigo-fonte\build\Splinter.exe`. Consulte o [guia de manutenção](Codigo-fonte/LEIA-ME.md) para ver o que cada arquivo faz e como testar a sua versão.
