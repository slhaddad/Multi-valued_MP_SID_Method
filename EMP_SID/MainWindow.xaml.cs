// corrigé le 25/06/2020
// Version V3 : élément structurant (ES) = disque euclidien de rayon i (dx² + dy² <= i²),
//   utilisé directement en une seule passe pour l'érosion, la dilatation, l'ouverture et
//   la fermeture (5, 13, 29, 49, 81 pixels pour i = 1..5, comme skimage.morphology.disk(i)).
//   La reconstruction géodésique utilise le disque élémentaire B1 (rayon 1, 5 pixels).
// Version V4 : [V4-ANTI-CONDORCET] le ∧ / ∨ point à point de la reconstruction utilise un ordre de référence
//   fixe et transitif (score de la méthode contre un panel fixe de vecteurs de l'image), et la suite
//   des marqueurs est rendue monotone : plus de cycle de Condorcet, stabilité toujours atteinte.
//   Le classement des n pixels de la fenêtre (MinMaxVecteurs) est inchangé.
// =====================================================================================
// EMP_SID
// Morphologie mathématique multivaluée (images multibandes) avec ordonnancement vectoriel
// réduit fondé sur la DISTANCE CUMULÉE SID (Spectral Information Divergence, Plaza et al.).
//
// Pour chaque pixel x_i du voisinage B(x) défini par l'élément structurant (ES) :
//     D_cum(x_i) = somme_{x_j dans B(x)} SID( f(x_i), f(x_j) )
//     SID(u,v)   = somme_k p_k log(p_k/q_k) + q_k log(q_k/p_k),  p_k = u_k / somme_l u_l
//   dilatation : supremum = pixel de D_cum MAXIMALE (le plus singulier du voisinage)
//   érosion    : infimum  = pixel de D_cum MINIMALE (le plus représentatif du voisinage)
//   ex aequo de D_cum : départage par l'indice spatial (ordre total <=_Dcum) :
//     infimum = plus petit indice spatial, supremum = plus grand indice spatial.
//
// Transformations calculées pour chaque taille i = 1..max de l'ES :
//   érosion, dilatation, ouverture, fermeture, ouverture par reconstruction,
//   fermeture par reconstruction (+ export multibande ENVI .hdr).
//
// ES : disque euclidien de rayon i (dx² + dy² <= i²), utilisé en une seule passe (V3) :
//      tous les pixels du disque sont comparés ensemble. Reconstruction : disque B1 (5 pixels).
// =====================================================================================
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows;
using SDColor = System.Drawing.Color;
using SDPoint = System.Drawing.Point;

namespace EMP_SID
{
    public partial class MainWindow : Window
    {
        // Tolérance numérique pour comparer deux distances cumulées (nombres réels)
        private const double EPS = 1e-9;

        // Petite constante ajoutée à chaque composante pour que les probabilités p_k soient
        // strictement positives : SID n'est pas définie pour une composante nulle (log 0, division par 0)
        private const double EPS_SID = 1e-6;

        // Bandes chargées par l'utilisateur (une image panchromatique = une bande)
        List<Bitmap> imagesBmp = new List<Bitmap>();

        public MainWindow()
        {
            InitializeComponent();
        }

        // Dossier de sortie « IMAGES-résultat » : toujours juste sous le dossier du projet.
        // On remonte depuis le dossier de l'exécutable (bin\Debug\... ou Executable\) jusqu'au
        // dossier qui contient la solution (.sln) ; s'il n'y en a pas (exécutable copié seul sur
        // une autre machine), le dossier est créé à côté de l'exécutable.
        private static string DossierResultats()
        {
            string dossierExe = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                DirectoryInfo d = new DirectoryInfo(dossierExe);
                while (d != null)
                {
                    if (d.GetFiles("*.sln").Length > 0)
                        return Path.Combine(d.FullName, "IMAGES-résultat");
                    d = d.Parent;
                }
            }
            catch (Exception)
            {
                // Dossier parent illisible : on garde le dossier de l'exécutable
            }
            return Path.Combine(dossierExe, "IMAGES-résultat");
        }

