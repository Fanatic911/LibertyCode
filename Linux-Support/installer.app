#!/bin/sh
# LibertyCode — Installeur macOS
# Installe Python 3.11 (via Homebrew si nécessaire), crée le venv,
# installe les dépendances, puis supprime cet installeur.
# L'utilisateur n'a rien à faire.

set -e
SELF="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$HERE"

echo ""
echo "╔══════════════════════════════════════════╗"
echo "║      LibertyCode — Installeur macOS      ║"
echo "╚══════════════════════════════════════════╝"
echo ""

# ── 1. Trouver Python 3.10+ ───────────────────────────────────────────────────
PY=""
for c in python3.13 python3.12 python3.11 python3.10 python3 python; do
    if command -v "$c" >/dev/null 2>&1 && \
       "$c" -c 'import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)' >/dev/null 2>&1; then
        PY="$c"; break
    fi
done

# ── 2. Installer Python via Homebrew si absent ────────────────────────────────
if [ -z "$PY" ]; then
    echo "[INFO] Python 3.10+ introuvable."
    if command -v brew >/dev/null 2>&1; then
        echo "[INFO] Installation via Homebrew..."
        brew install python@3.11
        PY="$(brew --prefix python@3.11)/bin/python3.11"
        # Fallback si le chemin exact n'existe pas
        if [ ! -x "$PY" ]; then PY="python3.11"; fi
    else
        echo "[INFO] Homebrew introuvable — installation de Homebrew + Python..."
        /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
        # Recharger PATH pour Homebrew (Apple Silicon / Intel)
        if [ -f /opt/homebrew/bin/brew ]; then
            eval "$(/opt/homebrew/bin/brew shellenv)"
        elif [ -f /usr/local/bin/brew ]; then
            eval "$(/usr/local/bin/brew shellenv)"
        fi
        brew install python@3.11
        PY="python3.11"
    fi
fi

echo "[OK] Python : $($PY --version 2>&1) ($PY)"

# ── 3. Créer l'environnement virtuel ─────────────────────────────────────────
if [ ! -x "$HERE/.venv/bin/python" ]; then
    echo "[INFO] Création de l'environnement virtuel .venv..."
    rm -rf "$HERE/.venv"
    "$PY" -m venv "$HERE/.venv"
fi

# ── 4. Installer les dépendances ─────────────────────────────────────────────
echo "[INFO] Installation des dépendances (rich, prompt_toolkit, httpx, cryptography)..."
"$HERE/.venv/bin/python" -m pip install --upgrade pip -q
"$HERE/.venv/bin/python" -m pip install -r "$HERE/requirements.txt" -q

# ── 5. Rendre run.sh exécutable ──────────────────────────────────────────────
chmod +x "$HERE/run.sh" 2>/dev/null || true

# ── 6. Créer le lanceur LibertyCode.app dans le dossier courant ──────────────
LAUNCHER="$HERE/LibertyCode.app"
cat > "$LAUNCHER" << 'LAUNCHEOF'
#!/bin/sh
HERE="$(cd "$(dirname "$0")" && pwd)"
if [ ! -x "$HERE/.venv/bin/python" ]; then
    echo "[ERREUR] Environnement introuvable. Relancez installer.app."
    exit 1
fi
PYTHONPATH="$HERE${PYTHONPATH:+:$PYTHONPATH}" exec "$HERE/.venv/bin/python" -m liberty_code "$@"
LAUNCHEOF
chmod +x "$LAUNCHER"

echo ""
echo "[OK] LibertyCode installé avec succès !"
echo "[OK] Lanceur créé : LibertyCode.app"
echo ""

# ── 7. Se supprimer ──────────────────────────────────────────────────────────
rm -f "$SELF"
echo "[INFO] Installeur supprimé."
echo ""
echo "  Lancez LibertyCode avec :  ./LibertyCode.app"
echo ""
