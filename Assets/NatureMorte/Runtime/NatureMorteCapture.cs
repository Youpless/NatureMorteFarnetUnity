// Nature morte « Vanité de Franz » — capture d'écran en mode Play.
// Placée automatiquement sur la caméra par Nature Morte > Construire la scène.
using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace NatureMorte
{
    [DisallowMultipleComponent]
    public class NatureMorteCapture : MonoBehaviour
    {
        [Tooltip("Prend une capture automatiquement après le lancement du mode Play.")]
        public bool captureAuLancement = true;

        [Tooltip("Images à attendre avant la capture : laisse l'anticrénelage temporel et l'éclairage indirect se stabiliser.")]
        [Min(1)] public int imagesAvantCapture = 120;

        [Tooltip("Multiplie la résolution de la vue Game (1 = taille de la vue Game). Règle la vue Game en 1920 x 1080.")]
        [Range(1, 4)] public int multiplicateur = 1;

        IEnumerator Start()
        {
            if (!captureAuLancement) yield break;
#if UNITY_EDITOR
            // L'éditeur compile certains shaders à la volée : attendre la fin, sinon des objets de remplacement seraient capturés.
            while (UnityEditor.ShaderUtil.anythingCompiling) yield return null;
#endif
            for (int i = 0; i < imagesAvantCapture; i++) yield return null;
            Capturer();
        }

        [ContextMenu("Capturer maintenant (mode Play)")]
        public void Capturer()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Nature morte] La capture se fait en mode Play : lance Play, puis recommence.");
                return;
            }
            // Toujours le même nom (celui qu'affiche le README du dépôt) ; la capture précédente est gardée, datée.
            string dossier = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Captures"));
            Directory.CreateDirectory(dossier);
            string chemin = Path.Combine(dossier, "nature_morte_unity.png");
            if (File.Exists(chemin))
            {
                string ancien = Path.Combine(dossier, "nature_morte_unity_" + File.GetLastWriteTime(chemin).ToString("yyyyMMdd_HHmmss") + ".png");
                try
                {
                    if (!File.Exists(ancien)) File.Move(chemin, ancien);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Nature morte] Ancienne capture non renommée : " + e.Message);
                }
            }
            ScreenCapture.CaptureScreenshot(chemin, multiplicateur);
            Debug.Log("[Nature morte] Capture enregistrée : " + chemin);
        }
    }
}
