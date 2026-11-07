using System.Text.RegularExpressions;

namespace LibertyCode;

/// <summary>Interface translation. English text is the key; Loc.T returns the French text when the language is "fr".</summary>
internal static class Loc
{
    public static string Lang { get; set; } = "en";

    private static readonly Dictionary<string, string> Exact = new()
    {
        // generic
        ["Cancelled."] = "Annulé.",
        ["Ready"] = "Prêt",
        ["No projects to delete."] = "Aucun projet à supprimer.",
        ["No projects yet."] = "Aucun projet pour le moment.",
        ["Nothing to undo."] = "Rien à annuler.",
        ["The log is empty."] = "Le journal est vide.",
        ["Use /provider set api-key to update the API key."] = "Utilise /provider set api-key pour changer la clé API.",
        ["You are already on /project."] = "Tu es déjà sur /project.",
        ["press ctrl+c again to exit"] = "appuie encore sur ctrl+c pour quitter",
        ["Debug mode off"] = "Mode debug désactivé",
        ["Debug mode on: live window opened"] = "Mode debug activé : fenêtre ouverte",
        ["Debug mode on, but the window could not be opened."] = "Mode debug activé, mais la fenêtre n'a pas pu s'ouvrir.",
        ["Extra instructions cleared"] = "Instructions supplémentaires effacées",
        ["Saved. Applies to the next message."] = "Enregistré. Appliqué au prochain message.",
        ["No API key. Use /provider set api-key."] = "Pas de clé API. Utilise /provider set api-key.",
        ["That name has invalid characters."] = "Ce nom contient des caractères invalides.",
        ["The name could not be checked."] = "Le nom n'a pas pu être vérifié.",
        ["The key cannot be empty."] = "La clé ne peut pas être vide.",
        ["This model doesn't support tools: chat only. Pick another with /models for file editing."] = "Ce modèle ne gère pas les outils : discussion seulement. Choisis-en un autre avec /models pour modifier des fichiers.",
        ["Several tool calls failed in a row, so I stopped. Tell me what to do next, or try /models with a stronger model."] = "Plusieurs appels d'outils ont échoué d'affilée, j'ai donc arrêté. Dis-moi quoi faire, ou essaie /models avec un modèle plus fort.",
        ["This provider has no model list: showing the built-in one (not verified)."] = "Ce fournisseur n'a pas de liste de modèles : liste intégrée affichée (non vérifiée).",
        ["Security values: Strict, Mid, Off.  Usage: /settings set Security Strict/Mid/Off"] = "Valeurs de sécurité : Strict, Mid, Off.  Utilisation : /settings set Security Strict/Mid/Off",
        ["Type lines to add. Empty line = save.  \"clear\" = erase all."] = "Écris les lignes à ajouter. Ligne vide = enregistrer.  \"clear\" = tout effacer.",
        ["Invalid key. Use only the characters of the key (no spaces, accents or symbols)."] = "Clé invalide. Utilise uniquement les caractères de la clé (pas d'espaces, d'accents ni de symboles).",
        ["Key too long. Paste only the API key."] = "Clé trop longue. Colle uniquement la clé API.",
        ["Thanks for using LibertyCode :)"] = "Merci d'utiliser LibertyCode :)",

        // banner / settings labels
        ["Model:"] = "Modèle :", ["Current directory:"] = "Dossier actuel :", ["Security:"] = "Sécurité :",
        ["Provider:"] = "Fournisseur :", ["Language:"] = "Langue :", ["Workspace:"] = "Espace de travail :",
        ["Autoselect:"] = "Autoselect :", ["Notification:"] = "Notification :", ["Debug:"] = "Debug :", ["Data:"] = "Données :",
        ["/help for commands  ·  esc to interrupt  ·  ctrl+c twice to exit"] = "/help pour les commandes  ·  esc pour interrompre  ·  ctrl+c deux fois pour quitter",
        ["asks before edits and commands"] = "demande avant les modifications et commandes",
        ["applies edits, asks before commands"] = "applique les modifications, demande avant les commandes",
        ["never asks (dangerous commands still ask)"] = "ne demande jamais (les commandes dangereuses demandent)",

        // tables
        ["Command"] = "Commande", ["Description"] = "Description",
        ["Project"] = "Projet", ["Created"] = "Créé", ["Modified"] = "Modifié", ["Files"] = "Fichiers",
        ["Change model (searchable list) or API key, anywhere"] = "Changer de modèle (liste filtrable) ou de clé API, partout",
        ["{0} models are still being checked in the background."] = "{0} modèles en cours de vérification en arrière-plan.",
        ["Models"] = "Modèles", ["Change model"] = "Changer de modèle", ["Change API key"] = "Changer la clé API",
        ["Permission level"] = "Niveau de sécurité",
        ["Revert the file changes of the last request"] = "Annuler les modifications de la dernière demande",
        ["List, create, open or delete a project"] = "Lister, créer, ouvrir ou supprimer un projet",
        ["Leave the project and go back to /project"] = "Quitter le projet et revenir à /project",
        ["Use another folder as workspace"] = "Utiliser un autre dossier de travail",
        ["New conversation"] = "Nouvelle conversation",
        ["Show settings, or set a value"] = "Afficher les réglages, ou en modifier un",
        ["Extra instructions added to the system prompt"] = "Instructions ajoutées au prompt système",
        ["Show the log"] = "Afficher le journal", ["Quit"] = "Quitter",

        // menus / permissions
        ["type to filter"] = "taper pour filtrer", ["↑↓ choose"] = "↑↓ choisir", ["Enter"] = "Entrée",
        ["Security"] = "Sécurité", ["AI provider"] = "Fournisseur d'IA",
       
        ["Yes, delete it"] = "Oui, supprimer", ["No, keep it"] = "Non, garder",
        ["Run this command?"] = "Lancer cette commande ?",
        ["This command looks destructive. Run it anyway?"] = "Cette commande semble destructrice. La lancer quand même ?",
        ["Yes, and don't ask again for edits this session"] = "Oui, et ne plus demander pour les modifications (session)",
        ["Yes, and don't ask again for this exact command"] = "Oui, et ne plus demander pour cette commande",
        ["Declined"] = "Refusé", ["Interrupted"] = "Interrompu", ["No changes"] = "Aucun changement",
        ["esc to interrupt"] = "esc pour interrompre",
        ["Testing the model"] = "Test du modèle", ["Running"] = "Exécution", ["Fetching models"] = "Récupération des modèles",
        ["Checking API key"] = "Vérification de la clé", ["Checking API key and models"] = "Vérification de la clé et des modèles", ["Checking the key and fetching the models"] = "Vérification de la clé et des modèles",
        ["Connecting to Ollama"] = "Connexion à Ollama",
    };

