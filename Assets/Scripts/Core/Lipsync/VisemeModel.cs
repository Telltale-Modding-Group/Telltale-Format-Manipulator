using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TFM.Core.Lipsync
{
    /// <summary>
    /// A viseme classifier learned from pairs of shipped lipsync files and their audio. Each shipped
    /// key gives (acoustic features, viseme) pairs; the model keeps a centroid per viseme in a
    /// normalised feature space and classifies by nearest centroid. Calibrating on the game's own
    /// lines adapts the generator to the rig instead of relying on hand-written rules.
    /// </summary>
    public sealed class VisemeModel
    {
        public readonly List<string> Visemes = new List<string>();
        public float[] Mean;
        public float[] Std;
        public List<float[]> Centroids = new List<float[]>();
        public int KeysUsed;

        public int Dim { get { return Mean != null ? Mean.Length : 0; } }

        public bool IsTrained { get { return Centroids.Count > 0 && Mean != null; } }

        /// <summary>Nearest centroid in z-scored feature space. Returns null when untrained.</summary>
        public string Classify(float[] f)
        {
            if (!IsTrained || f == null || f.Length != Dim) return null;
            string best = null;
            double bestD = double.MaxValue;
            for (int c = 0; c < Centroids.Count; c++)
            {
                float[] k = Centroids[c];
                double d = 0;
                for (int i = 0; i < Dim; i++)
                {
                    double z = (f[i] - k[i]) / (Std[i] + 1e-6f);
                    d += z * z;
                }
                if (d < bestD) { bestD = d; best = Visemes[c]; }
            }
            return best;
        }

        // ------------------------------------------------------------ training
        readonly List<string> _labels = new List<string>();
        readonly List<float[]> _vectors = new List<float[]>();

        public void Add(string viseme, float[] features)
        {
            if (viseme == null || features == null || features.Length == 0) return;
            if (Mean != null && features.Length != Mean.Length) return;
            _labels.Add(viseme);
            _vectors.Add(features);
        }

        public void Clear()
        {
            Visemes.Clear();
            Centroids.Clear();
            Mean = null;
            Std = null;
            KeysUsed = 0;
            _labels.Clear();
            _vectors.Clear();
        }

        /// <summary>Builds the centroids from everything added so far.</summary>
        public bool Finish()
        {
            if (_vectors.Count == 0) return false;
            int dim = _vectors[0].Length;
            Mean = new float[dim];
            Std = new float[dim];
            for (int i = 0; i < _vectors.Count; i++)
                for (int k = 0; k < dim; k++) Mean[k] += _vectors[i][k];
            for (int k = 0; k < dim; k++) Mean[k] /= _vectors.Count;
            for (int i = 0; i < _vectors.Count; i++)
                for (int k = 0; k < dim; k++)
                {
                    float d = _vectors[i][k] - Mean[k];
                    Std[k] += d * d;
                }
            for (int k = 0; k < dim; k++) Std[k] = (float)Math.Sqrt(Std[k] / _vectors.Count) + 1e-6f;

            Dictionary<string, double[]> sums = new Dictionary<string, double[]>(StringComparer.Ordinal);
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _vectors.Count; i++)
            {
                string lab = _labels[i];
                double[] acc;
                if (!sums.TryGetValue(lab, out acc)) { acc = new double[dim]; sums[lab] = acc; counts[lab] = 0; }
                for (int k = 0; k < dim; k++) acc[k] += _vectors[i][k];
                counts[lab]++;
            }

            Visemes.Clear();
            Centroids.Clear();
            List<string> keys = new List<string>(sums.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                double[] acc = sums[keys[i]];
                float[] c = new float[dim];
                for (int k = 0; k < dim; k++) c[k] = (float)(acc[k] / counts[keys[i]]);
                Visemes.Add(keys[i]);
                Centroids.Add(c);
            }
            KeysUsed = _vectors.Count;
            return true;
        }

        /// <summary>How many training keys each viseme contributed.</summary>
        public Dictionary<string, int> Counts()
        {
            Dictionary<string, int> c = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _labels.Count; i++)
            {
                int n;
                c.TryGetValue(_labels[i], out n);
                c[_labels[i]] = n + 1;
            }
            return c;
        }

        // ------------------------------------------------------------ persistence
        public string Serialize()
        {
            if (!IsTrained) return "";
            StringBuilder b = new StringBuilder();
            b.Append("tfm-lipsync-model 1").Append('\n');
            b.Append(KeysUsed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            b.Append(Dim.ToString(CultureInfo.InvariantCulture)).Append('\n');
            AppendFloats(b, Mean);
            AppendFloats(b, Std);
            b.Append(Visemes.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < Visemes.Count; i++)
            {
                b.Append(Visemes[i]).Append('\t');
                AppendFloats(b, Centroids[i]);
            }
            return b.ToString();
        }

        static void AppendFloats(StringBuilder b, float[] v)
        {
            for (int i = 0; i < v.Length; i++)
            {
                if (i > 0) b.Append(' ');
                b.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
            }
            b.Append('\n');
        }

        public static VisemeModel Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            // Blank lines are tolerated: an older writer put one after every centroid row.
            string[] raw = text.Replace("\r", "").Split('\n');
            List<string> kept = new List<string>();
            for (int i = 0; i < raw.Length; i++) if (raw[i].Trim().Length > 0) kept.Add(raw[i]);
            string[] lines = kept.ToArray();
            int at = 0;
            if (lines.Length < 5 || !lines[0].StartsWith("tfm-lipsync-model", StringComparison.Ordinal)) return null;
            at++;
            VisemeModel m = new VisemeModel();
            m.KeysUsed = int.Parse(lines[at++], CultureInfo.InvariantCulture);
            int dim = int.Parse(lines[at++], CultureInfo.InvariantCulture);
            m.Mean = ParseFloats(lines[at++], dim);
            m.Std = ParseFloats(lines[at++], dim);
            if (m.Mean == null || m.Std == null) return null;
            int n = int.Parse(lines[at++], CultureInfo.InvariantCulture);
            for (int i = 0; i < n && at < lines.Length; i++)
            {
                string[] parts = lines[at++].Split('\t');
                if (parts.Length != 2) return null;
                float[] c = ParseFloats(parts[1], dim);
                if (c == null) return null;
                m.Visemes.Add(parts[0]);
                m.Centroids.Add(c);
            }
            return m.IsTrained ? m : null;
        }

        static float[] ParseFloats(string line, int dim)
        {
            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != dim) return null;
            float[] v = new float[dim];
            for (int i = 0; i < dim; i++) v[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            return v;
        }
    }
}
