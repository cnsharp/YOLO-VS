# Agent YOLO for Visual Studio

YOLO is a Visual Studio extension that acts as a bridge between the IDE and your AI coding agents. From a docked tool window you launch agents such as Claude, Codex, Copilot, or Cursor directly inside a real terminal, and steer them with Y (skip permission) / R (resume session) toggles — so the agent runs where your code already lives, with its output and working directory rooted in the current solution.

Install range is `[17.0, 19.0)`, so it covers both **Visual Studio 2022** and **Visual Studio 2026**. It is developed and debugged against VS2026 Community.

## Features

- **Right-side tool window**: a YOLO window docked next to the Solution Explorer. Open it from the top-level **Agent YOLO** entry on the **View** menu, or press **Ctrl+W, Y**.
- **Data-driven agent dropdown**: lists the agents found in `agents.json` (Claude, Codex, Copilot, Cursor, OpenCode, etc. — 34 built-ins), kept separate from code — add an agent by editing the JSON, not the source.
- **Y (skip permission) / R (resume session) global toggles**: sticky, persisted across sessions. At launch they append the agent's skip/resume flag (or inject a skip env var, e.g. `GOOSE_MODE=auto`) — they never touch already-running sessions.
- **Real multi-tab ConPTY terminal**: each Agent launch opens a fresh tab backed by a hand-drawn ConPTY (not WebView2 / Windows Terminal control). Multiple Agent sessions run concurrently; every tab has a close button, and text selection / copy work inside the TUI.
- **Tab switcher**: when more than one terminal tab is open, a compact chevron button appears at the right end of the toolbar and opens a tab-list popup to jump between sessions.
- **Keyboard capture**: the terminal keeps keyboard focus (click to capture; Esc / Ctrl+C work as expected).
- **Settings page** (`Tools > Options > Agent YOLO`): edit agent flags, custom tools, and icons. Icons are validated as real images, and network icon URLs are downloaded to a local cache.
- **Installed-agent cache**: the detected agent set is persisted and only re-scanned in the background when it changes.

## Screenshot

![YOLO tool window with a running agent terminal](Screenshots/yolo-panel.png)

## Supported agents

Every agent below ships in `agents.json` (34 built-ins). The file is the source of truth — this table is generated from it, so add or change an agent by editing the JSON, not the code.

