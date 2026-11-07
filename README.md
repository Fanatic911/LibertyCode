# LibertyCode

> **A free, open-source, AI-powered terminal coding agent — built for developers who want full control over their tools and costs.**

> ⚠️ **LibertyCode is currently in active development.** Expect breaking changes, bugs, and missing features.  
> Please [report any issues on GitHub](https://github.com/Fanatic911/LibertyCode/issues).

---

## Overview

LibertyCode is a lightweight terminal coding agent that puts an AI directly in your shell. It reads, writes, and refactors files in your workspace, runs shell commands, and manages projects — all powered by the LLM provider of your choice.

No subscriptions. No lock-in. Use free providers, paid APIs, or your own self-hosted models.

---

## Features

- **AI coding agent** — reads, writes, edits, and refactors files in your workspace
- **Multi-provider** — supports free, paid, and self-hosted LLM providers
- **Undo** — revert any file change made by the agent with `/undo`
- **Project management** — create and switch between projects with `/project`
- **Permission modes** — `ask`, `edits`, or `auto` — control how much the agent can do without asking
- **Shell commands** — run build, test, and git commands directly from the REPL
- **Encrypted API keys** — keys are stored encrypted locally, never in plain text
- **Debug log** — full trace log for troubleshooting (`/log`)

---

## Supported Providers

| Provider | Type | Free tier |
|---|---|---|
| [Groq](https://console.groq.com) | Cloud | ✅ Yes |
| [GitHub Models](https://github.com/marketplace/models) | Cloud | ✅ Yes |
| [Google Gemini](https://aistudio.google.com) | Cloud | ✅ Yes |
| [OpenRouter](https://openrouter.ai) | Cloud | ✅ Partial |
| [OpenAI](https://platform.openai.com) | Cloud | ❌ Paid |
| [Anthropic](https://console.anthropic.com) | Cloud | ❌ Paid |
| [DeepSeek](https://platform.deepseek.com) | Cloud | ❌ Paid |
| [Mistral](https://console.mistral.ai) | Cloud | ❌ Paid |
| [NVIDIA NIM](https://build.nvidia.com) | Cloud | ❌ Paid |
| [Ollama](https://ollama.com) | Self-hosted | ✅ Free |

Any provider that exposes an OpenAI-compatible `/v1/chat/completions` endpoint is also supported via the `LIBERTY_BASE_URL` environment variable.

---

## Installation

### Windows

1. Download the latest release from the [Releases](https://github.com/Fanatic911/LibertyCode/releases/latest) page.
2. Open the `Finish` folder and pick the folder matching your processor architecture.
3. Extract `LibertyCode.exe` anywhere on your system.
4. Run it — a setup wizard will guide you through selecting a provider, entering your API key, and choosing a model.

The `Finish` folder contains one `LibertyCode.exe` compiled for each supported architecture:

```
Finish/
├── x64/     ← 64-bit Intel / AMD (most PCs)
├── x86/     ← 32-bit Intel / AMD
└── arm64/   ← 64-bit ARM (e.g. Snapdragon / Windows on ARM)
```

> Not sure which one to choose? On most computers, use **`x64`**.

**Requirements:** Windows 10 / 11 (32-bit Windows 10 is supported via `x86`) · no .NET install needed (the runtime is bundled in the `.exe`)

---

### Linux (Debian / Ubuntu)

LibertyCode ships as a proper `.deb` package. It installs automatically — Python, the virtual environment, and all dependencies are set up without any manual steps.

**Requirements:** Debian 11+ or Ubuntu 20.04+ · `sudo` access

```bash
# 1. Download Linux-Support.zip from the Releases page and extract it
unzip Linux-Support.zip
cd Linux-Support

# 2. Install the package
sudo apt install ./libertycode_1.0.0_all.deb
```

`apt` will automatically install Python 3 and the required system packages, then the `postinst` script will:
- Detect the best available Python version (3.10 or newer)
- Create an isolated virtual environment in `/opt/libertycode/.venv`
- Install all Python dependencies (`rich`, `prompt_toolkit`, `httpx`, `cryptography`)
- Create the `libertycode` command in `/usr/local/bin`
- Remove the installer files from your folder

After installation, run from anywhere:

```bash
libertycode          # opens /project hub (project list)
libertycode .        # opens the current folder as workspace
libertycode ~/myapp  # opens a specific folder
```

**To uninstall:**

```bash
sudo apt remove libertycode
```

This removes the command, the virtual environment, and all installed files. Your projects and settings (stored in `~/.config/LibertyCode/`) are kept.

---

### macOS

LibertyCode ships as a double-clickable `.app` installer bundle for macOS.

**Requirements:** macOS 11 (Big Sur) or newer

```
Linux-Support/
└── LibertyCode-Installer.app   ← double-click this in Finder
```

What happens when you double-click:
1. Terminal opens automatically
2. Python 3.11 is installed via Homebrew if not already present (Homebrew itself is installed if needed)
3. A virtual environment is created in `~/Library/Application Support/LibertyCode/.venv`
4. All Python dependencies are installed
5. A `LibertyCode.app` launcher is created in `~/Applications/`
6. The installer bundle deletes itself

After installation, launch LibertyCode by double-clicking **`~/Applications/LibertyCode.app`**, or from the terminal:

```bash
open ~/Applications/LibertyCode.app
```

> **Gatekeeper warning:** On first launch, macOS may show "unidentified developer". Right-click the `.app` → **Open** → **Open** to bypass it.

---

## First Launch

On first run, the setup wizard will ask you to:

1. **Choose a provider** — pick from the list (Groq is recommended for free usage)
2. **Enter your API key** — paste your key (it is stored encrypted, never in plain text)
3. **Pick a model** — choose from the provider's available models

Once set up, type any request in plain language and the agent will get to work.

---

## Commands

| Command | Description |
|---|---|
| `/project [list\|new\|open\|delete] <name>` | List, create, open, or delete a project |
| `/leave` | Leave the current project and return to the hub |
| `/cd <path>` | Change the workspace folder |
| `/models [name]` | Change the active model |
| `/provider [set <n>]` | List providers or switch provider |
| `/provider set api-key <key>` | Update the API key for the current provider |
| `/settings` | Show current settings |
| `/settings set <key> <value>` | Change a setting |
| `/mode [ask\|edits\|auto]` | Set the permission level |
| `/undo` | Revert the last agent file changes |
| `/prompt [edit\|clear]` | View or edit extra system instructions |
| `/clear` | Start a new conversation (keeps settings) |
| `/log` | Show the debug log |
| `/help` | List all commands |

Type `!<command>` to run a shell command directly — e.g. `!git status`, `!npm test`.

---

## Permission Modes

| Mode | Behavior |
|---|---|
| `ask` | Asks before every file edit or shell command *(default)* |
| `edits` | Auto-approves file edits, asks for shell commands |
| `auto` | Auto-approves everything (use with caution) |

Switch at any time with `/mode ask`, `/mode edits`, or `/mode auto`.  
For sensitive environments, `ask` mode is strongly recommended.

---

## Projects

LibertyCode organizes work into projects stored in `~/LibertyCode/Projects/`.

```
/project new myapp    — create a new project
/project open myapp   — open an existing project
/project list         — list all projects
/project delete myapp — delete a project
```

You can also open any folder directly as a workspace:

```bash
libertycode /path/to/any/folder
```

---

## Self-Hosted Models (Ollama)

Select **Ollama** as your provider. LibertyCode connects to `http://localhost:11434` automatically and lists all models you have pulled.

```bash
ollama pull llama3.1    # pull a model
libertycode             # LibertyCode will find it
```

You can also use any OpenAI-compatible endpoint:

```bash
LIBERTY_BASE_URL=http://your-server:port/v1 libertycode
```

---

## Data & Settings

| Path | Contents |
|---|---|
| `~/.config/LibertyCode/settings.json` | Settings (provider, model, language…) |
| `~/.config/LibertyCode/.env.enc` | Encrypted API keys |
| `~/.config/LibertyCode/libertycode.log` | Log file |
| `~/.config/LibertyCode/prompt.txt` | Custom system instructions |
| `~/LibertyCode/Projects/` | Your projects |

---

## Building from Source

```bash
# Windows / .NET version
git clone https://github.com/Fanatic911/LibertyCode
cd LibertyCode
dotnet build
dotnet run --project LibertyCode

# Release build: compile LibertyCode.exe for every architecture (Windows)
# Output: Finish\x64, Finish\x86, Finish\arm64
build-all.bat

# Linux / macOS (Python version)
git clone https://github.com/Fanatic911/LibertyCode
cd LibertyCode/Linux-Support
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python -m liberty_code
```

---

## Security

LibertyCode is new software — security vulnerabilities may exist. Always use the latest release and read [SECURITY.md](SECURITY.md) before using LibertyCode in sensitive environments.

---

## License

LibertyCode is released under the **LibertyCode License**. Key points:

- ✅ Free to use for personal and non-commercial purposes
- ✅ Derivative works are allowed — but must be **fully open source**
- ❌ Commercial use is **not permitted**
- ✅ AI-generated content produced with LibertyCode must be **disclosed as AI-generated**
- ✅ Any project based on LibertyCode must credit LibertyCode

See [LICENSE.md](LICENSE.md) for the full terms.

---

## Contributing

Contributions are welcome. Please open an issue before submitting a large pull request.

Found a bug? **[Open an issue](https://github.com/Fanatic911/LibertyCode/issues)** — it helps a lot.
