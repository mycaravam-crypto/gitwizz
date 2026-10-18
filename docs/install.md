# Installation

Requires the .NET 10 SDK and git ≥ 2.38 (`merge-tree --write-tree`; ≥ 2.40 for `--strategy rebase`). Providers need
an authenticated [`gh`](https://cli.github.com) (GitHub) or [`ado`](https://github.com/mycaravam-crypto/ado-devops)
logged in with `ado auth login` (Azure DevOps Server). Gates need the tools they run, e.g. Docker for environments.

Publish to a fixed folder and point a `gitwizz` command at it. Re-run `dotnet publish` after pulling changes.

```bash
# Linux, macOS, Git Bash
dotnet publish src/Gitwizz -c Release -o ~/tools/gitwizz
echo "alias gitwizz='dotnet ~/tools/gitwizz/gitwizz.dll'" >> ~/.bashrc   # zsh: ~/.zshrc
```


## Windows

PowerShell: `Set-Alias` can't pass arguments, so add a function to `$PROFILE`. If the profile won't load, run
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once.

```powershell
dotnet publish src/Gitwizz -c Release -o "$HOME\tools\gitwizz"
if (!(Test-Path $PROFILE)) { New-Item -Type File -Force $PROFILE }
Add-Content $PROFILE 'function gitwizz { dotnet "$HOME\tools\gitwizz\gitwizz.dll" @args }'
```

cmd (also works from PowerShell): put a `gitwizz.cmd` on your `PATH`. Run this once in PowerShell, then open a new
terminal.

```powershell
dotnet publish src/Gitwizz -c Release -o "$HOME\tools\gitwizz"
New-Item -Type Directory -Force "$HOME\bin" | Out-Null
Set-Content "$HOME\bin\gitwizz.cmd" "@dotnet `"$HOME\tools\gitwizz\gitwizz.dll`" %*" -Encoding ASCII
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (";$userPath;" -notlike "*;$HOME\bin;*") { [Environment]::SetEnvironmentVariable('Path', "$userPath;$HOME\bin", 'User') }
```

## Single-file binary

Fastest start-up: a precompiled (ReadyToRun) single-file binary in `src/Gitwizz/bin/Release/net10.0/<rid>/publish/`.
ReadyToRun compiles for the target platform, so build the Windows binary on Windows (or drop
`-p:PublishReadyToRun=true`).

```bash
dotnet publish src/Gitwizz -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true
# also: -r win-x64, -r osx-arm64
```
