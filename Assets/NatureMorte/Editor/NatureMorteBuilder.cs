// Nature morte « Vanité de Franz » — La Bibliothèque infinie
// Construit la scène dans Unity 6 (HDRP ou URP, détection automatique) à partir de deux fichiers communs avec Unreal :
//   Assets/NatureMorte/Source/nature_morte.gltf (+ .bin) : géométrie, positions des lumières, caméra
//   Assets/NatureMorte/Source/parametres_rendu.json      : matériaux, lumières, exposition, post-process
// Menu : Nature Morte > Construire la scène
//
// Le script n'a aucune dépendance de compilation vers HDRP ou URP : les types propres aux pipelines (volumes,
// données additionnelles des lumières et caméras) sont atteints par réflexion. Il compile donc dans tout projet Unity 6.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureMorte.Editeur
{
    public static class NatureMorteBuilder
    {
        const string Racine = "Assets/NatureMorte";
        const string DossierSource = Racine + "/Source";
        const string DossierGenere = Racine + "/Genere";
        const string DossierMaillages = DossierGenere + "/Maillages";
        const string DossierMateriaux = DossierGenere + "/Materiaux";
        const string CheminProfil = DossierGenere + "/NM_Volume_PostProcess.asset";
        const string CheminScene = Racine + "/Scenes/NatureMorte.unity";
        const string FichierGltf = "nature_morte.gltf";
        const string FichierParams = "parametres_rendu.json";
        const string Titre = "Nature morte";

        enum Pipeline { Aucun, HDRP, URP }
        enum TypeTex { Couleur, CouleurAlpha, Lineaire, Normale }

        static Pipeline pipe = Pipeline.Aucun;
        static readonly List<string> avertissements = new List<string>();
        static float ev100 = 1f;
        static float echelleExpo = 0.5f; // 2^-EV100, convention Unreal (1 cd/m² vaut 1 à EV100 = 0)
        static Material materiauSecours;

        // ------------------------------------------------------------------ menus
        [MenuItem("Nature Morte/Construire la scène", false, 1)]
        public static void Construire()
        {
            avertissements.Clear();
            pipe = DetecterPipeline();
            if (pipe == Pipeline.Aucun)
            {
                EditorUtility.DisplayDialog(Titre, "Ce projet n'utilise ni HDRP ni URP.\n\nCrée le projet avec le modèle « High Definition 3D » (conseillé) ou « Universal 3D », puis relance la commande.", "OK");
                return;
            }
            if (!VerifierEmplacement()) return;
            string dossier = Path.Combine(Path.GetDirectoryName(Application.dataPath), DossierSource);
            string cheminGltf = Path.Combine(dossier, FichierGltf);
            string cheminParams = Path.Combine(dossier, FichierParams);
            if (!File.Exists(cheminGltf) || !File.Exists(cheminParams))
            {
                EditorUtility.DisplayDialog(Titre, "Fichiers introuvables. Ils doivent se trouver dans :\n" + DossierSource + "/" + FichierGltf + "\n" + DossierSource + "/" + FichierParams, "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Camera camera = null;
            int nbObjets = 0, nbLumieres = 0, nbMateriaux = 0;
            try
            {
                Progression("Lecture des fichiers", 0.02f);
                var prm = Json.Obj(Json.Parse(File.ReadAllText(cheminParams, Encoding.UTF8)));
                var gltf = new Gltf(cheminGltf);
                ev100 = Json.F(Json.Get(Json.Obj(Json.Get(prm, "exposition")), "ev100"), 1f);
                echelleExpo = Mathf.Pow(2f, -ev100);

                AssurerDossier(DossierMaillages);
                AssurerDossier(DossierMateriaux);
                AssurerDossier(Racine + "/Scenes");

                var materiaux = CreerMateriaux(Json.Obj(Json.Get(prm, "materiaux")));
                nbMateriaux = materiaux.Count;

                Progression("Nouvelle scène", 0.45f);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var racine = new GameObject("Nature_Morte_Vanite_de_Franz").transform;

                nbObjets = ConstruireGeometrie(gltf, materiaux, prm, racine);
                Progression("Lumières", 0.8f);
                nbLumieres = ConstruireLumieres(gltf, prm, racine);
                Progression("Caméra", 0.84f);
                camera = ConstruireCamera(gltf, prm, racine);
                Progression("Post-process", 0.88f);
                ConstruireVolume(prm, racine);
                VerifierQualite();
                ConstruireSondeReflets(prm, racine);
                ReglerEnvironnement(prm);

                Progression("Enregistrement", 0.96f);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, CheminScene);
                AssetDatabase.SaveAssets();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog(Titre, "La construction a échoué :\n" + e.Message + "\n\nDétails dans la Console.", "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (camera != null)
            {
                Selection.activeGameObject = camera.gameObject;
                var vue = SceneView.lastActiveSceneView;
                if (vue != null) vue.AlignViewToObject(camera.transform);
            }
            var resume = new StringBuilder();
            resume.AppendLine("Scène construite pour " + (pipe == Pipeline.HDRP ? "HDRP" : "URP") + " : " + CheminScene);
            resume.AppendLine(nbObjets + " objets, " + nbMateriaux + " matériaux, " + nbLumieres + " lumières.");
            if (rangements.Count > 0) resume.AppendLine("Remis en place : " + string.Join(", ", rangements) + ".");
            resume.AppendLine("Exposition : EV100 " + ev100.ToString("0.##", CultureInfo.InvariantCulture) + (pipe == Pipeline.URP ? " (intégrée aux intensités URP)" : ""));
            resume.AppendLine();
            resume.AppendLine("Règle la vue Game en 16:9 (1920 x 1080), puis lance le mode Play : une capture est enregistrée dans le dossier Captures du projet.");
            if (avertissements.Count > 0)
            {
                resume.AppendLine();
                resume.AppendLine(avertissements.Count + " remarque(s), détaillées dans la Console.");
            }
            Debug.Log("[Nature morte] " + resume);
            EditorUtility.DisplayDialog(Titre, resume.ToString(), "OK");
        }

        [MenuItem("Nature Morte/Ouvrir le dossier des captures", false, 20)]
        public static void OuvrirCaptures()
        {
            string dossier = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Captures"));
            Directory.CreateDirectory(dossier);
            EditorUtility.RevealInFinder(dossier);
        }

        // ------------------------------------------------------------------ emplacement des fichiers
        // Erreur fréquente : copier le contenu de NatureMorte_Unity DANS le dossier Assets du projet au lieu de sa racine.
        // On obtient Assets/Assets/NatureMorte, Assets/Captures, Assets/README.md... Le script retrouve les fichiers et
        // propose de les remettre en place.
        static readonly List<string> rangements = new List<string>();

        static bool VerifierEmplacement()
        {
            rangements.Clear();
            string assets = Application.dataPath.Replace('\\', '/').TrimEnd('/');
            string projet = Path.GetDirectoryName(assets).Replace('\\', '/');
            bool sourceOk = SourcePresente(projet + "/" + DossierSource);
            string deplacerDe = null, deplacerVers = null, dossierQuitte = null;
            if (!sourceOk)
            {
                string trouve = null;
                try
                {
                    foreach (var f in Directory.GetFiles(assets, FichierParams, SearchOption.AllDirectories))
                    {
                        string d = Path.GetDirectoryName(f).Replace('\\', '/');
                        if (SourcePresente(d)) { trouve = d; break; }
                    }
                }
                catch (Exception e) { Avert("Recherche des fichiers source : " + e.Message); }
                if (trouve == null || !trouve.StartsWith(assets + "/"))
                {
                    EditorUtility.DisplayDialog(Titre, "Fichiers introuvables. Copie le dossier NatureMorte de l'archive (NatureMorte_Unity/Assets/NatureMorte) dans le dossier Assets du projet, pour obtenir :\n"
                        + DossierSource + "/" + FichierGltf + "\n" + DossierSource + "/" + FichierParams, "OK");
                    return false;
                }
                string rel = "Assets" + trouve.Substring(assets.Length); // Assets/.../NatureMorte/Source
                string parent = Parent(rel);
                if (!AssetDatabase.IsValidFolder(Racine) && parent != null && Path.GetFileName(parent) == "NatureMorte")
                {
                    deplacerDe = parent;
                    deplacerVers = Racine;
                }
                else if (!AssetDatabase.IsValidFolder(DossierSource))
                {
                    deplacerDe = rel;
                    deplacerVers = DossierSource;
                }
                else
                {
                    EditorUtility.DisplayDialog(Titre, "Les fichiers source sont dans « " + rel + " », mais « " + DossierSource + " » existe déjà sans eux. Remplace ce dossier par celui de l'archive, puis relance la commande.", "OK");
                    return false;
                }
                dossierQuitte = Parent(deplacerDe);
            }

            // README.md, .gitignore, .gitattributes et Captures vont à la racine du projet (celle du dépôt GitHub), pas dans Assets.
            var horsAssets = new List<KeyValuePair<string, string>>();
            var dossiers = new List<string> { assets };
            for (string d = dossierQuitte; d != null && d.StartsWith("Assets/"); d = Parent(d))
                dossiers.Add(projet + "/" + d);
            foreach (string d in dossiers)
            {
                foreach (string nom in new[] { "README.md", ".gitignore", ".gitattributes" })
                {
                    string src = d + "/" + nom, dst = projet + "/" + nom;
                    if (!File.Exists(src) || !FichierDuProjet(src) || horsAssets.Exists(x => x.Value == dst)) continue;
                    if (File.Exists(dst)) Avert(nom + " existe déjà à la racine du projet : celui de la nature morte est resté dans " + Relatif(projet, d) + ".");
                    else horsAssets.Add(new KeyValuePair<string, string>(src, dst));
                }
                string cap = d + "/Captures", capDst = projet + "/Captures";
                if (Directory.Exists(cap) && !Directory.Exists(capDst) && !horsAssets.Exists(x => x.Value == capDst) && File.Exists(cap + "/LISEZ-MOI.txt"))
                    horsAssets.Add(new KeyValuePair<string, string>(cap, capDst));
            }
            if (deplacerDe == null && horsAssets.Count == 0) return true;

            var msg = new StringBuilder();
            msg.AppendLine("Des fichiers de la nature morte ne sont pas à leur place.");
            if (horsAssets.Count > 0 || (dossierQuitte != null && (dossierQuitte.EndsWith("/Assets") || dossierQuitte.Contains("NatureMorte_Unity"))))
                msg.AppendLine("Le contenu de NatureMorte_Unity a sans doute été copié dans le dossier Assets du projet, au lieu de sa racine.");
            msg.AppendLine();
            if (deplacerDe != null) msg.AppendLine("• " + deplacerDe + "  →  " + deplacerVers);
            foreach (var kv in horsAssets) msg.AppendLine("• " + Relatif(projet, kv.Key) + "  →  " + Relatif(projet, kv.Value));
            msg.AppendLine();
            msg.Append("Les remettre en place maintenant ?");
            if (!EditorUtility.DisplayDialog(Titre, msg.ToString(), "Ranger", "Annuler"))
            {
                if (!sourceOk) EditorUtility.DisplayDialog(Titre, "Construction annulée : le script a besoin du dossier " + DossierSource + ".", "OK");
                return sourceOk;
            }

            if (deplacerDe != null)
            {
                if (deplacerVers == DossierSource) AssurerDossier(Racine);
                string erreur = AssetDatabase.MoveAsset(deplacerDe, deplacerVers);
                if (!string.IsNullOrEmpty(erreur))
                {
                    EditorUtility.DisplayDialog(Titre, "Déplacement impossible : " + erreur + "\n\nDéplace le dossier à la main dans la fenêtre Project, puis relance la commande.", "OK");
                    return false;
                }
                rangements.Add(deplacerVers);
            }
            foreach (var kv in horsAssets)
            {
                try
                {
                    if (Directory.Exists(kv.Key)) Directory.Move(kv.Key, kv.Value);
                    else File.Move(kv.Key, kv.Value);
                    if (File.Exists(kv.Key + ".meta")) File.Delete(kv.Key + ".meta");
                    rangements.Add(Relatif(projet, kv.Value));
                }
                catch (Exception e)
                {
                    Avert("Impossible de déplacer " + Relatif(projet, kv.Key) + " : " + e.Message);
                }
            }
            AssetDatabase.Refresh();
            // dossiers devenus vides, par exemple Assets/Assets
            for (string d = dossierQuitte; d != null && d.StartsWith("Assets/"); d = Parent(d))
            {
                if (!DossierVide(projet + "/" + d)) break;
                AssetDatabase.DeleteAsset(d);
            }
            return SourcePresente(projet + "/" + DossierSource);
        }

        static bool SourcePresente(string dossier)
        {
            return File.Exists(Path.Combine(dossier, FichierGltf)) && File.Exists(Path.Combine(dossier, FichierParams));
        }

        static string Parent(string chemin)
        {
            int i = chemin.LastIndexOf('/');
            return i > 0 ? chemin.Substring(0, i) : null;
        }

        static string Relatif(string projet, string chemin)
        {
            chemin = chemin.Replace('\\', '/');
            return chemin.StartsWith(projet + "/") ? chemin.Substring(projet.Length + 1) : chemin;
        }

        // Ne déplace que nos propres fichiers, jamais un README ou un .gitignore appartenant à autre chose.
        static bool FichierDuProjet(string chemin)
        {
            try
            {
                string texte = File.ReadAllText(chemin);
                return texte.Contains("Vanité de Franz") || texte.Contains("[Ll]ibrary") || texte.Contains("Force Text") || texte.Contains("LightingData.asset");
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool DossierVide(string dossier)
        {
            if (!Directory.Exists(dossier)) return false;
            foreach (var e in Directory.GetFileSystemEntries(dossier))
            {
                string nom = Path.GetFileName(e);
                if (nom.StartsWith(".") || nom.EndsWith(".meta")) continue;
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ utilitaires
        static Pipeline DetecterPipeline()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return Pipeline.Aucun;
            string nom = rp.GetType().FullName ?? "";
            if (nom.Contains("HighDefinition")) return Pipeline.HDRP;
            if (nom.Contains("Universal")) return Pipeline.URP;
            return Pipeline.Aucun;
        }

        static void Progression(string etape, float t)
        {
            EditorUtility.DisplayProgressBar(Titre, etape, Mathf.Clamp01(t));
        }

        // Signale les réglages de qualité du pipeline qui empêcheraient d'obtenir l'image prévue.
        static void VerifierQualite()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return;
            if (pipe == Pipeline.HDRP)
            {
                var reglages = LireMembre(asset, "currentPlatformRenderPipelineSettings");
                if (reglages != null && LireMembre(reglages, "supportSSGI") is bool ssgi && !ssgi)
                    Avert("L'asset HDRP « " + asset.name + " » n'autorise pas le SSGI : coche Lighting > Screen Space Global Illumination dans cet asset (LISEZ-MOI, section 3, étape 3), sinon il n'y aura pas d'éclairage indirect.");
                return;
            }
            if (LireMembre(asset, "supportsHDR") is bool hdr && !hdr)
                Avert("L'asset URP « " + asset.name + " » n'a pas le HDR activé : coche Quality > HDR, sinon les flammes saturent et le bloom disparaît.");
            if (LireMembre(asset, "supportsAdditionalLightShadows") is bool ombres && !ombres)
                Avert("L'asset URP « " + asset.name + " » n'active pas les ombres des lumières additionnelles : coche Lighting > Additional Lights > Cast Shadows, sinon les flammes ne projettent pas d'ombre.");
        }

        static void Avert(string message)
        {
            avertissements.Add(message);
            Debug.LogWarning("[Nature morte] " + message);
        }

        static void AssurerDossier(string chemin)
        {
            if (AssetDatabase.IsValidFolder(chemin)) return;
            string parent = Path.GetDirectoryName(chemin).Replace('\\', '/');
            AssurerDossier(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(chemin));
        }

        // Unity attend les couleurs « non HDR » (matériaux, lumières) en sRVB ; nos valeurs sont linéaires.
        static Color Gamma(Color lineaire)
        {
            return lineaire.gamma;
        }

        static Type TrouverType(string nomComplet)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(nomComplet, false); } catch { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        static object Convertir(object valeur, Type cible)
        {
            if (valeur == null) return null;
            if (cible.IsInstanceOfType(valeur)) return valeur;
            if (cible.IsEnum)
            {
                var texte = valeur as string;
                if (texte != null) return Enum.Parse(cible, texte);
                return Enum.ToObject(cible, Convert.ToInt32(valeur, CultureInfo.InvariantCulture));
            }
            if (cible == typeof(float)) return Convert.ToSingle(valeur, CultureInfo.InvariantCulture);
            if (cible == typeof(int)) return Convert.ToInt32(valeur, CultureInfo.InvariantCulture);
            if (cible == typeof(bool)) return Convert.ToBoolean(valeur, CultureInfo.InvariantCulture);
            return valeur;
        }

        static bool DefinirMembre(object cible, string nom, object valeur)
        {
            if (cible == null) return false;
            var t = cible.GetType();
            try
            {
                var p = t.GetProperty(nom, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(cible, Convertir(valeur, p.PropertyType));
                    return true;
                }
                var f = t.GetField(nom, BindingFlags.Public | BindingFlags.Instance);
                if (f != null)
                {
                    f.SetValue(cible, Convertir(valeur, f.FieldType));
                    return true;
                }
            }
            catch (Exception e)
            {
                Avert(t.Name + "." + nom + " : " + e.Message);
                return false;
            }
            Avert("Réglage absent dans cette version : " + t.Name + "." + nom);
            return false;
        }

        static object LireMembre(object cible, string nom)
        {
            if (cible == null) return null;
            var t = cible.GetType();
            try
            {
                var p = t.GetProperty(nom, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanRead) return p.GetValue(cible);
                var f = t.GetField(nom, BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(cible);
            }
            catch (Exception) { }
            return null;
        }

        static bool Appeler(object cible, string methode, params object[] args)
        {
            if (cible == null) return false;
            foreach (var m in cible.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != methode) continue;
                var ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                try
                {
                    var conv = new object[args.Length];
                    for (int i = 0; i < args.Length; i++) conv[i] = Convertir(args[i], ps[i].ParameterType);
                    m.Invoke(cible, conv);
                    return true;
                }
                catch (Exception e)
                {
                    Avert(cible.GetType().Name + "." + methode + " : " + e.Message);
                    return false;
                }
            }
            Avert("Méthode absente dans cette version : " + cible.GetType().Name + "." + methode);
            return false;
        }

        static Component AjouterComposant(GameObject go, string nomType)
        {
            var t = TrouverType(nomType);
            if (t == null) return null;
            var c = go.GetComponent(t);
            if (c == null) c = go.AddComponent(t);
            return c;
        }

        static void AppliquerTransform(Dictionary<string, object> noeud, Transform t)
        {
            // glTF (main droite) vers Unity (main gauche) : on retourne l'axe Z.
            // Position (x, y, -z) ; rotation (-x, -y, z, w). Les caméras et lumières glTF visent -Z, celles d'Unity +Z :
            // ce retournement les fait viser au même endroit sans rotation supplémentaire.
            var tr = Json.Arr(Json.Get(noeud, "translation"));
            var rot = Json.Arr(Json.Get(noeud, "rotation"));
            var ech = Json.Arr(Json.Get(noeud, "scale"));
            if (tr != null && tr.Count >= 3) t.localPosition = new Vector3(Json.F(tr[0]), Json.F(tr[1]), -Json.F(tr[2]));
            if (rot != null && rot.Count >= 4) t.localRotation = new Quaternion(-Json.F(rot[0]), -Json.F(rot[1]), Json.F(rot[2]), Json.F(rot[3]));
            if (ech != null && ech.Count >= 3) t.localScale = new Vector3(Json.F(ech[0]), Json.F(ech[1]), Json.F(ech[2]));
        }

        // ------------------------------------------------------------------ textures
        static Texture2D Texture(Dictionary<string, object> def, string cle, TypeTex type)
        {
            string rel = Json.S(Json.Get(def, cle));
            if (string.IsNullOrEmpty(rel)) return null;
            string chemin = DossierSource + "/" + rel;
            var imp = AssetImporter.GetAtPath(chemin) as TextureImporter;
            if (imp == null)
            {
                Avert("Texture introuvable : " + chemin);
                return null;
            }
            bool modif = false;
            if (type == TypeTex.Normale)
            {
                if (imp.textureType != TextureImporterType.NormalMap) { imp.textureType = TextureImporterType.NormalMap; modif = true; }
            }
            else
            {
                if (imp.textureType != TextureImporterType.Default) { imp.textureType = TextureImporterType.Default; modif = true; }
                bool srgb = type != TypeTex.Lineaire;
                if (imp.sRGBTexture != srgb) { imp.sRGBTexture = srgb; modif = true; }
                if (type == TypeTex.CouleurAlpha && !imp.alphaIsTransparency) { imp.alphaIsTransparency = true; modif = true; }
            }
            if (!imp.mipmapEnabled) { imp.mipmapEnabled = true; modif = true; }
            if (imp.wrapMode != TextureWrapMode.Repeat) { imp.wrapMode = TextureWrapMode.Repeat; modif = true; }
            if (imp.anisoLevel < 8) { imp.anisoLevel = 8; modif = true; }
            if (modif) imp.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(chemin);
            if (tex == null) Avert("Texture non chargée : " + chemin);
            return tex;
        }

        // ------------------------------------------------------------------ matériaux
        static Dictionary<string, Material> CreerMateriaux(Dictionary<string, object> defs)
        {
            var res = new Dictionary<string, Material>();
            if (defs == null) throw new InvalidDataException("Section « materiaux » absente de " + FichierParams);
            var shader = Shader.Find(pipe == Pipeline.HDRP ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("Shader Lit introuvable pour " + pipe);
            materiauSecours = new Material(shader) { name = "NM_Secours" };
            int i = 0;
            foreach (var kv in defs)
            {
                Progression("Matériau " + kv.Key, 0.05f + 0.38f * i++ / Mathf.Max(1, defs.Count));
                var def = Json.Obj(kv.Value);
                string chemin = DossierMateriaux + "/" + kv.Key + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(chemin);
                bool nouveau = mat == null;
                if (nouveau) mat = new Material(shader) { name = kv.Key };
                else if (mat.shader != shader) mat.shader = shader;

                if (pipe == Pipeline.HDRP) ReglerMateriauHDRP(mat, def);
                else ReglerMateriauURP(mat, def);

                if (nouveau) AssetDatabase.CreateAsset(mat, chemin);
                // Les post-processeurs HDRP/URP initialisent les nouveaux matériaux ; on revalide après coup.
                if (pipe == Pipeline.HDRP) ValiderHDRP(mat);
                else ValiderURP(mat);
                EditorUtility.SetDirty(mat);
                res[kv.Key] = mat;
            }
            AssetDatabase.SaveAssets();
            return res;
        }

        struct DefMateriau
        {
            public string type, alpha;
            public bool transparent, additif, masque, doubleFace, occlusion;
            public float seuil, metal, rugosite, opacite;
            public Color couleur;
            public Texture2D tCouleur, tMasque, tNormale, tEmissif;
            public bool emissif;
            public Color couleurEmissive; // linéaire, déjà multipliée par l'intensité en nits
        }

        static DefMateriau Lire(Dictionary<string, object> d)
        {
            var m = new DefMateriau();
            m.type = Json.S(Json.Get(d, "type")) ?? "standard";
            m.alpha = Json.S(Json.Get(d, "alpha")) ?? "opaque";
            m.additif = m.alpha == "additif";
            m.transparent = m.alpha == "transparent" || m.additif;
            m.masque = m.alpha == "masque";
            m.doubleFace = Json.B(Json.Get(d, "double_face"), false);
            m.occlusion = Json.B(Json.Get(d, "occlusion"), true);
            m.seuil = Json.F(Json.Get(d, "seuil_alpha"), 0.5f);
            m.metal = Json.F(Json.Get(d, "metal"), 0f);
            m.rugosite = Json.F(Json.Get(d, "rugosite"), 0.5f);
            m.couleur = Json.Couleur(Json.Get(d, "couleur"), Color.white);
            m.opacite = Json.F(Json.Get(d, "opacite"), m.couleur.a);
            m.tCouleur = Texture(d, "texture_couleur", m.masque ? TypeTex.CouleurAlpha : TypeTex.Couleur);
            m.tMasque = Texture(d, "texture_masque_unity", TypeTex.Lineaire);
            m.tNormale = Texture(d, "texture_normale", TypeTex.Normale);
            var em = Json.Obj(Json.Get(d, "emissif"));
            m.emissif = em != null;
            if (m.emissif)
            {
                var c = Json.Couleur(Json.Get(em, "couleur"), Color.white);
                float nits = Json.F(Json.Get(em, "intensite_nits"), 1f);
                m.couleurEmissive = new Color(c.r * nits, c.g * nits, c.b * nits, 1f);
                m.tEmissif = Texture(em, "texture", TypeTex.Couleur);
            }
            if (m.type == "verre") m.metal = 0f;
            if (m.additif)
            {
                m.couleur = new Color(0f, 0f, 0f, 1f);
                m.metal = 0f;
                m.rugosite = 1f;
            }
            if (m.type == "verre") m.couleur.a = m.opacite;
            return m;
        }

        static void ReglerMateriauHDRP(Material mat, Dictionary<string, object> d)
        {
            var m = Lire(d);
            mat.SetFloat("_SurfaceType", m.transparent ? 1f : 0f);
            mat.SetFloat("_BlendMode", m.additif ? 1f : 0f); // Alpha = 0, Additive = 1
            mat.SetFloat("_EnableBlendModePreserveSpecularLighting", 1f);
            mat.SetFloat("_EnableFogOnTransparent", 1f);
            mat.SetFloat("_AlphaCutoffEnable", m.masque ? 1f : 0f);
            mat.SetFloat("_AlphaCutoff", m.seuil);
            mat.SetFloat("_Cutoff", m.seuil);
            mat.SetFloat("_DoubleSidedEnable", m.doubleFace ? 1f : 0f);
            mat.SetFloat("_DoubleSidedNormalMode", 0f); // Flip, comme les matériaux deux faces d'Unreal
            mat.SetFloat("_ReceivesSSRTransparent", m.type == "verre" ? 1f : 0f);
            // Verre double face : faces arrière puis faces avant (« Back Then Front Rendering »), pour un tri stable.
            mat.SetFloat("_TransparentBackfaceEnable", m.type == "verre" && m.doubleFace ? 1f : 0f);

            mat.SetColor("_BaseColor", Gamma(m.couleur));
            mat.SetTexture("_BaseColorMap", m.tCouleur);

            mat.SetTexture("_MaskMap", m.tMasque);
            if (m.tMasque != null)
            {
                // MaskMap : R = métal, G = occlusion, B = masque de détail, A = lissé
                mat.SetFloat("_MetallicRemapMin", 0f);
                mat.SetFloat("_MetallicRemapMax", m.metal);
                mat.SetFloat("_SmoothnessRemapMin", 0f);
                mat.SetFloat("_SmoothnessRemapMax", 1f);
                mat.SetFloat("_AORemapMin", m.occlusion ? 0f : 1f);
                mat.SetFloat("_AORemapMax", 1f);
            }
            mat.SetFloat("_Metallic", m.metal);
            mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - m.rugosite));

            mat.SetTexture("_NormalMap", m.tNormale);
            mat.SetFloat("_NormalScale", 1f);

            mat.SetFloat("_UseEmissiveIntensity", 0f);
            mat.SetFloat("_EmissiveExposureWeight", 1f); // émission en nits, soumise à l'exposition comme dans Unreal
            mat.SetColor("_EmissiveColor", m.emissif ? m.couleurEmissive : Color.black);
            mat.SetTexture("_EmissiveColorMap", m.emissif ? m.tEmissif : null);
        }

        static void ValiderHDRP(Material mat)
        {
            var t = TrouverType("UnityEngine.Rendering.HighDefinition.HDMaterial");
            var mi = t != null ? t.GetMethod("ValidateMaterial", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null) : null;
            if (mi != null)
            {
                try { mi.Invoke(null, new object[] { mat }); return; }
                catch (Exception e) { Avert("Validation HDRP de " + mat.name + " : " + e.Message); }
            }
            var t2 = TrouverType("UnityEditor.Rendering.HighDefinition.HDShaderUtils");
            var mi2 = t2 != null ? t2.GetMethod("ResetMaterialKeywords", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null) : null;
            if (mi2 != null)
            {
                try { mi2.Invoke(null, new object[] { mat }); return; }
                catch (Exception e) { Avert("Mots-clés HDRP de " + mat.name + " : " + e.Message); }
            }
            Avert("Validation HDRP indisponible pour " + mat.name + " : sélectionne le matériau une fois dans le Project pour que HDRP le mette à jour.");
        }

        static void ReglerMateriauURP(Material mat, Dictionary<string, object> d)
        {
            var m = Lire(d);
            mat.SetFloat("_WorkflowMode", 1f); // Metallic
            mat.SetFloat("_Surface", m.transparent ? 1f : 0f);
            mat.SetFloat("_Blend", m.additif ? 2f : 0f); // Alpha = 0, Additive = 2
            mat.SetFloat("_BlendModePreserveSpecular", 1f);
            mat.SetFloat("_AlphaClip", m.masque ? 1f : 0f);
            mat.SetFloat("_Cutoff", m.seuil);
            mat.SetFloat("_Cull", m.doubleFace ? 0f : 2f); // Off = 0, Back = 2
            mat.SetFloat("_ReceiveShadows", 1f);

            mat.SetColor("_BaseColor", Gamma(m.couleur));
            mat.SetTexture("_BaseMap", m.tCouleur);

            if (m.tMasque != null)
            {
                // même texture que la MaskMap HDRP : R = métal, G = occlusion, A = lissé
                mat.SetTexture("_MetallicGlossMap", m.tMasque);
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetTexture("_OcclusionMap", m.occlusion ? m.tMasque : null);
                mat.SetFloat("_OcclusionStrength", 1f);
            }
            else
            {
                mat.SetTexture("_MetallicGlossMap", null);
                mat.SetTexture("_OcclusionMap", null);
                mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - m.rugosite));
            }
            mat.SetFloat("_Metallic", m.metal);

            mat.SetTexture("_BumpMap", m.tNormale);
            mat.SetFloat("_BumpScale", 1f);

            if (m.emissif)
            {
                // URP n'a pas d'exposition physique : l'émission est convertie en valeur d'écran (nits x 2^-EV100).
                var c = m.couleurEmissive;
                mat.SetColor("_EmissionColor", new Color(c.r * echelleExpo, c.g * echelleExpo, c.b * echelleExpo, 1f));
                mat.SetTexture("_EmissionMap", m.tEmissif);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                mat.SetColor("_EmissionColor", Color.black);
                mat.SetTexture("_EmissionMap", null);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            EtatsURP(mat, m);
        }

        // Réglages de rendu « à la main », au cas où l'API éditeur d'URP ne serait pas joignable.
        static void EtatsURP(Material mat, DefMateriau m)
        {
            if (m.transparent)
            {
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetFloat("_SrcBlend", (float)BlendMode.One);
                mat.SetFloat("_DstBlend", m.additif ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                mat.SetFloat("_DstBlendAlpha", m.additif ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = (int)RenderQueue.Transparent;
                mat.SetShaderPassEnabled("ShadowCaster", false);
            }
            else
            {
                mat.SetOverrideTag("RenderType", m.masque ? "TransparentCutout" : "Opaque");
                mat.SetFloat("_SrcBlend", (float)BlendMode.One);
                mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
                mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                mat.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
                mat.SetFloat("_ZWrite", 1f);
                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = m.masque ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry;
                mat.SetShaderPassEnabled("ShadowCaster", true);
            }
            ActiverMotCle(mat, "_ALPHATEST_ON", m.masque);
            ActiverMotCle(mat, "_NORMALMAP", m.tNormale != null);
            ActiverMotCle(mat, "_METALLICSPECGLOSSMAP", m.tMasque != null);
            ActiverMotCle(mat, "_OCCLUSIONMAP", m.tMasque != null && m.occlusion);
            ActiverMotCle(mat, "_EMISSION", m.emissif);
        }

        static void ActiverMotCle(Material mat, string mot, bool actif)
        {
            if (actif) mat.EnableKeyword(mot);
            else mat.DisableKeyword(mot);
        }

        static void ValiderURP(Material mat)
        {
            var bsg = TrouverType("UnityEditor.BaseShaderGUI");
            var lit = TrouverType("UnityEditor.Rendering.Universal.ShaderGUI.LitGUI");
            var setKw = bsg != null ? bsg.GetMethod("SetMaterialKeywords", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material), typeof(Action<Material>), typeof(Action<Material>) }, null) : null;
            var litKw = lit != null ? lit.GetMethod("SetMaterialKeywords", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null) : null;
            if (setKw == null)
            {
                Avert("Mise à jour automatique des mots-clés URP indisponible pour " + mat.name + " : réglages manuels appliqués.");
                return;
            }
            try
            {
                Action<Material> modele = litKw != null ? (Action<Material>)Delegate.CreateDelegate(typeof(Action<Material>), litKw) : null;
                setKw.Invoke(null, new object[] { mat, modele, null });
            }
            catch (Exception e)
            {
                Avert("Mots-clés URP de " + mat.name + " : " + e.Message);
            }
        }

        // ------------------------------------------------------------------ géométrie
        static int ConstruireGeometrie(Gltf g, Dictionary<string, Material> materiaux, Dictionary<string, object> prm, Transform racine)
        {
            var sansOmbre = new HashSet<string>();
            var liste = Json.Arr(Json.Get(prm, "objets_sans_ombre"));
            if (liste != null) foreach (var o in liste) sansOmbre.Add(Json.S(o));
            var conteneur = new GameObject("Geometrie").transform;
            conteneur.SetParent(racine, false);
            var scenes = Json.Arr(Json.Get(g.Racine, "scenes"));
            var noeudsRacine = scenes != null && scenes.Count > 0 ? Json.Arr(Json.Get(Json.Obj(scenes[0]), "nodes")) : null;
            int compte = 0;
            if (noeudsRacine == null)
            {
                for (int i = 0; i < g.Noeuds.Count; i++) ConstruireNoeud(g, i, conteneur, materiaux, sansOmbre, ref compte);
            }
            else
            {
                foreach (var idx in noeudsRacine) ConstruireNoeud(g, Json.I(idx), conteneur, materiaux, sansOmbre, ref compte);
            }
            return compte;
        }

        static void ConstruireNoeud(Gltf g, int index, Transform parent, Dictionary<string, Material> materiaux, HashSet<string> sansOmbre, ref int compte)
        {
            var n = Json.Obj(g.Noeuds[index]);
            bool lumiereOuCamera = Json.Get(n, "camera") != null || Json.Get(Json.Obj(Json.Get(n, "extensions")), "KHR_lights_punctual") != null;
            var enfants = Json.Arr(Json.Get(n, "children"));
            if (lumiereOuCamera && enfants == null) return;
            string nom = Json.S(Json.Get(n, "name")) ?? ("Noeud_" + index);
            var go = new GameObject(nom);
            go.transform.SetParent(parent, false);
            AppliquerTransform(n, go.transform);
            if (!lumiereOuCamera && Json.Get(n, "mesh") != null)
            {
                Progression("Maillage " + nom, 0.45f + 0.33f * index / Mathf.Max(1, g.Noeuds.Count));
                List<string> nomsMat;
                var mesh = CreerMaillage(g, Json.I(Json.Get(n, "mesh")), "SM_" + nom, out nomsMat);
                mesh = SauverMaillage(mesh, DossierMaillages + "/SM_" + nom + ".asset");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var tableau = new Material[nomsMat.Count];
                for (int i = 0; i < nomsMat.Count; i++)
                {
                    Material mat;
                    if (nomsMat[i] != null && materiaux.TryGetValue(nomsMat[i], out mat)) tableau[i] = mat;
                    else
                    {
                        tableau[i] = materiauSecours;
                        Avert("Matériau sans réglages : " + nomsMat[i] + " (objet " + nom + ")");
                    }
                }
                mr.sharedMaterials = tableau;
                mr.shadowCastingMode = sansOmbre.Contains(nom) ? ShadowCastingMode.Off : ShadowCastingMode.On;
                mr.receiveShadows = true;
                compte++;
            }
            if (enfants != null)
                foreach (var e in enfants) ConstruireNoeud(g, Json.I(e), go.transform, materiaux, sansOmbre, ref compte);
        }

        static Mesh CreerMaillage(Gltf g, int indexMaillage, string nom, out List<string> nomsMat)
        {
            var def = Json.Obj(g.Maillages[indexMaillage]);
            var prims = Json.Arr(Json.Get(def, "primitives"));
            var pos = new List<Vector3>();
            var nor = new List<Vector3>();
            var uv = new List<Vector2>();
            var sousMaillages = new List<int[]>();
            nomsMat = new List<string>();
            foreach (var po in prims)
            {
                var p = Json.Obj(po);
                var att = Json.Obj(Json.Get(p, "attributes"));
                int comp;
                float[] P = g.Flottants(Json.I(Json.Get(att, "POSITION")), out comp);
                int nv = P.Length / 3;
                float[] N = Json.Get(att, "NORMAL") != null ? g.Flottants(Json.I(Json.Get(att, "NORMAL")), out comp) : null;
                float[] T = Json.Get(att, "TEXCOORD_0") != null ? g.Flottants(Json.I(Json.Get(att, "TEXCOORD_0")), out comp) : null;
                int b = pos.Count;
                for (int v = 0; v < nv; v++)
                {
                    pos.Add(new Vector3(P[3 * v], P[3 * v + 1], -P[3 * v + 2]));
                    nor.Add(N != null ? new Vector3(N[3 * v], N[3 * v + 1], -N[3 * v + 2]) : Vector3.up);
                    uv.Add(T != null ? new Vector2(T[2 * v], 1f - T[2 * v + 1]) : Vector2.zero);
                }
                int[] I;
                if (Json.Get(p, "indices") != null) I = g.Indices(Json.I(Json.Get(p, "indices")));
                else
                {
                    I = new int[nv];
                    for (int k = 0; k < nv; k++) I[k] = k;
                }
                // le retournement de Z inverse l'orientation des triangles : on échange deux sommets
                var tri = new int[I.Length - I.Length % 3];
                for (int t = 0; t + 2 < I.Length; t += 3)
                {
                    tri[t] = I[t] + b;
                    tri[t + 1] = I[t + 2] + b;
                    tri[t + 2] = I[t + 1] + b;
                }
                sousMaillages.Add(tri);
                int mi = Json.Get(p, "material") != null ? Json.I(Json.Get(p, "material")) : -1;
                nomsMat.Add(mi >= 0 && mi < g.Materiaux.Count ? Json.S(Json.Get(Json.Obj(g.Materiaux[mi]), "name")) : null);
            }
            var mesh = new Mesh();
            mesh.name = nom;
            mesh.indexFormat = pos.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(pos);
            mesh.SetNormals(nor);
            mesh.SetUVs(0, uv);
            mesh.subMeshCount = sousMaillages.Count;
            for (int s = 0; s < sousMaillages.Count; s++) mesh.SetTriangles(sousMaillages[s], s, false);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        static Mesh SauverMaillage(Mesh m, string chemin)
        {
            var existant = AssetDatabase.LoadAssetAtPath<Mesh>(chemin);
            if (existant != null)
            {
                EditorUtility.CopySerialized(m, existant);
                existant.name = m.name;
                EditorUtility.SetDirty(existant);
                UnityEngine.Object.DestroyImmediate(m);
                return existant;
            }
            AssetDatabase.CreateAsset(m, chemin);
            return m;
        }

        // ------------------------------------------------------------------ lumières
        static int ConstruireLumieres(Gltf g, Dictionary<string, object> prm, Transform racine)
        {
            var defs = Json.Obj(Json.Get(prm, "lumieres"));
            var conteneur = new GameObject("Lumieres").transform;
            conteneur.SetParent(racine, false);
            int compte = 0;
            for (int i = 0; i < g.Noeuds.Count; i++)
            {
                var n = Json.Obj(g.Noeuds[i]);
                var klp = Json.Obj(Json.Get(Json.Obj(Json.Get(n, "extensions")), "KHR_lights_punctual"));
                if (klp == null) continue;
                int li = Json.I(Json.Get(klp, "light"));
                if (li < 0 || li >= g.Lumieres.Count) continue;
                var lg = Json.Obj(g.Lumieres[li]);
                string nom = Json.S(Json.Get(n, "name")) ?? ("Lumiere_" + li);
                var d = Json.Obj(Json.Get(defs, nom));
                string typeG = Json.S(Json.Get(lg, "type")) ?? "point";
                var type = typeG == "directional" ? LightType.Directional : (typeG == "spot" ? LightType.Spot : LightType.Point);
                Color couleur = Json.Couleur(Json.Get(d, "couleur") ?? Json.Get(lg, "color"), Color.white);
                float valeur = type == LightType.Directional
                    ? Json.F(Json.Get(d, "lux"), Json.F(Json.Get(lg, "intensity"), 1f))
                    : Json.F(Json.Get(d, "candela"), Json.F(Json.Get(lg, "intensity"), 1f));
                float portee = Json.F(Json.Get(d, "portee_m"), Json.F(Json.Get(lg, "range"), 10f));
                bool ombres = Json.B(Json.Get(d, "ombres"), true);

                var light = CreerLumiere(nom, type, conteneur);
                AppliquerTransform(n, light.transform);
                light.color = Gamma(couleur);
                light.useColorTemperature = false;
                // HDRP : intensité en unité native (candela pour les points, lux pour la directionnelle).
                // URP : pas d'unités physiques ni de 1/pi dans son BRDF, d'où la conversion valeur x 2^-EV100 / pi.
                light.intensity = pipe == Pipeline.HDRP ? valeur : valeur * echelleExpo / Mathf.PI;
                light.lightUnit = type == LightType.Directional ? LightUnit.Lux : LightUnit.Candela;
                if (type != LightType.Directional)
                {
                    light.range = portee;
                    // URP : le plan proche des ombres ne descend jamais sous 1 % de la portée (et 10 cm au plus). Avec 8 à 10 m,
                    // les barreaux de la lanterne, à 3-9 cm de la flamme, ne projetteraient pas d'ombre. Une portée de 6 m ramène
                    // ce plan à 6 cm ; l'atténuation de bord d'URP n'enlève alors que 2,5 % de lumière à 2 m.
                    if (pipe == Pipeline.URP && ombres) light.range = Mathf.Min(portee, 6f);
                }
                light.shadows = ombres ? LightShadows.Soft : LightShadows.None;
                light.shadowStrength = 1f;
                light.shadowNearPlane = type == LightType.Directional ? 0.1f : Mathf.Clamp(0.01f * light.range, 0.01f, 0.1f);
                if (pipe == Pipeline.HDRP) ReglerLumiereHDRP(light, d, type, ombres);
                compte++;
            }
            return compte;
        }

        static Light CreerLumiere(string nom, LightType type, Transform parent)
        {
            var go = new GameObject(nom);
            go.transform.SetParent(parent, false);
            if (pipe == Pipeline.HDRP)
            {
                // AddHDLight ajoute Light + HDAdditionalLightData et applique les valeurs par défaut d'HDRP :
                // à faire AVANT de régler l'intensité, sinon HDRP les réécrit plus tard.
                var ext = TrouverType("UnityEngine.Rendering.HighDefinition.GameObjectExtension");
                var ajout = ext != null ? ext.GetMethod("AddHDLight", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject), typeof(LightType) }, null) : null;
                bool fait = false;
                if (ajout != null)
                {
                    try { ajout.Invoke(null, new object[] { go, type }); fait = true; }
                    catch (Exception e) { Avert("AddHDLight : " + e.Message); }
                }
                if (!fait) AjouterComposant(go, "UnityEngine.Rendering.HighDefinition.HDAdditionalLightData");
            }
            var light = go.GetComponent<Light>();
            if (light == null) light = go.AddComponent<Light>();
            light.type = type;
            return light;
        }

        static void ReglerLumiereHDRP(Light light, Dictionary<string, object> d, LightType type, bool ombres)
        {
            var hd = AjouterComposant(light.gameObject, "UnityEngine.Rendering.HighDefinition.HDAdditionalLightData");
            if (hd == null) return;
            if (type == LightType.Directional)
                DefinirMembre(hd, "angularDiameter", Json.F(Json.Get(d, "angle_source_deg"), 0.5f));
            else
                DefinirMembre(hd, "shapeRadius", Mathf.Max(0.001f, Json.F(Json.Get(d, "rayon_source_m"), 0.01f)));
            DefinirMembre(hd, "shadowNearPlane", 0.02f);
            DefinirMembre(hd, "volumetricDimmer", Json.F(Json.Get(d, "volumetrique"), 0f));
            if (ombres)
            {
                Appeler(hd, "SetShadowResolutionOverride", true);
                Appeler(hd, "SetShadowResolution", type == LightType.Directional ? 2048 : 1024);
            }
        }

        // ------------------------------------------------------------------ caméra
        static Camera ConstruireCamera(Gltf g, Dictionary<string, object> prm, Transform racine)
        {
            var def = Json.Obj(Json.Get(prm, "camera"));
            string nom = Json.S(Json.Get(def, "noeud")) ?? "Camera_Principale";
            Dictionary<string, object> noeud = null;
            foreach (var o in g.Noeuds)
            {
                var n = Json.Obj(o);
                if (Json.Get(n, "camera") == null) continue;
                if (noeud == null || Json.S(Json.Get(n, "name")) == nom) noeud = n;
            }
            var go = new GameObject(nom);
            go.transform.SetParent(racine, false);
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            if (noeud != null) AppliquerTransform(noeud, go.transform);
            else
            {
                var p = Json.Arr(Json.Get(def, "position"));
                var c = Json.Arr(Json.Get(def, "cible"));
                if (p != null && c != null)
                {
                    go.transform.position = new Vector3(Json.F(p[0]), Json.F(p[1]), -Json.F(p[2]));
                    go.transform.LookAt(new Vector3(Json.F(c[0]), Json.F(c[1]), -Json.F(c[2])));
                }
            }
            var capteur = Json.Arr(Json.Get(def, "capteur_mm"));
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 400f;
            cam.allowHDR = true;
            cam.usePhysicalProperties = true;
            cam.sensorSize = new Vector2(capteur != null ? Json.F(capteur[0], 36f) : 36f, capteur != null && capteur.Count > 1 ? Json.F(capteur[1], 20.25f) : 20.25f);
            cam.gateFit = Camera.GateFitMode.Horizontal;
            cam.focalLength = Json.F(Json.Get(def, "focale_mm"), 26.4f);
            cam.aperture = Json.F(Json.Get(def, "ouverture_f"), 2f);
            cam.focusDistance = Json.F(Json.Get(def, "mise_au_point_m"), 1.6f);
            cam.bladeCount = Json.I(Json.Get(def, "lames_diaphragme"), 7);
            cam.iso = 100;
            cam.shutterSpeed = 1f / 60f;

            if (pipe == Pipeline.HDRP)
            {
                var hd = AjouterComposant(go, "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                if (hd != null) DefinirMembre(hd, "antialiasing", 2); // TemporalAntialiasing
            }
            else
            {
                var ud = AjouterComposant(go, "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                if (ud != null)
                {
                    DefinirMembre(ud, "renderPostProcessing", true); // désactivé par défaut sur une caméra créée par script
                    DefinirMembre(ud, "antialiasing", 2); // SubpixelMorphologicalAntiAliasing (SMAA)
                }
            }
            go.AddComponent<AudioListener>();
            go.AddComponent<NatureMorte.NatureMorteCapture>();
            return cam;
        }

        // ------------------------------------------------------------------ post-process (volume global)
        static void ConstruireVolume(Dictionary<string, object> prm, Transform racine)
        {
            var typeVolume = TrouverType("UnityEngine.Rendering.Volume");
            var typeProfil = TrouverType("UnityEngine.Rendering.VolumeProfile");
            if (typeVolume == null || typeProfil == null)
            {
                Avert("Système de volumes introuvable : post-process non créé.");
                return;
            }
            if (AssetDatabase.LoadMainAssetAtPath(CheminProfil) != null) AssetDatabase.DeleteAsset(CheminProfil);
            var profil = ScriptableObject.CreateInstance(typeProfil);
            profil.name = "NM_Volume_PostProcess";
            AssetDatabase.CreateAsset(profil, CheminProfil);
            var ajout = typeProfil.GetMethod("Add", new[] { typeof(Type), typeof(bool) });

            Func<string, ScriptableObject> composant = nomType =>
            {
                var t = TrouverType(nomType);
                if (t == null || ajout == null)
                {
                    Avert("Effet absent de ce pipeline : " + nomType);
                    return null;
                }
                try
                {
                    var c = ajout.Invoke(profil, new object[] { t, false }) as ScriptableObject;
                    if (c != null) AssetDatabase.AddObjectToAsset(c, profil); // sinon l'effet disparaît au rechargement
                    return c;
                }
                catch (Exception e)
                {
                    Avert(nomType + " : " + e.Message);
                    return null;
                }
            };

            var post = Json.Obj(Json.Get(prm, "post"));
            var amb = Json.Obj(Json.Get(prm, "ambiance"));
            var cam = Json.Obj(Json.Get(prm, "camera"));
            if (pipe == Pipeline.HDRP) VolumeHDRP(composant, post, amb, cam);
            else VolumeURP(composant, post, cam);
            EditorUtility.SetDirty(profil);
            AssetDatabase.SaveAssets();

            var go = new GameObject("Volume_PostProcess");
            go.transform.SetParent(racine, false);
            var vol = go.AddComponent(typeVolume);
            DefinirMembre(vol, "isGlobal", true);
            DefinirMembre(vol, "priority", 10f);
            DefinirMembre(vol, "weight", 1f);
            DefinirMembre(vol, "sharedProfile", profil);
        }

        // Règle un paramètre de volume (champ de type VolumeParameter) et coche sa case « override ».
        static void P(object composant, string champ, object valeur)
        {
            if (composant == null) return;
            var f = composant.GetType().GetField(champ, BindingFlags.Public | BindingFlags.Instance);
            if (f == null)
            {
                Avert("Paramètre absent dans cette version : " + composant.GetType().Name + "." + champ);
                return;
            }
            var param = f.GetValue(composant);
            if (param == null) return;
            try
            {
                var pv = param.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
                var po = param.GetType().GetProperty("overrideState", BindingFlags.Public | BindingFlags.Instance);
                pv.SetValue(param, Convertir(valeur, pv.PropertyType));
                po.SetValue(param, true);
            }
            catch (Exception e)
            {
                Avert(composant.GetType().Name + "." + champ + " : " + e.Message);
            }
        }

        static float FacteurLentilleHDRP()
        {
            // HDRP : exposition = 1 / (78 / (100 q) x 2^EV100). Unreal utilise q = 0,78 (facteur 1) ; HDRP 0,65 par défaut (facteur 1,2).
            float q = 0.65f;
            var cu = TrouverType("UnityEngine.Rendering.ColorUtils");
            var f = cu != null ? cu.GetField("s_LensAttenuation", BindingFlags.Public | BindingFlags.Static) : null;
            if (f != null)
            {
                try { q = (float)f.GetValue(null); } catch (Exception) { q = 0.65f; }
            }
            if (q < 0.1f || q > 1f) q = 0.65f;
            return 78f / (100f * q);
        }

        static void VolumeHDRP(Func<string, ScriptableObject> c, Dictionary<string, object> post, Dictionary<string, object> amb, Dictionary<string, object> cam)
        {
            const string H = "UnityEngine.Rendering.HighDefinition.";
            float facteur = FacteurLentilleHDRP();
            var expo = c(H + "Exposure");
            P(expo, "mode", "Fixed");
            P(expo, "fixedExposure", ev100 - Mathf.Log(facteur, 2f));
            P(expo, "compensation", 0f); // HDRP retranche aussi la compensation en mode Fixed

            P(c(H + "Tonemapping"), "mode", "ACES");

            var b = Json.Obj(Json.Get(post, "bloom"));
            var bloom = c(H + "Bloom");
            P(bloom, "threshold", 0f);
            P(bloom, "intensity", Json.F(Json.Get(b, "hdrp_intensite"), 0.2f));
            P(bloom, "scatter", Json.F(Json.Get(b, "hdrp_dispersion"), 0.65f));

            var v = Json.Obj(Json.Get(post, "vignette"));
            var vig = c(H + "Vignette");
            P(vig, "color", Color.black);
            P(vig, "intensity", Json.F(Json.Get(v, "unity_intensite"), 0.3f));
            P(vig, "smoothness", Json.F(Json.Get(v, "unity_douceur"), 0.45f));

            var ca = c(H + "ColorAdjustments");
            P(ca, "postExposure", 0f);
            P(ca, "contrast", Json.F(Json.Get(Json.Obj(Json.Get(post, "contraste")), "unity"), 0f));
            P(ca, "saturation", Json.F(Json.Get(Json.Obj(Json.Get(post, "saturation")), "unity"), 0f));
            P(c(H + "WhiteBalance"), "temperature", Json.F(Json.Get(Json.Obj(Json.Get(post, "balance_blancs")), "unity_temperature"), 0f));

            var gr = c(H + "FilmGrain");
            P(gr, "type", "Thin1");
            P(gr, "intensity", Json.F(Json.Get(Json.Obj(Json.Get(post, "grain")), "unity_intensite"), 0.15f));
            P(c(H + "ChromaticAberration"), "intensity", Json.F(Json.Get(Json.Obj(Json.Get(post, "aberration_chromatique")), "unity_intensite"), 0f));

            if (Json.B(Json.Get(post, "profondeur_de_champ"), true))
            {
                var dof = c(H + "DepthOfField");
                P(dof, "focusMode", "UsePhysicalCamera");
                P(dof, "focusDistanceMode", "Volume");
                P(dof, "focusDistance", Json.F(Json.Get(cam, "mise_au_point_m"), 1.6f));
                P(dof, "quality", 2); // High
            }

            var br = Json.Obj(Json.Get(amb, "brouillard"));
            var fog = c(H + "Fog");
            P(fog, "enabled", true);
            P(fog, "colorMode", "ConstantColor");
            P(fog, "color", Json.Couleur(Json.Get(br, "couleur_nits"), new Color(0.11f, 0.14f, 0.2f)));
            P(fog, "meanFreePath", Json.F(Json.Get(br, "distance_attenuation_m"), 22f));
            // Sous « baseHeight », HDRP garde une densité constante : la placer au-dessus de toute la scène donne un brouillard
            // homogène, comme dans Unreal (chute de hauteur quasi nulle) et URP (brouillard exponentiel en distance).
            P(fog, "baseHeight", Json.F(Json.Get(br, "hdrp_hauteur_base_m"), 80f));
            P(fog, "maximumHeight", Json.F(Json.Get(br, "hdrp_hauteur_max_m"), 200f));
            P(fog, "enableVolumetricFog", false);

            var ciel = Json.Obj(Json.Get(amb, "ciel_ambiant_nits"));
            var ve = c(H + "VisualEnvironment");
            P(ve, "skyType", 3); // Gradient Sky
            P(ve, "skyAmbientMode", "Dynamic");
            var gs = c(H + "GradientSky");
            P(gs, "top", Json.Couleur(Json.Get(ciel, "haut"), new Color(0.035f, 0.045f, 0.07f)));
            P(gs, "middle", Json.Couleur(Json.Get(ciel, "milieu"), new Color(0.02f, 0.022f, 0.03f)));
            P(gs, "bottom", Json.Couleur(Json.Get(ciel, "bas"), new Color(0.01f, 0.008f, 0.006f)));
            P(gs, "skyIntensityMode", "Multiplier");
            P(gs, "multiplier", 1f);

            var ao = Json.Obj(Json.Get(post, "occlusion_ambiante"));
            var ssao = c(H + "ScreenSpaceAmbientOcclusion");
            P(ssao, "intensity", Json.F(Json.Get(ao, "hdrp_intensite"), 1f));
            P(ssao, "radius", Json.F(Json.Get(ao, "hdrp_rayon_m"), 0.6f));
            P(c(H + "ScreenSpaceReflection"), "enabled", true);
            var gi = c(H + "GlobalIllumination"); // lumière indirecte en espace écran, l'équivalent le plus proche de Lumen
            P(gi, "enable", true);
            P(gi, "quality", 1);
            P(c(H + "HDShadowSettings"), "maxShadowDistance", 25f);
            var ilc = c(H + "IndirectLightingController");
            P(ilc, "indirectDiffuseLightingMultiplier", 1f);
            P(ilc, "reflectionLightingMultiplier", 1f);
        }

        static void VolumeURP(Func<string, ScriptableObject> c, Dictionary<string, object> post, Dictionary<string, object> cam)
        {
            const string U = "UnityEngine.Rendering.Universal.";
            P(c(U + "Tonemapping"), "mode", "ACES");
            var b = Json.Obj(Json.Get(post, "bloom"));
            var bloom = c(U + "Bloom");
            P(bloom, "threshold", Json.F(Json.Get(b, "urp_seuil"), 0.9f));
            P(bloom, "intensity", Json.F(Json.Get(b, "urp_intensite"), 0.8f));
            P(bloom, "scatter", Json.F(Json.Get(b, "urp_dispersion"), 0.65f));
            P(bloom, "highQualityFiltering", true);
            var v = Json.Obj(Json.Get(post, "vignette"));
            var vig = c(U + "Vignette");
            P(vig, "color", Color.black);
            P(vig, "intensity", Json.F(Json.Get(v, "unity_intensite"), 0.3f));
            P(vig, "smoothness", Json.F(Json.Get(v, "unity_douceur"), 0.45f));
            var ca = c(U + "ColorAdjustments");
            P(ca, "postExposure", 0f); // l'exposition est déjà intégrée aux intensités URP
            P(ca, "contrast", Json.F(Json.Get(Json.Obj(Json.Get(post, "contraste")), "unity"), 0f));
            P(ca, "saturation", Json.F(Json.Get(Json.Obj(Json.Get(post, "saturation")), "unity"), 0f));
            P(c(U + "WhiteBalance"), "temperature", Json.F(Json.Get(Json.Obj(Json.Get(post, "balance_blancs")), "unity_temperature"), 0f));
            var gr = c(U + "FilmGrain");
            P(gr, "type", "Thin1");
            P(gr, "intensity", Json.F(Json.Get(Json.Obj(Json.Get(post, "grain")), "unity_intensite"), 0.15f));
            P(c(U + "ChromaticAberration"), "intensity", Json.F(Json.Get(Json.Obj(Json.Get(post, "aberration_chromatique")), "unity_intensite"), 0f));
            if (Json.B(Json.Get(post, "profondeur_de_champ"), true))
            {
                var dof = c(U + "DepthOfField");
                P(dof, "mode", "Bokeh");
                P(dof, "focusDistance", Json.F(Json.Get(cam, "mise_au_point_m"), 1.6f));
                P(dof, "aperture", Json.F(Json.Get(cam, "ouverture_f"), 2f));
                P(dof, "focalLength", Json.F(Json.Get(cam, "focale_mm"), 26.4f));
                P(dof, "bladeCount", Json.I(Json.Get(cam, "lames_diaphragme"), 7));
            }
        }

        // ------------------------------------------------------------------ reflets et ambiance
        static void ConstruireSondeReflets(Dictionary<string, object> prm, Transform racine)
        {
            var s = Json.Obj(Json.Get(Json.Obj(Json.Get(prm, "ambiance")), "sonde_reflet"));
            var centre = Json.Arr(Json.Get(s, "centre"));
            var taille = Json.Arr(Json.Get(s, "taille"));
            var go = new GameObject("Sonde_Reflets");
            go.transform.SetParent(racine, false);
            if (centre != null && centre.Count >= 3) go.transform.position = new Vector3(Json.F(centre[0]), Json.F(centre[1]), -Json.F(centre[2]));
            var rp = go.AddComponent<ReflectionProbe>();
            rp.mode = ReflectionProbeMode.Realtime;
            rp.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
            rp.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            rp.size = taille != null && taille.Count >= 3 ? new Vector3(Json.F(taille[0]), Json.F(taille[1]), Json.F(taille[2])) : new Vector3(3f, 2.2f, 2.6f);
            rp.center = Vector3.zero;
            rp.boxProjection = false;
            rp.resolution = 256;
            rp.intensity = 1f;
            rp.nearClipPlane = 0.05f;
            rp.hdr = true;
            if (pipe == Pipeline.HDRP)
            {
                // HDRP recopie ces réglages dans ses propres données ; on les force aussi explicitement.
                var hd = AjouterComposant(go, "UnityEngine.Rendering.HighDefinition.HDAdditionalReflectionData");
                if (hd != null)
                {
                    DefinirMembre(hd, "mode", 1);         // Realtime
                    DefinirMembre(hd, "realtimeMode", 0); // EveryFrame
                    var iv = LireMembre(hd, "influenceVolume");
                    if (iv != null)
                    {
                        DefinirMembre(iv, "shape", 0); // Box
                        DefinirMembre(iv, "boxSize", rp.size);
                    }
                }
            }
        }

        static void ReglerEnvironnement(Dictionary<string, object> prm)
        {
            var amb = Json.Obj(Json.Get(prm, "ambiance"));
            var ciel = Json.Obj(Json.Get(amb, "ciel_ambiant_nits"));
            var br = Json.Obj(Json.Get(amb, "brouillard"));
            RenderSettings.skybox = null;
            if (pipe != Pipeline.URP)
            {
                RenderSettings.fog = false; // HDRP utilise le brouillard du volume
                return;
            }
            float k = echelleExpo; // même conversion que pour l'émission
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Gamma(Echelle(Json.Couleur(Json.Get(ciel, "haut"), new Color(0.035f, 0.045f, 0.07f)), k));
            RenderSettings.ambientEquatorColor = Gamma(Echelle(Json.Couleur(Json.Get(ciel, "milieu"), new Color(0.02f, 0.022f, 0.03f)), k));
            RenderSettings.ambientGroundColor = Gamma(Echelle(Json.Couleur(Json.Get(ciel, "bas"), new Color(0.01f, 0.008f, 0.006f)), k));
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 1f / Mathf.Max(0.1f, Json.F(Json.Get(br, "distance_attenuation_m"), 22f));
            RenderSettings.fogColor = Gamma(Echelle(Json.Couleur(Json.Get(br, "couleur_nits"), new Color(0.11f, 0.14f, 0.2f)), k));
        }

        static Color Echelle(Color c, float k)
        {
            return new Color(c.r * k, c.g * k, c.b * k, 1f);
        }

        // ================================================================== lecture du glTF
        sealed class Gltf
        {
            public readonly Dictionary<string, object> Racine;
            public readonly List<object> Noeuds, Maillages, Materiaux, Accesseurs, Vues, Lumieres;
            readonly byte[] bin;

            public Gltf(string chemin)
            {
                Racine = Json.Obj(Json.Parse(File.ReadAllText(chemin, Encoding.UTF8)));
                if (Racine == null) throw new InvalidDataException("glTF illisible : " + chemin);
                Noeuds = Json.Arr(Json.Get(Racine, "nodes")) ?? new List<object>();
                Maillages = Json.Arr(Json.Get(Racine, "meshes")) ?? new List<object>();
                Materiaux = Json.Arr(Json.Get(Racine, "materials")) ?? new List<object>();
                Accesseurs = Json.Arr(Json.Get(Racine, "accessors")) ?? new List<object>();
                Vues = Json.Arr(Json.Get(Racine, "bufferViews")) ?? new List<object>();
                var ext = Json.Obj(Json.Get(Json.Obj(Json.Get(Racine, "extensions")), "KHR_lights_punctual"));
                Lumieres = Json.Arr(Json.Get(ext, "lights")) ?? new List<object>();
                var buffers = Json.Arr(Json.Get(Racine, "buffers"));
                if (buffers == null || buffers.Count == 0) throw new InvalidDataException("glTF sans buffer");
                string uri = Json.S(Json.Get(Json.Obj(buffers[0]), "uri"));
                if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.Ordinal)) throw new InvalidDataException("Le buffer doit être un fichier .bin à côté du .gltf");
                bin = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(chemin), Uri.UnescapeDataString(uri)));
            }

            public float[] Flottants(int accesseur, out int composantes)
            {
                var acc = Json.Obj(Accesseurs[accesseur]);
                int nombre = Json.I(Json.Get(acc, "count"));
                string type = Json.S(Json.Get(acc, "type"));
                composantes = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : 4;
                if (Json.I(Json.Get(acc, "componentType")) != 5126) throw new InvalidDataException("Accesseur " + accesseur + " : flottants attendus");
                var vue = Json.Obj(Vues[Json.I(Json.Get(acc, "bufferView"))]);
                int decalage = Json.I(Json.Get(vue, "byteOffset")) + Json.I(Json.Get(acc, "byteOffset"));
                int pas = Json.I(Json.Get(vue, "byteStride"));
                int taille = composantes * 4;
                var res = new float[nombre * composantes];
                if (pas == 0 || pas == taille) Buffer.BlockCopy(bin, decalage, res, 0, nombre * taille);
                else for (int i = 0; i < nombre; i++) Buffer.BlockCopy(bin, decalage + i * pas, res, i * taille, taille);
                return res;
            }

            public int[] Indices(int accesseur)
            {
                var acc = Json.Obj(Accesseurs[accesseur]);
                int nombre = Json.I(Json.Get(acc, "count"));
                int typeComp = Json.I(Json.Get(acc, "componentType"));
                var vue = Json.Obj(Vues[Json.I(Json.Get(acc, "bufferView"))]);
                int decalage = Json.I(Json.Get(vue, "byteOffset")) + Json.I(Json.Get(acc, "byteOffset"));
                var res = new int[nombre];
                switch (typeComp)
                {
                    case 5125: Buffer.BlockCopy(bin, decalage, res, 0, nombre * 4); break;
                    case 5123: for (int i = 0; i < nombre; i++) res[i] = BitConverter.ToUInt16(bin, decalage + 2 * i); break;
                    case 5121: for (int i = 0; i < nombre; i++) res[i] = bin[decalage + i]; break;
                    default: throw new InvalidDataException("Type d'indices non géré : " + typeComp);
                }
                return res;
            }
        }

        // ================================================================== mini lecteur JSON (insensible à la langue du système)
        static class Json
        {
            public static object Parse(string texte)
            {
                int i = 0;
                var v = Valeur(texte, ref i);
                return v;
            }

            public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
            public static List<object> Arr(object o) { return o as List<object>; }
            public static string S(object o) { return o as string; }

            public static object Get(Dictionary<string, object> d, string cle)
            {
                object v;
                return d != null && d.TryGetValue(cle, out v) ? v : null;
            }

            public static float F(object o, float defaut = 0f)
            {
                if (o is double) return (float)(double)o;
                if (o is bool) return (bool)o ? 1f : 0f;
                return defaut;
            }

            public static int I(object o, int defaut = 0)
            {
                if (o is double) return (int)Math.Round((double)o);
                return defaut;
            }

            public static bool B(object o, bool defaut = false)
            {
                if (o is bool) return (bool)o;
                if (o is double) return (double)o != 0.0;
                return defaut;
            }

            public static Color Couleur(object o, Color defaut)
            {
                var a = Arr(o);
                if (a == null || a.Count < 3) return defaut;
                return new Color(F(a[0]), F(a[1]), F(a[2]), a.Count > 3 ? F(a[3], 1f) : 1f);
            }

            static void Blancs(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            static object Valeur(string s, ref int i)
            {
                Blancs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON incomplet");
                char c = s[i];
                if (c == '{') return Objet(s, ref i);
                if (c == '[') return Tableau(s, ref i);
                if (c == '"') return Chaine(s, ref i);
                if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
                if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
                if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
                return Nombre(s, ref i);
            }

            static Dictionary<string, object> Objet(string s, ref int i)
            {
                var d = new Dictionary<string, object>();
                i++;
                Blancs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (i < s.Length)
                {
                    Blancs(s, ref i);
                    string cle = Chaine(s, ref i);
                    Blancs(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("':' attendu à la position " + i);
                    i++;
                    d[cle] = Valeur(s, ref i);
                    Blancs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("',' ou '}' attendu à la position " + i);
                }
                throw new FormatException("Objet JSON non fermé");
            }

            static List<object> Tableau(string s, ref int i)
            {
                var l = new List<object>();
                i++;
                Blancs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (i < s.Length)
                {
                    l.Add(Valeur(s, ref i));
                    Blancs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("',' ou ']' attendu à la position " + i);
                }
                throw new FormatException("Tableau JSON non fermé");
            }

            static string Chaine(string s, ref int i)
            {
                if (s[i] != '"') throw new FormatException("Chaîne attendue à la position " + i);
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                throw new FormatException("Chaîne JSON non fermée");
            }

            static object Nombre(string s, ref int i)
            {
                int debut = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (i == debut) throw new FormatException("Valeur JSON inattendue à la position " + i);
                return double.Parse(s.Substring(debut, i - debut), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
