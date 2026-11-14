# Código-fonte do Splinter

O Splinter é um programa em C# para Windows de 64 bits.

- `Splinter.cs` coordena os dois cliques e alterna a configuração de monitores do Windows. Os identificadores `AUS2782` e `AOC2355` ficam no início do arquivo.
- `MonitorInput.cs` envia ao Asus os comandos DDC/CI para HDMI e DisplayPort.
- `WindowLayout.cs` salva e restaura as janelas, incluindo suas posições e seu estado de maximização.
- `Splinter.ico` é o ícone incorporado ao executável.
- `Splinter.manifest` configura o tratamento de escala de tela pelo Windows.

## Como compilar e testar

Na pasta `Codigo-fonte`, execute no PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Compilar.ps1
```

O script usa o compilador do .NET Framework de 64 bits e gera `build\Splinter.exe`. A opção `-ExecutionPolicy Bypass` vale só para esse processo do PowerShell.

Comece com as duas telas estendidas e o Asus no DisplayPort. Abra `build\Splinter.exe` para ativar o modo PS5 e abra o mesmo arquivo novamente para voltar. O primeiro processo fica em segundo plano entre os cliques para manter o controle da entrada do Asus.

Para usar a versão nova no dia a dia, guarde uma cópia do executável da raiz e substitua-o pelo que você compilou. Faça isso depois de voltar ao modo estendido, quando o processo em segundo plano já tiver terminado.
