#!/bin/sh
# LibertyCode - installation des dependances (Linux / macOS)
cd "$(dirname "$0")" || exit 1

echo "=============================================="
echo "  LibertyCode - installation des dependances"
echo "=============================================="
echo

PY=""
for c in python3.13 python3.12 python3.11 python3.10 python3 python; do
    if command -v "$c" >/dev/null 2>&1 &&
       "$c" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' >/dev/null 2>&1; then
        PY="$c"
        break
    fi
done

if [ -z "$PY" ]; then
    echo "[ERREUR] Python 3.10 ou plus recent est introuvable."
    case "$(uname -s)" in
        Darwin) echo "Installe-le avec : brew install python   (ou https://www.python.org/downloads/)" ;;
        *)      echo "Debian/Ubuntu : sudo apt install python3 python3-venv python3-pip"
                echo "Fedora        : sudo dnf install python3"
                echo "Arch          : sudo pacman -S python" ;;
    esac
    exit 1
fi
echo "Python : $("$PY" --version 2>&1) ($PY)"

if [ ! -x .venv/bin/python ]; then
    echo "Creation de l'environnement virtuel .venv ..."
    rm -rf .venv
    if ! "$PY" -m venv .venv; then
        rm -rf .venv
        echo "[ERREUR] Impossible de creer l'environnement virtuel."
        echo "Debian/Ubuntu : sudo apt install python3-venv"
        exit 1
    fi
fi

echo "Installation des paquets (rich, prompt_toolkit, httpx, cryptography) ..."
.venv/bin/python -m pip install --upgrade pip || echo "(mise a jour de pip ignoree)"
if ! .venv/bin/python -m pip install -r requirements.txt; then
    echo "[ERREUR] L'installation des dependances a echoue."
    exit 1
fi

chmod +x run.sh 2>/dev/null
echo
echo "Installation terminee. Lance LibertyCode avec ./run.sh"