        // ============================== CHARGEMENT DES BANDES ==============================
        private void button_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFile = new OpenFileDialog();
            openFile.Multiselect = true;
            openFile.DefaultExt = "png";
            openFile.Filter = "PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|BMP (*.bmp)|*.bmp|TIFF (*.tiff;*.tif)|*.tiff;*.tif";
            bool? ok = openFile.ShowDialog();
            if (ok != true || openFile.FileNames.Length == 0)
                return;
            // On libère les anciennes bandes avant d'en charger de nouvelles
            foreach (Bitmap old in imagesBmp)
                old.Dispose();
            imagesBmp.Clear();
            foreach (string filename in openFile.FileNames)
                imagesBmp.Add(new Bitmap(filename));
            MessageBox.Show(imagesBmp.Count + " bande(s) chargée(s).", "Chargement");
        }

        // Lecture sécurisée d'un entier
        private static bool TryParseInt(string s, out int v)
        {
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)
                || int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out v);
        }

        // Lecture sécurisée d'un réel (accepte la virgule ou le point décimal)
        private static bool TryParseDouble(string s, out double v)
        {
            string t = (s ?? "").Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out v);
        }

        // ============================== PROGRAMME PRINCIPAL ==============================
        private void button1_Click(object sender, RoutedEventArgs e)
        {
            // ---- 1. Vérification des données saisies ----
            if (imagesBmp.Count == 0)
            {
                MessageBox.Show("Chargez d'abord au moins une bande (bouton Parcourir).", "Erreur");
                return;
            }
            if (imagesBmp.Count < 2)
            {
                MessageBox.Show("La distance SID compare des signatures spectrales : chargez au moins deux bandes.", "Erreur");
                return;
            }
            int max, itStabilite;
            double prStabilitePct;
            if (!TryParseInt(textBox.Text, out max) || max < 1)
            {
                MessageBox.Show("Taille maximale de l'ES invalide (entier >= 1).", "Erreur");
                return;
            }
            if (!TryParseInt(textBox2.Text, out itStabilite) || itStabilite < 0)
            {
                MessageBox.Show("Nombre d'itérations de stabilité invalide (entier >= 0 ; 0 = illimité).", "Erreur");
                return;
            }
            if (!TryParseDouble(textBox3.Text, out prStabilitePct) || prStabilitePct < 0 || prStabilitePct > 100)
            {
                MessageBox.Show("Pourcentage de ressemblance invalide (0 à 100).", "Erreur");
                return;
            }
            double prStabilite = prStabilitePct / 100.0;

            // Toutes les bandes doivent avoir la même taille
            int w0 = imagesBmp[0].Width, h0 = imagesBmp[0].Height;
            for (int k = 1; k < imagesBmp.Count; k++)
            {
                if (imagesBmp[k].Width != w0 || imagesBmp[k].Height != h0)
                {
                    MessageBox.Show("Toutes les bandes doivent avoir la même taille.", "Erreur");
                    return;
                }
            }

            // ---- 2. Dossier de sortie ----
            string outDir = DossierResultats();
            Directory.CreateDirectory(outDir);

            // ---- 3. Conversion des bitmaps en matrices d'entiers [x, y] (une matrice par bande) ----
            List<int[,]> imagesMat;
            try
            {
                imagesMat = bmpToMat(imagesBmp);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Impossible de lire les images : " + ex.Message, "Erreur");
                return;
            }

            try
            {
                // Listes de toutes les reconstructions (toutes tailles d'ES, toutes bandes) pour l'export ENVI
                List<int[,]> enviOuvert = new List<int[,]>();
                List<int[,]> enviFerme = new List<int[,]>();
                List<string> nomsOuvert = new List<string>();
                List<string> nomsFerme = new List<string>();

                // ---- 4. Boucle sur la taille i de l'ES (disque) ----
                for (int i = 1; i <= max; i++)
                {
                    List<int[,]> imagesErodeInit = new List<int[,]>();
                    List<int[,]> imagesDilateInit = new List<int[,]>();
                    List<int[,]> imagesOuvertesStandards = new List<int[,]>();
                    List<int[,]> imagesFermeesStandards = new List<int[,]>();

                    // Érosion et dilatation multivaluées de taille i
                    ErosionDilatationInit(imagesMat, ref imagesErodeInit, ref imagesDilateInit, i);
                    // Ouverture et fermeture standard de taille i
                    OuvertureFermetureStandard(imagesErodeInit, imagesDilateInit, ref imagesOuvertesStandards, ref imagesFermeesStandards, i);

                    // Sauvegarde des résultats (une image TIFF par bande)
                    for (int k = 0; k < imagesBmp.Count; k++)
                    {
                        Bitmap sortieErod = new Bitmap(w0, h0);
                        Bitmap sortieDilat = new Bitmap(w0, h0);
                        Bitmap sortieOuverteStandard = new Bitmap(w0, h0);
                        Bitmap sortieFermeeStandard = new Bitmap(w0, h0);
                        for (int x = 0; x < w0; x++)
                            for (int y = 0; y < h0; y++)
                            {
                                sortieErod.SetPixel(x, y, Gray(imagesErodeInit[k][x, y]));
                                sortieDilat.SetPixel(x, y, Gray(imagesDilateInit[k][x, y]));
                                sortieOuverteStandard.SetPixel(x, y, Gray(imagesOuvertesStandards[k][x, y]));
                                sortieFermeeStandard.SetPixel(x, y, Gray(imagesFermeesStandards[k][x, y]));
                            }
                        sortieErod.Save(Path.Combine(outDir, "Erod B_" + k + " ES_" + i + ".tiff"));
                        sortieDilat.Save(Path.Combine(outDir, "Dilat B_" + k + " ES_" + i + ".tiff"));
                        sortieOuverteStandard.Save(Path.Combine(outDir, "OuvertureStandard B_" + k + " ES_" + i + ".tiff"));
                        sortieFermeeStandard.Save(Path.Combine(outDir, "FermetureStandard B_" + k + " ES_" + i + ".tiff"));
                        sortieErod.Dispose();
                        sortieDilat.Dispose();
                        sortieOuverteStandard.Dispose();
                        sortieFermeeStandard.Dispose();
                    }

                    // Ouverture et fermeture par reconstruction (arrêt par stabilité)
                    List<int[,]> gNewFerme = new List<int[,]>();
                    List<int[,]> gNewOuvert = new List<int[,]>();
                    Reconstruction(imagesMat, imagesErodeInit, imagesDilateInit, itStabilite, prStabilite, ref gNewOuvert, ref gNewFerme);

                    for (int k = 0; k < imagesBmp.Count; k++)
                    {
                        Bitmap sortieFerme = new Bitmap(w0, h0);
                        Bitmap sortieOuvert = new Bitmap(w0, h0);
                        for (int x = 0; x < w0; x++)
                            for (int y = 0; y < h0; y++)
                            {
                                sortieFerme.SetPixel(x, y, Gray(gNewFerme[k][x, y]));
                                sortieOuvert.SetPixel(x, y, Gray(gNewOuvert[k][x, y]));
                            }
                        sortieFerme.Save(Path.Combine(outDir, "FermeReconstruction B_" + k + " ES_" + i + ".tiff"));
                        sortieOuvert.Save(Path.Combine(outDir, "OuvertReconstruction B_" + k + " ES_" + i + ".tiff"));
                        sortieFerme.Dispose();
                        sortieOuvert.Dispose();
                        // Mémorisation pour les fichiers multibandes ENVI
                        enviOuvert.Add((int[,])gNewOuvert[k].Clone());
                        enviFerme.Add((int[,])gNewFerme[k].Clone());
                        nomsOuvert.Add("OuvertReconstruction B_" + k + " ES_" + i);
                        nomsFerme.Add("FermeReconstruction B_" + k + " ES_" + i);
                    }
                }

                // ---- 5. Fichiers multibandes ENVI (.img + .hdr) ----
                List<int[,]> enviTout = new List<int[,]>();
                List<string> nomsTout = new List<string>();
                enviTout.AddRange(enviOuvert);
                enviTout.AddRange(enviFerme);
                nomsTout.AddRange(nomsOuvert);
                nomsTout.AddRange(nomsFerme);
                WriteEnviMultiband(outDir, "EXTENDED-PROFIL-MOR-multibande", w0, h0, enviTout, nomsTout);
                WriteEnviMultiband(outDir, "OuvertReconstruction-multibande", w0, h0, enviOuvert, nomsOuvert);
                WriteEnviMultiband(outDir, "FermeReconstruction-multibande", w0, h0, enviFerme, nomsFerme);

                MessageBox.Show("Traitement terminé.\nRésultats (TIFF + ENVI .hdr) dans :\n" + outDir, "EMP_SID");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur pendant le calcul :\n" + ex.Message, "Erreur");
            }
        }

        // Convertit une valeur entière en niveau de gris (borné à [0, 255])
        private static SDColor Gray(int v)
        {
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return SDColor.FromArgb(v, v, v);
        }

        // Écrit un fichier multibande ENVI (.img en BSQ, 8 bits + .hdr)
        private static void WriteEnviMultiband(string outDir, string baseName, int width, int height, List<int[,]> bands, List<string> bandNames)
        {
            if (bands == null || bands.Count == 0)
                return;
            string imgPath = Path.Combine(outDir, baseName + ".img");
            string hdrPath = Path.Combine(outDir, baseName + ".hdr");
            using (FileStream fs = new FileStream(imgPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[width * height];
                for (int b = 0; b < bands.Count; b++)
                {
                    int idx = 0;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int v = bands[b][x, y];
                            if (v < 0) v = 0;
                            if (v > 255) v = 255;
                            buf[idx++] = (byte)v;
                        }
                    }
                    fs.Write(buf, 0, buf.Length);
                }
            }
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < bandNames.Count; i++)
            {
                if (i > 0) names.Append(",\r\n ");
                names.Append(bandNames[i]);
            }
            string hdr =
                "ENVI\r\n" +
                "description = { EMP_SID, " + baseName + " }\r\n" +
                "samples = " + width + "\r\n" +
                "lines = " + height + "\r\n" +
                "bands = " + bands.Count + "\r\n" +
                "header offset = 0\r\n" +
                "file type = ENVI Standard\r\n" +
                "data type = 1\r\n" +
                "interleave = bsq\r\n" +
                "byte order = 0\r\n" +
                "band names = {\r\n " + names + "\r\n}\r\n";
            File.WriteAllText(hdrPath, hdr, System.Text.Encoding.ASCII);
        }

        // ============================== ÉLÉMENT STRUCTURANT DISQUE ==============================
        // Décalages (dx, dy) des pixels couverts par un disque de rayon r centré sur (0,0).
        // r=1 -> 5 pixels, r=2 -> 13 pixels, r=3 -> 29 pixels, r=4 -> 49 pixels, r=5 -> 81 pixels
        // (même ensemble de pixels que skimage.morphology.disk(r)).
        // L'ordre de parcours (dx puis dy croissants) correspond à l'INDICE SPATIAL croissant.
        private List<SDPoint> GetDiskOffsets(int r)
        {
            var offs = new List<SDPoint>();
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    if (dx * dx + dy * dy <= r * r) offs.Add(new SDPoint(dx, dy));
            return offs;
        }

        // Vrai si le disque centré en (x,y) déborde de l'image : pixel de bord, non traité
        // (il conserve sa valeur précédente)
        private bool IsBorder(int x, int y, int W, int H, List<SDPoint> offs)
        {
            foreach (var p in offs)
            {
                int nx = x + p.X, ny = y + p.Y;
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) return true;
            }
            return false;
        }

        // ============================== DISTANCE SID ==============================
        // SID entre le vecteur A(xa,ya) de imgA et le vecteur B(xb,yb) de imgB :
        //   p_k = (a_k + EPS_SID) / somme_l (a_l + EPS_SID) ; q_k = (b_k + EPS_SID) / somme_l (b_l + EPS_SID)
        //   SID = somme_k p_k log(p_k/q_k) + q_k log(q_k/p_k) = somme_k (p_k - q_k) log(p_k/q_k)  (>= 0)
        // EPS_SID évite log(0) et la division par 0 lorsqu'une composante (ou tout le vecteur) est nulle.
        private static double SID(List<int[,]> imgA, int xa, int ya, List<int[,]> imgB, int xb, int yb)
        {
            int m = imgA.Count;
            double sa = 0, sb = 0;
            for (int k = 0; k < m; k++)
            {
                sa += imgA[k][xa, ya] + EPS_SID;
                sb += imgB[k][xb, yb] + EPS_SID;
            }
            double sid = 0;
            for (int k = 0; k < m; k++)
            {
                double p = (imgA[k][xa, ya] + EPS_SID) / sa;
                double q = (imgB[k][xb, yb] + EPS_SID) / sb;
                sid += (p - q) * Math.Log10(p / q);
            }
            return sid;
        }

        // Infimum (D_cum minimale, calculée sur imagesErodPrec) et supremum (D_cum maximale, calculée
        // sur imagesDilatePrec) des pixels-vecteurs du disque de rayon 'rayonDisque' centré en (x,y).
        // Ex aequo (à EPS près) : infimum = plus petit indice spatial, supremum = plus grand indice spatial.
        private void MinMaxVecteurs(List<int[,]> imagesErodPrec, List<int[,]> imagesDilatePrec, int x, int y, ref int sMin, ref int tMin, ref int sMax, ref int tMax, int rayonDisque)
        {
            var offs = GetDiskOffsets(rayonDisque);
            int n = offs.Count;

            // Distances cumulées : SID étant symétrique, chaque paire (i, j) n'est calculée qu'une fois.
            // Le terme SID(x_i, x_i) = 0 n'a pas besoin d'être ajouté.
            double[] dErod = new double[n];
            double[] dDilat = new double[n];
            for (int i = 0; i < n; i++)
            {
                int xi = x + offs[i].X, yi = y + offs[i].Y;
                for (int j = i + 1; j < n; j++)
                {
                    int xj = x + offs[j].X, yj = y + offs[j].Y;
                    double sE = SID(imagesErodPrec, xi, yi, imagesErodPrec, xj, yj);
                    double sD = SID(imagesDilatePrec, xi, yi, imagesDilatePrec, xj, yj);
                    dErod[i] += sE; dErod[j] += sE;
                    dDilat[i] += sD; dDilat[j] += sD;
                }
            }

            // Le parcours se fait par indice spatial croissant ; on part du premier pixel du disque
            int idxMin = 0, idxMax = 0;
            double minVal = dErod[0], maxVal = dDilat[0];
            for (int z = 1; z < n; z++)
            {
                // Infimum : strictement plus petit => remplace ; ex aequo => on garde le plus petit indice
                if (dErod[z] < minVal - EPS) { minVal = dErod[z]; idxMin = z; }
                // Supremum : plus grand ou ex aequo => remplace (le plus grand indice l'emporte)
                if (dDilat[z] >= maxVal - EPS)
                {
                    idxMax = z;
                    if (dDilat[z] > maxVal) maxVal = dDilat[z];
                }
            }
            sMin = x + offs[idxMin].X; tMin = y + offs[idxMin].Y;
            sMax = x + offs[idxMax].X; tMax = y + offs[idxMax].Y;
        }

        // Distance cumulée du vecteur v(xv,yv) de imgV par rapport au voisinage B1 du pixel (x,y)
        // dans l'image masque f : somme_{s dans B1} SID( v, f(x+s) ).
        private double DistanceCumuleeMasque(List<int[,]> imgV, int xv, int yv, List<int[,]> imagesMat, int x, int y, List<SDPoint> offsB1)
        {
            double d = 0;
            foreach (var p in offsB1)
                d += SID(imgV, xv, yv, imagesMat, x + p.X, y + p.Y);
            return d;
        }

        // Comparaison point à point de deux vecteurs A et B au pixel (x,y), utilisée pour le supremum /
        // infimum géodésique. Une paire isolée ne peut pas être ordonnée par la distance cumulée (SID est
        // symétrique : D_cum(A) = D_cum(B) sur {A, B}). Les deux vecteurs sont donc évalués par leur
        // distance cumulée par rapport au MÊME ensemble de référence : le voisinage B1 de (x,y) dans
        // l'image masque f. Ex aequo : indice spatial.
        // Retourne -1 si A < B, 0 si A = B, 1 si A > B.
        // [V4-ANTI-CONDORCET] Ancienne comparaison (versions précédentes), conservée pour mémoire : la reconstruction
        // ne l'appelle plus, elle utilise CompareReference (ordre de référence fixe et transitif).
        private int CompareDeuxVecteursSID(List<int[,]> imgA, int xa, int ya, List<int[,]> imgB, int xb, int yb, List<int[,]> imagesMat, int x, int y, List<SDPoint> offsB1)
        {
            // Vecteurs identiques : égalité
            bool identiques = true;
            for (int k = 0; k < imgA.Count; k++)
                if (imgA[k][xa, ya] != imgB[k][xb, yb]) { identiques = false; break; }
            if (identiques) return 0;

            double dA = DistanceCumuleeMasque(imgA, xa, ya, imagesMat, x, y, offsB1);
            double dB = DistanceCumuleeMasque(imgB, xb, yb, imagesMat, x, y, offsB1);
            if (Math.Abs(dA - dB) > EPS) return dA < dB ? -1 : 1;

            // Ex aequo : indice spatial (parcours x puis y)
            int H = imagesMat[0].GetLength(1);
            int idxA = xa * H + ya;
            int idxB = xb * H + yb;
            if (idxA < idxB) return -1;
            if (idxA > idxB) return 1;
            return 0;
        }

        // =====================================================================================
        // [V4-ANTI-CONDORCET] ORDRE DE RÉFÉRENCE FIXE POUR LE ∧ / ∨ POINT À POINT DE LA RECONSTRUCTION
        // =====================================================================================
        // Méthode : distance cumulée SID.
        // Problème corrigé : jusqu'ici, le ∧ / ∨ point à point de la reconstruction comparait les deux vecteurs
        // par leur distance cumulée au voisinage B1 du pixel dans f (CompareDeuxVecteursSID). Cet ensemble de
        // référence change d'un pixel à l'autre : la même paire peut être classée A > B en un pixel et B > A
        // en un autre. Les comparaisons utilisées ne forment donc pas un ordre fixe (cycles A > B, B > C et
        // C > A possibles), et la reconstruction pouvait tourner en boucle sans jamais se stabiliser.
        // Correction : chaque vecteur v reçoit un score FIXE, calculé avec la même méthode et les mêmes
        // paramètres, mais toujours contre le même panel de référence R : au plus TAILLE_PANEL_REF
        // vecteurs prélevés une fois pour toutes dans l'image d'entrée f.
        //   D_R(v) = somme_{r dans R} SID(v, r)  (même formule SID que la fenêtre ; supremum = D_R maximale).
        // Deux vecteurs sont comparés par ce score, puis par l'ordre lexicographique des bandes en cas
        // d'égalité : c'est un ordre TOTAL sur les valeurs (réflexif, antisymétrique, transitif), donc
        // aucun cycle de Condorcet n'est possible. Le classement des n pixels de la fenêtre
        // (MinMaxVecteurs) n'est PAS modifié : seul le ∧ / ∨ point à point de la reconstruction change.
        private const int TAILLE_PANEL_REF = 256;          // [V4-ANTI-CONDORCET] nombre maximal de vecteurs du panel R
        private List<int[]> panelRef;                       // [V4-ANTI-CONDORCET] panel de référence R
        private int[] prioriteRef;                          // [V4-ANTI-CONDORCET] ordre des bandes pour le départage
        private Dictionary<int[], double> cacheScoreRef;    // [V4-ANTI-CONDORCET] score déjà calculé de chaque vecteur

        // [V4-ANTI-CONDORCET] Égalité exacte de deux vecteurs (clé du cache des scores)
        private sealed class ComparateurVecteurs : IEqualityComparer<int[]>
        {
            public bool Equals(int[] a, int[] b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return false;
                return true;
            }

            public int GetHashCode(int[] a)
            {
                unchecked
                {
                    int h = 17;
                    for (int i = 0; i < a.Length; i++) h = h * 31 + a[i];
                    return h;
                }
            }
        }

        // [V4-ANTI-CONDORCET] Construction du panel R (prélèvement systématique : un pixel sur 'pas', dans
        // l'ordre de parcours x puis y de l'image d'entrée) et de l'ordre de départage des bandes.
        private void PreparerOrdreReference(List<int[,]> imagesMat)
        {
            int m = imagesMat.Count;
            int W = imagesMat[0].GetLength(0), H = imagesMat[0].GetLength(1);
            int total = W * H;
            int pas = Math.Max(1, (total + TAILLE_PANEL_REF - 1) / TAILLE_PANEL_REF);
            panelRef = new List<int[]>();
            for (int p = 0; p < total; p += pas)
            {
                int[] v = new int[m];
                for (int k = 0; k < m; k++) v[k] = imagesMat[k][p / H, p % H];
                panelRef.Add(v);
            }
            // Départage : bandes dans l'ordre 1, 2, ..., m
            prioriteRef = new int[m];
            for (int k = 0; k < m; k++) prioriteRef[k] = k;

            cacheScoreRef = new Dictionary<int[], double>(new ComparateurVecteurs());
        }

        // [V4-ANTI-CONDORCET] SID entre deux vecteurs (même calcul que la fonction SID)
        private static double SIDVecteurs(int[] a, int[] b)
        {
            int m = a.Length;
            double sa = 0, sb = 0;
            for (int k = 0; k < m; k++)
            {
                sa += a[k] + EPS_SID;
                sb += b[k] + EPS_SID;
            }
            double sid = 0;
            for (int k = 0; k < m; k++)
            {
                double p = (a[k] + EPS_SID) / sa;
                double q = (b[k] + EPS_SID) / sb;
                sid += (p - q) * Math.Log10(p / q);
            }
            return sid;
        }

        // [V4-ANTI-CONDORCET] Score fixe D_R(v) : distance cumulée SID de v au panel R
        private double ScoreReference(int[] v)
        {
            double s;
            if (cacheScoreRef.TryGetValue(v, out s)) return s;
            double somme = 0;
            foreach (int[] r in panelRef) somme += SIDVecteurs(v, r);
            s = somme;
            if (double.IsNaN(s) || double.IsInfinity(s)) s = 0;   // sécurité : le score reste une fonction de v
            s = Math.Round(s, 10);                                 // arrondi fixe : ex aequo numériques => départage lexicographique
            cacheScoreRef[v] = s;
            return s;
        }

        // [V4-ANTI-CONDORCET] Comparaison de deux vecteurs pour le ∧ / ∨ point à point de la reconstruction :
        // score fixe (contre le panel R), puis ordre lexicographique des bandes (prioriteRef) en cas d'égalité.
        // C'est un ordre total sur les valeurs : il est transitif, aucun cycle de Condorcet n'est possible.
        // Retourne -1 si A < B, 0 si A et B sont le même vecteur, 1 si A > B.
        private int CompareReference(List<int[,]> imgA, int xa, int ya, List<int[,]> imgB, int xb, int yb)
        {
            int m = imgA.Count;
            int[] a = new int[m];
            int[] b = new int[m];
            for (int k = 0; k < m; k++)
            {
                a[k] = imgA[k][xa, ya];
                b[k] = imgB[k][xb, yb];
            }
            double sa = ScoreReference(a);
            double sb = ScoreReference(b);
            if (sa < sb) return -1;
            if (sa > sb) return 1;
            foreach (int k in prioriteRef)
            {
                if (a[k] < b[k]) return -1;
                if (a[k] > b[k]) return 1;
            }
            return 0;
        }


        // ============================== RECONSTRUCTION MORPHOLOGIQUE ==============================
        // Ouverture par reconstruction : R^delta_f( epsilon_Bi(f) )  (marqueur = érodé, masque = f)
        // Fermeture par reconstruction : R^epsilon_f( delta_Bi(f) )  (marqueur = dilaté, masque = f)
        // On itère les dilatations/érosions géodésiques d'ordre 1 jusqu'à stabilité, ou jusqu'à
        // atteindre le nombre d'itérations maximal fixé (0 = illimité), ou jusqu'au pourcentage
        // de ressemblance demandé.
        private void Reconstruction(List<int[,]> imagesMat, List<int[,]> imagesErodeInit, List<int[,]> imagesDilateInit, int itStabilite, double prStabilite, ref List<int[,]> gNewOuvert, ref List<int[,]> gNewFerme)
        {
            List<int[,]> gLastFerme = new List<int[,]>();
            List<int[,]> gLastOuvert = new List<int[,]>();
            for (int k = 0; k < imagesMat.Count; k++)
            {
                gNewFerme.Add(new int[imagesMat[0].GetLength(0), imagesMat[0].GetLength(1)]);
                gNewOuvert.Add(new int[imagesMat[0].GetLength(0), imagesMat[0].GetLength(1)]);
                // Marqueurs initiaux : image érodée (ouverture) et image dilatée (fermeture)
                gLastOuvert.Add((int[,])imagesErodeInit[k].Clone());
                gLastFerme.Add((int[,])imagesDilateInit[k].Clone());
            }

            // [V4-ANTI-CONDORCET] Ordre de référence fixe (transitif) utilisé par le ∧ / ∨ point à point.
            PreparerOrdreReference(imagesMat);
            // [V4-ANTI-CONDORCET] Marqueurs placés du bon côté du masque f (au sens de l'ordre de référence) :
            //   ouverture : h0 = inf(epsilon(f), f) ;  fermeture : h0 = sup(delta(f), f).
            // Avec la règle de monotonie de ErosionDilatationGeodesique, la suite des marqueurs est monotone
            // et bornée par f dans un ensemble fini de vecteurs : la stabilité est TOUJOURS atteinte.
            for (int x = 0; x < imagesMat[0].GetLength(0); x++)
                for (int y = 0; y < imagesMat[0].GetLength(1); y++)
                {
                    if (CompareReference(gLastOuvert, x, y, imagesMat, x, y) > 0)
                        for (int k = 0; k < imagesMat.Count; k++) gLastOuvert[k][x, y] = imagesMat[k][x, y];
                    if (CompareReference(gLastFerme, x, y, imagesMat, x, y) < 0)
                        for (int k = 0; k < imagesMat.Count; k++) gLastFerme[k][x, y] = imagesMat[k][x, y];
                }
            int toleranceStabilite = 0;
            if (itStabilite == 0) itStabilite = int.MaxValue; // 0 = nombre d'itérations non limité
            bool stopErod = false, stopDilat = false;

            // Détection de cycle : l'ordre par distance cumulée dépend du voisinage (ordre réduit local),
            // la suite des images géodésiques n'est donc pas forcément monotone et peut devenir
            // périodique sans jamais se stabiliser. On mémorise l'empreinte de chaque état ; si un état
            // réapparaît, la stabilité ne sera jamais atteinte et on arrête la transformation concernée
            // (évite une boucle infinie avec 0 itération = illimité et 100 % de ressemblance).
            // [V4-ANTI-CONDORCET] Avec l'ordre de référence et la règle de monotonie, un état ne peut plus réapparaître ;
            // cette détection de cycle est conservée uniquement par sécurité (elle ne se déclenche plus).
            HashSet<ulong> etatsOuvert = new HashSet<ulong>();
            HashSet<ulong> etatsFerme = new HashSet<ulong>();
            etatsOuvert.Add(Empreinte(gLastOuvert));
            etatsFerme.Add(Empreinte(gLastFerme));
            bool cycleOuvert = false, cycleFerme = false;

            while (((!stopDilat) || (!stopErod)) && (toleranceStabilite < itStabilite))
            {
                stopErod = true; stopDilat = true;
                // Une étape de dilatation / érosion géodésique d'ordre 1
                ErosionDilatationGeodesique(imagesMat, gLastFerme, gLastOuvert, prStabilite, ref gNewFerme, ref gNewOuvert, ref stopErod, ref stopDilat);
                // Les images calculées deviennent les marqueurs de l'itération suivante
                for (int k = 0; k < imagesMat.Count; k++)
                {
                    gLastOuvert[k] = (int[,])gNewOuvert[k].Clone();
                    gLastFerme[k] = (int[,])gNewFerme[k].Clone();
                }
                toleranceStabilite++;

                // Un état déjà rencontré => suite périodique => arrêt de la transformation concernée
                if (!etatsOuvert.Add(Empreinte(gLastOuvert))) cycleOuvert = true;
                if (!etatsFerme.Add(Empreinte(gLastFerme))) cycleFerme = true;
                if (cycleOuvert) stopDilat = true;
                if (cycleFerme) stopErod = true;
            }
        }

        // Empreinte 64 bits (FNV-1a) d'une image multibande, utilisée pour reconnaître un état déjà vu
        private static ulong Empreinte(List<int[,]> img)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (int[,] bande in img)
                    foreach (int v in bande)
                    {
                        h ^= (uint)v;
                        h *= 1099511628211UL;
                    }
                return h;
            }
        }

        // Une étape géodésique d'ordre 1 avec B1 (disque de rayon 1) :
        //   dilatation géodésique : delta_f^(1)(h)   = infimum ( delta_B1(h),   f )  -> ouverture
        //   érosion géodésique    : epsilon_f^(1)(h) = supremum( epsilon_B1(h), f )  -> fermeture
        // Le supremum / infimum point à point est déterminé par CompareReference (distance cumulée SID au panel fixe R).
        // [V4-ANTI-CONDORCET] Le ∧ / ∨ point à point utilise CompareReference (ordre de référence fixe et transitif)
        // et la suite des marqueurs est monotone (voir la section ORDRE DE RÉFÉRENCE FIXE).
        private void ErosionDilatationGeodesique(List<int[,]> imagesMat, List<int[,]> gLastFerme, List<int[,]> gLastOuvert, double prStabilite, ref List<int[,]> gNewFerme, ref List<int[,]> gNewOuvert, ref bool stopErod, ref bool stopDilat)
        {
            int W = imagesMat[0].GetLength(0), H = imagesMat[0].GetLength(1);
            var offsB1 = GetDiskOffsets(1);
            int ressemblanceOuverture = 0, ressemblanceFermeture = 0;
            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < H; y++)
                {
                    if (IsBorder(x, y, W, H, offsB1))
                    {
                        // Pixel de bord : valeur précédente conservée
                        for (int k = 0; k < imagesMat.Count; k++)
                        {
                            gNewFerme[k][x, y] = gLastFerme[k][x, y];
                            gNewOuvert[k][x, y] = gLastOuvert[k][x, y];
                        }
                    }
                    else
                    {
                        // Infimum (érosion) du marqueur de fermeture et supremum (dilatation) du marqueur d'ouverture dans B1
                        int sMax = 0, tMax = 0, sMin = 0, tMin = 0;
                        MinMaxVecteurs(gLastFerme, gLastOuvert, x, y, ref sMin, ref tMin, ref sMax, ref tMax, 1);

                        // Érosion géodésique : supremum( epsilon_B1(h), f )
                        // [V4-ANTI-CONDORCET] sup(epsilon_B1(h), f) au sens de l'ordre de référence fixe (transitif)
                        int cmpFerme = CompareReference(imagesMat, x, y, gLastFerme, sMin, tMin);
                        if (cmpFerme < 0) // f < epsilon_B1(h) => le supremum est epsilon_B1(h)
                            for (int k = 0; k < imagesMat.Count; k++) gNewFerme[k][x, y] = gLastFerme[k][sMin, tMin];
                        else              // f >= epsilon_B1(h) => le supremum est f
                            for (int k = 0; k < imagesMat.Count; k++) gNewFerme[k][x, y] = imagesMat[k][x, y];
                        // [V4-ANTI-CONDORCET] Monotonie : la fermeture par reconstruction est la limite d'une suite
                        // DÉCROISSANTE. Si la nouvelle valeur est plus grande que la précédente (au sens de l'ordre
                        // de référence), la valeur précédente est conservée : aucun aller-retour, donc aucun cycle.
                        if (CompareReference(gNewFerme, x, y, gLastFerme, x, y) > 0)
                            for (int k = 0; k < imagesMat.Count; k++) gNewFerme[k][x, y] = gLastFerme[k][x, y];

                        // Dilatation géodésique : infimum( delta_B1(h), f )
                        // [V4-ANTI-CONDORCET] inf(delta_B1(h), f) au sens de l'ordre de référence fixe (transitif)
                        int cmpOuvert = CompareReference(gLastOuvert, sMax, tMax, imagesMat, x, y);
                        if (cmpOuvert < 0) // delta_B1(h) < f => l'infimum est delta_B1(h)
                            for (int k = 0; k < imagesMat.Count; k++) gNewOuvert[k][x, y] = gLastOuvert[k][sMax, tMax];
                        else               // delta_B1(h) >= f => l'infimum est f
                            for (int k = 0; k < imagesMat.Count; k++) gNewOuvert[k][x, y] = imagesMat[k][x, y];
                        // [V4-ANTI-CONDORCET] Monotonie : l'ouverture par reconstruction est la limite d'une suite
                        // CROISSANTE. Si la nouvelle valeur est plus petite que la précédente (au sens de l'ordre
                        // de référence), la valeur précédente est conservée : aucun aller-retour, donc aucun cycle.
                        if (CompareReference(gNewOuvert, x, y, gLastOuvert, x, y) < 0)
                            for (int k = 0; k < imagesMat.Count; k++) gNewOuvert[k][x, y] = gLastOuvert[k][x, y];

                        // Test de stabilité : le pixel est-il identique à l'itération précédente (toutes bandes) ?
                        int locOuv = 0, locFerm = 0;
                        for (int k = 0; k < imagesMat.Count; k++)
                        {
                            if (gNewFerme[k][x, y] != gLastFerme[k][x, y]) stopErod = false; else locFerm++;
                            if (gNewOuvert[k][x, y] != gLastOuvert[k][x, y]) stopDilat = false; else locOuv++;
                        }
                        if (locOuv == imagesMat.Count) ressemblanceOuverture++;
                        if (locFerm == imagesMat.Count) ressemblanceFermeture++;
                    }
                }
            }
            // Stabilité par pourcentage de ressemblance entre deux images successives
            double tauxOuv = (double)ressemblanceOuverture / (W * H);
            double tauxFerm = (double)ressemblanceFermeture / (W * H);
            if (tauxOuv >= prStabilite) stopDilat = true;
            if (tauxFerm >= prStabilite) stopErod = true;
        }

        // ============================== OUVERTURE / FERMETURE STANDARD ==============================
        // Ouverture = dilatation de l'érodé ; fermeture = érosion du dilaté (V3) : même ES
        //   (disque euclidien de rayon 'rayon'), appliqué en une seule passe à chaque opération.
        //   Pixel de bord (disque débordant de l'image) : valeur de l'image d'entrée conservée.
        private void OuvertureFermetureStandard(List<int[,]> imagesErodeInit, List<int[,]> imagesDilateInit, ref List<int[,]> imagesOuvertesStandards, ref List<int[,]> imagesFermeesStandards, int rayon)
        {
            List<int[,]> imagesErodPrec = new List<int[,]>();
            List<int[,]> imagesDilatePrec = new List<int[,]>();
            for (int k = 0; k < imagesErodeInit.Count; k++)
            {
                imagesOuvertesStandards.Add(new int[imagesErodeInit[0].GetLength(0), imagesErodeInit[0].GetLength(1)]);
                imagesFermeesStandards.Add(new int[imagesErodeInit[0].GetLength(0), imagesErodeInit[0].GetLength(1)]);
                // Fermeture : on érode l'image dilatée ; ouverture : on dilate l'image érodée
                imagesErodPrec.Add((int[,])imagesDilateInit[k].Clone());
                imagesDilatePrec.Add((int[,])imagesErodeInit[k].Clone());
            }
            // V3 : ES = disque euclidien de rayon 'rayon' (pixels tels que dx² + dy² <= rayon²),
            // utilisé directement en une seule passe : tous les pixels du disque sont comparés ensemble.
            var offsBi = GetDiskOffsets(rayon);
            int W = imagesErodeInit[0].GetLength(0), H = imagesErodeInit[0].GetLength(1);
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    if (IsBorder(x, y, W, H, offsBi))
                    {
                        // Pixel de bord (le disque de rayon 'rayon' déborde de l'image) : valeur d'entrée conservée
                        for (int k = 0; k < imagesErodeInit.Count; k++)
                        {
                            imagesOuvertesStandards[k][x, y] = imagesDilatePrec[k][x, y];
                            imagesFermeesStandards[k][x, y] = imagesErodPrec[k][x, y];
                        }
                    }
                    else
                    {
                        int sMax = 0, tMax = 0, sMin = 0, tMin = 0;
                        MinMaxVecteurs(imagesErodPrec, imagesDilatePrec, x, y, ref sMin, ref tMin, ref sMax, ref tMax, rayon);
                        for (int k = 0; k < imagesErodeInit.Count; k++)
                        {
                            imagesFermeesStandards[k][x, y] = imagesErodPrec[k][sMin, tMin];
                            imagesOuvertesStandards[k][x, y] = imagesDilatePrec[k][sMax, tMax];
                        }
                    }
                }
        }

        // ============================== ÉROSION / DILATATION ==============================
        // Érosion (infimum) et dilatation (supremum) multivaluées de taille 'rayon' (V3) :
        //   ES = disque euclidien de rayon 'rayon' ; pour chaque pixel, l'infimum et le supremum sont
        //   cherchés en UNE SEULE PASSE parmi TOUS les pixels-vecteurs couverts par ce disque.
        //   Pixel de bord (disque débordant de l'image) : valeur de l'image d'entrée conservée.
        private void ErosionDilatationInit(List<int[,]> imagesMat, ref List<int[,]> imagesErodeInit, ref List<int[,]> imagesDilateInit, int rayon)
        {
            List<int[,]> imagesErodPrec = new List<int[,]>();
            List<int[,]> imagesDilatePrec = new List<int[,]>();
            for (int k = 0; k < imagesMat.Count; k++)
            {
                imagesDilateInit.Add(new int[imagesMat[0].GetLength(0), imagesMat[0].GetLength(1)]);
                imagesErodeInit.Add(new int[imagesMat[0].GetLength(0), imagesMat[0].GetLength(1)]);
                imagesErodPrec.Add((int[,])imagesMat[k].Clone());
                imagesDilatePrec.Add((int[,])imagesMat[k].Clone());
            }
            // V3 : ES = disque euclidien de rayon 'rayon' (pixels tels que dx² + dy² <= rayon²),
            // utilisé directement en une seule passe : tous les pixels du disque sont comparés ensemble.
            var offsBi = GetDiskOffsets(rayon);
            int W = imagesMat[0].GetLength(0), H = imagesMat[0].GetLength(1);
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    if (IsBorder(x, y, W, H, offsBi))
                    {
                        // Pixel de bord (le disque de rayon 'rayon' déborde de l'image) : valeur d'entrée conservée
                        for (int k = 0; k < imagesMat.Count; k++)
                        {
                            imagesDilateInit[k][x, y] = imagesDilatePrec[k][x, y];
                            imagesErodeInit[k][x, y] = imagesErodPrec[k][x, y];
                        }
                    }
                    else
                    {
                        int sMax = 0, tMax = 0, sMin = 0, tMin = 0;
                        MinMaxVecteurs(imagesErodPrec, imagesDilatePrec, x, y, ref sMin, ref tMin, ref sMax, ref tMax, rayon);
                        for (int k = 0; k < imagesMat.Count; k++)
                        {
                            imagesErodeInit[k][x, y] = imagesErodPrec[k][sMin, tMin];
                            imagesDilateInit[k][x, y] = imagesDilatePrec[k][sMax, tMax];
                        }
                    }
                }
        }

        // ============================== CONVERSION BITMAP -> MATRICES ==============================
        // Une matrice int[x, y] par bande (valeur du canal rouge = niveau de gris)
        private List<int[,]> bmpToMat(List<Bitmap> imagesBmp)
        {
            List<int[,]> imagesMat = new List<int[,]>();
            for (int z = 0; z < imagesBmp.Count; z++)
            {
                imagesMat.Add(new int[imagesBmp[z].Width, imagesBmp[z].Height]);
                for (int x = 0; x < imagesBmp[z].Width; x++)
                    for (int y = 0; y < imagesBmp[z].Height; y++)
                        imagesMat[z][x, y] = imagesBmp[z].GetPixel(x, y).R;
            }
            return imagesMat;
        }
    }
}