| id | display name | command | website |
|---|---|---|---|
| claude | Claude Code | `claude` | <a href="https://claude.ai/"><img src="Resources/icons/agents/claude.svg" height="20" alt="Claude Code"></a> |
| codex | Codex | `codex` | <a href="https://openai.com/codex"><img src="Resources/icons/agents/codex.svg" height="20" alt="Codex"></a> |
| cursor | Cursor | `cursor-agent` | <a href="https://cursor.com/"><img src="Resources/icons/agents/cursor.svg" height="20" alt="Cursor"></a> |
| copilot | GitHub Copilot | `copilot` | <a href="https://github.com/features/copilot"><img src="Resources/icons/agents/copilot.svg" height="20" alt="GitHub Copilot"></a> |
| opencode | OpenCode | `opencode` | <a href="https://opencode.ai/"><img src="Resources/icons/agents/opencode.svg" height="20" alt="OpenCode"></a> |
| aider | Aider | `aider` | <a href="https://aider.chat/"><img src="Resources/icons/agents/aider.svg" height="20" alt="Aider"></a> |
| cline | Cline | `cline` | <a href="https://cline.bot/"><img src="Resources/icons/agents/cline.svg" height="20" alt="Cline"></a> |
| continue | Continue | `cn` | <a href="https://continue.dev/"><img src="Resources/icons/agents/continue.svg" height="20" alt="Continue"></a> |
| openclaw | OpenClaw | `openclaw` | <a href="https://openclaw.ai/"><img src="Resources/icons/agents/openclaw.svg" height="20" alt="OpenClaw"></a> |
| kiro | Kiro | `kiro-cli` | <a href="https://kiro.dev/"><img src="Resources/icons/agents/kiro.svg" height="20" alt="Kiro"></a> |
| goose | Goose | `goose` | <a href="https://block.github.io/goose/"><img src="Resources/icons/agents/goose.svg" height="20" alt="Goose"></a> |
| crush | Charm Crush | `crush` | <a href="https://charm.sh/crush"><img src="Resources/icons/agents/crush.png" height="20" alt="Charm Crush"></a> |
| amp | Amp | `amp` | <a href="https://ampcode.com/"><img src="Resources/icons/agents/amp.svg" height="20" alt="Amp"></a> |
| kimi | Kimi | `kimi` | <a href="https://kimi.moonshot.cn/"><img src="Resources/icons/agents/kimi.svg" height="20" alt="Kimi"></a> |
| qwen-code | Qwen Code | `qwen` | <a href="https://qwen.ai/qwencode"><img src="Resources/icons/agents/qwen-code.png" height="20" alt="Qwen Code"></a> |
| trae | TraeCode | `traecli` | <a href="https://www.trae.ai/"><img src="Resources/icons/agents/trae.svg" height="20" alt="TraeCode"></a> |
| codebuddy | CodeBuddy | `codebuddy` | <a href="https://www.codebuddy.ai/"><img src="Resources/icons/agents/codebuddy.svg" height="20" alt="CodeBuddy"></a> |
| qoder | Qoder | `qoder` | <a href="https://qoder.com/"><img src="Resources/icons/agents/qoder.svg" height="20" alt="Qoder"></a> |
| devin | Devin | `devin` | <a href="https://devin.ai/"><img src="Resources/icons/agents/devin.svg" height="20" alt="Devin"></a> |
| grok | Grok | `grok` | <a href="https://grok.com/"><img src="Resources/icons/agents/grok.svg" height="20" alt="Grok"></a> |
| antigravity | Antigravity | `agy` | <a href="https://antigravity.google/"><img src="Resources/icons/agents/antigravity.png" height="20" alt="Antigravity"></a> |
| mistral-vibe | Mistral Vibe | `vibe` | <a href="https://mistral.ai/"><img src="Resources/icons/agents/mistral-vibe.svg" height="20" alt="Mistral Vibe"></a> |
| kilo | Kilo Code | `kilo` | <a href="https://kilocode.ai/"><img src="Resources/icons/agents/kilo.svg" height="20" alt="Kilo Code"></a> |
| hermes | Hermes | `hermes` | <a href="https://hermes-agent.nousresearch.com/"><img src="Resources/icons/agents/hermes.png" height="20" alt="Hermes"></a> |
| pi | Pi | `pi` | <a href="https://pi.dev/"><img src="Resources/icons/agents/pi.svg" height="20" alt="Pi"></a> |
| droid | Droid | `droid` | <a href="https://factory.ai/"><img src="Resources/icons/agents/droid.svg" height="20" alt="Droid"></a> |
| aug | Auggie | `auggie` | <a href="https://augmentcode.com/"><img src="Resources/icons/agents/aug.svg" height="20" alt="Auggie"></a> |
| rovo | Rovo Dev | `rovo` | <a href="https://rovo.atlassian.com/"><img src="Resources/icons/agents/rovo.svg" height="20" alt="Rovo Dev"></a> |
| prime-agent | Prime Agent | `prime-agent` | <a href="https://www.primeintellect.ai/"><img src="Resources/icons/agents/prime-agent.png" height="20" alt="Prime Agent"></a> |
| autohand | Autohand | `autohand` | <a href="https://autohand.ai/"><img src="Resources/icons/agents/autohand.svg" height="20" alt="Autohand"></a> |
| command-code | Command Code | `command-code` | <a href="https://commandcode.ai/"><img src="Resources/icons/agents/command-code.svg" height="20" alt="Command Code"></a> |
| ante | Ante | `ante` | <a href="https://antigma.ai/"><img src="Resources/icons/agents/ante.svg" height="20" alt="Ante"></a> |
| codebuff | Codebuff | `codebuff` | <a href="https://codebuff.com/"><img src="Resources/icons/agents/codebuff.png" height="20" alt="Codebuff"></a> |
| omp | OMP | `omp` | <a href="https://ohmyposh.dev/"><img src="Resources/icons/agents/omp.svg" height="20" alt="OMP"></a> |

Anything not listed works as a custom tool: add it under `Tools > Options > Agent YOLO` and fill in its flags yourself.

---

## Tech stack

- **Extension skeleton**: `Microsoft.VisualStudio.SDK` `17.0.31902.203` (`AsyncPackage`)
- **UI framework**: WPF (tool window content, themed via `EnvironmentColors` / `VSColorTheme`)
- **Target framework**: `net472` (maximizes compatibility with the VS Shell / COM interop)
- **Debug target**: Visual Studio 2026 (v18), launched via the VSIX experimental instance with F5
  - The v17-referenced assemblies resolve against the v18 runtime through VS's binding redirects.
