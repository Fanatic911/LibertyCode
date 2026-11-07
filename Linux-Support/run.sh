#!/bin/sh
# LibertyCode - lancement (Linux / macOS)
#   ./run.sh            demarre sur /project (ou le dernier projet si AutoSelect = true)
#   ./run.sh .          utilise le dossier ou tu te trouves comme espace de travail
#   ./run.sh --debug    ouvre la fenetre de debug en direct
# Astuce : ln -s "$PWD/run.sh" ~/.local/bin/liberty   puis, dans n'importe quel projet : liberty .

# Retrouve le vrai dossier du script, meme s'il est lance via un lien symbolique.
SRC="$0"
while [ -L "$SRC" ]; do
    DIR=$(cd -P "$(dirname "$SRC")" && pwd)
    SRC=$(readlink "$SRC")
    case "$SRC" in /*) ;; *) SRC="$DIR/$SRC" ;; esac
done
HERE=$(cd -P "$(dirname "$SRC")" && pwd)

if [ ! -x "$HERE/.venv/bin/python" ]; then
    echo "Environnement introuvable : lance d'abord ./install.sh"
    exit 1
fi

# On ne change pas de dossier : "run.sh ." ouvre le dossier courant de l'utilisateur.
PYTHONPATH="$HERE${PYTHONPATH:+:$PYTHONPATH}" exec "$HERE/.venv/bin/python" -m liberty_code "$@"