    private static readonly List<(Regex Rx, string Fr)> Patterns = Build(new Dictionary<string, string>
    {
        ["Usage: {0}"] = "Utilisation : {0}",
        ["Language: {0}"] = "Langue : {0}",
        ["Model set to {0}"] = "Modèle : {0}",
        ["Security: {0}"] = "Sécurité : {0}",
        ["Notification: {0}"] = "Notification : {0}",
        ["AutoSelect: {0}"] = "AutoSelect : {0}",
        ["Project '{0}' deleted"] = "Projet '{0}' supprimé",
        ["Project '{0}' already exists."] = "Le projet '{0}' existe déjà.",
        ["Project '{0}' not found. Try /project list."] = "Projet '{0}' introuvable. Essaie /project list.",
        ["Unknown command {0}. Type /help."] = "Commande inconnue {0}. Tape /help.",
        ["Folder not found: {0}"] = "Dossier introuvable : {0}",
        ["Could not save: {0}"] = "Impossible d'enregistrer : {0}",
        ["Could not delete '{0}': {1}"] = "Impossible de supprimer '{0}' : {1}",
        ["Could not check the key ({0}). Saved anyway."] = "Impossible de vérifier la clé ({0}). Enregistrée quand même.",
        ["Could not check the model list: {0}"] = "Impossible de vérifier la liste des modèles : {0}",
        ["Stopped after {0} steps. Send a message to continue."] = "Arrêt après {0} étapes. Envoie un message pour continuer.",
        ["{0} rejected this key. Nothing was saved."] = "{0} a refusé cette clé. Rien n'a été enregistré.",
        ["{0} rejected your API key. Use /provider set api-key to enter a valid one."] = "{0} a refusé ta clé API. Utilise /provider set api-key pour en saisir une valide.",
        ["{0} rejected this key (HTTP {1}). Check it and try again."] = "{0} a refusé cette clé (HTTP {1}). Vérifie-la et réessaie.",
        ["'{0}' is not a model available on {1}."] = "'{0}' n'est pas un modèle disponible sur {1}.",
        ["The model '{0}' is not available with this key. Pick one:"] = "Le modèle '{0}' n'est pas disponible avec cette clé. Choisis-en un :",
        ["The model '{0}' does not exist on {1} (any more). Pick one from the real list:"] = "Le modèle '{0}' n'existe pas (ou plus) sur {1}. Choisis dans la vraie liste :",
        ["This model is not usable with your key. Pick another one."] = "Ce modèle n'est pas utilisable avec ta clé. Choisis-en un autre.",
        ["An old API key was found. Use it?"] = "Une ancienne clé a été trouvée. Veux-tu l'utiliser ?",
        ["Did you mean: {0}"] = "Tu voulais dire : {0}",
        ["Type /models to see the real list."] = "Tape /models pour voir la vraie liste.",
        ["Model ({0} available)"] = "Modèle ({0} disponibles)",
        ["Read {0} lines"] = "{0} lignes lues", ["Read {0} line"] = "{0} ligne lue",
        ["{0} items"] = "{0} éléments", ["{0} item"] = "{0} élément",
        ["{0} files"] = "{0} fichiers", ["{0} file"] = "{0} fichier",
        ["{0} matches"] = "{0} résultats", ["{0} match"] = "{0} résultat",
        ["… +{0} lines"] = "… +{0} lignes",
        ["Timed out after {0}s"] = "Délai dépassé après {0}s",
        ["filter: {0}"] = "filtre : {0}",
    });

    private static List<(Regex, string)> Build(Dictionary<string, string> src)
    {
        var list = new List<(Regex, string)>();
        foreach (var (en, fr) in src)
        {
            string rx = "^" + Regex.Replace(Regex.Escape(en), @"\\?\{(\d)\}", "(?<g$1>.*?)") + "$";
            list.Add((new Regex(rx, RegexOptions.Singleline | RegexOptions.Compiled), fr));
        }
        return list;
    }

    public static string T(string en)
    {
        if (Lang != "fr" || string.IsNullOrEmpty(en)) return en;
        if (Exact.TryGetValue(en, out var fr)) return fr;
        foreach (var (rx, tpl) in Patterns)
        {
            var m = rx.Match(en);
            if (!m.Success) continue;
            string r = tpl;
            for (int i = 0; i < 4; i++)
                if (m.Groups["g" + i].Success) r = r.Replace("{" + i + "}", m.Groups["g" + i].Value);
            return r;
        }
        return en;
    }
}