- **Terminal**: a hand-drawn ConPTY implementation (`Terminal/`) — selected over hosting Windows Terminal's control during the original spike.

## Directory structure

```
yolo-vs/
  Yolo.csproj                 # SDK-style project file (net472)
  Yolo.sln                    # Solution
  Yolo.vsct                   # Command table: View menu + Ctrl+W,Y
  source.extension.vsixmanifest  # VSIX manifest (InstallationTarget [17.0,19.0))
  YoloPackage.cs              # AsyncPackage entry point
  YoloToolWindowCommand.cs    # View-menu command (toggles the tool window)
  Constants.cs                # Constant definitions
  agents.json                 # Agent metadata (embedded resource)
  Resources.resx / Resources.Designer.cs  # Localization resources
  Properties/AssemblyInfo.cs

  ToolWindow/
    YoloToolWindowPane.cs     # ToolWindowPane
    YoloPanel.xaml(.cs)       # Panel UI (agent dropdown + Y/R toggles + tab switcher + multi-tab terminal)

  Terminal/                   # Hand-drawn ConPTY terminal (no WebView2 / HwndHost)
    ConPty.cs                 # CreatePseudoConsole / ResizePseudoConsole / Close P/Invoke
    ConPtyTerminal.cs         # Process + PTY lifecycle, pending command/env buffering
    TerminalEmulator.cs       # VT/ANSI escape-sequence parsing
    TerminalSurface.cs        # Hand-drawn text grid (cell metrics)
    WpfTerminalView.xaml(.cs) # WPF host control
    TerminalPalette.cs / CharWidth.cs / ITerminalView.cs / Logger.cs

  Links/                      # Terminal-output link detection (present, not yet wired into the main flow)
    YoloLinkPatterns.cs       # Regex patterns
    YoloHyperlink.cs          # Link model (LinkMatch / LinkTarget / LinkKind)
    FileLinkFilter.cs         # File-path links
    StackTraceLinkFilter.cs   # Stack-frame / bare file-name links
    TypeLinkFilter.cs         # Type-name links
    MemberLinkFilter.cs       # Class.member links
    UrlLinkFilter.cs          # URL links
    OutputLinkInterceptor.cs  # Output interceptor entry point

  Agents/
    AgentRegistry.cs           # Loads and queries agents.json
    AgentDetector.cs           # PATH + --version probing
    InstalledAgents.cs        # Installed-set cache + background re-scan
    AgentIconImage.cs          # SVG/PNG icon rendering
    DefaultSkipEnvs.cs        # env-style bypass (e.g. GOOSE_MODE=auto)
    ExecutableNames.cs         # Executable-name normalization

  Options/
    YoloOptionsPage.cs         # DialogPage (Tools > Options > Agent YOLO > Agents)
    YoloOptionsControl.xaml(.cs)
    YoloSettingsDialog.xaml(.cs)
    YoloSettings.cs            # Persistence (XmlSerializer, at %LocalAppData%/YoloVS/settings.xml)
    AgentModels.cs             # Settings-page data model

  Resources/                  # Brand tile + icons
    pluginIcon.png / pluginIcon.svg / pluginIcon_dark.svg
    icons/agents/*             # Per-agent icons
    icons/skipY*, resume*      # Y/R toggle icon geometry
```

## Build

```bash
# Build with the .NET CLI (name the csproj explicitly — the repo has several sln/csproj files)
dotnet build Yolo.csproj -c Debug
# Output: bin/Debug/net472/Yolo.vsix

# Or use Visual Studio: open Yolo.sln and press F6.
```

## Debug

1. Open `Yolo.sln` in **Visual Studio 2026**.
2. Press **F5** to launch the VS2026 extension experimental instance and load this extension.
3. Open the tool window via the top-level **Agent YOLO** entry on the **View** menu, or **Ctrl+W, Y**.
   - Settings: `Tools > Options > Agent YOLO` (corresponds to `YoloOptionsPage`).
4. Pick an agent and launch — it runs as a live process inside a new terminal tab.

> After editing `Yolo.vsct` or `source.extension.vsixmanifest`, reset the experimental instance before F5 so the command table is rebuilt from the registry. (See `AGENTS.md` for the exact `CreateExpInstance /Reset` command — it requires the `18.0_9602cece` instance suffix.)
