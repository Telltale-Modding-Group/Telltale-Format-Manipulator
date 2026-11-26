using System;
using System.Collections.Generic;

namespace TFM.Core.Lipsync
{
    /// <summary>Mono audio samples ready for analysis.</summary>
    public sealed class AudioData
    {
        public float[] Samples = new float[0];
        public int SampleRate = 16000;

        public float Duration { get { return SampleRate > 0 ? Samples.Length / (float)SampleRate : 0f; } }

        /// <summary>Reads a RIFF/WAVE file: 8/16/24/32-bit PCM and 32/64-bit IEEE float, any channel count.</summary>
        public static AudioData FromWav(byte[] d)
        {
            if (d == null || d.Length < 12) throw new InvalidDataException("Not a WAV file (too short).");
            if (d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') throw new InvalidDataException("Not a RIFF file.");
            if (d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E') throw new InvalidDataException("Not a WAVE file.");

            int channels = 1, rate = 0, bits = 0, format = 1;
            int dataAt = -1, dataLen = 0;

            int p = 12;
            while (p + 8 <= d.Length)
            {
                string id = "" + (char)d[p] + (char)d[p + 1] + (char)d[p + 2] + (char)d[p + 3];
                int len = (int)BitConverter.ToUInt32(d, p + 4);
                int body = p + 8;
                if (len < 0) len = 0;
                if (id == "fmt " && body + 16 <= d.Length)
                {
                    format = BitConverter.ToUInt16(d, body);
                    channels = BitConverter.ToUInt16(d, body + 2);
                    rate = (int)BitConverter.ToUInt32(d, body + 4);
                    bits = BitConverter.ToUInt16(d, body + 14);
                    if (format == 0xFFFE && len >= 40 && body + 26 <= d.Length)
                        format = BitConverter.ToUInt16(d, body + 24); // WAVE_FORMAT_EXTENSIBLE sub-format
                }
                else if (id == "data") { dataAt = body; dataLen = Math.Min(len, d.Length - body); break; }
                p = body + len + (len & 1);
            }

            if (dataAt < 0) throw new InvalidDataException("WAV has no data chunk.");
            if (channels <= 0 || rate <= 0 || bits <= 0) throw new InvalidDataException("WAV header is incomplete.");
            if (format != 1 && format != 3) throw new InvalidDataException("Unsupported WAV format " + format + " (only PCM and float).");

            int bytesPer = bits / 8;
            int frames = dataLen / (bytesPer * channels);
            if (frames <= 0) throw new InvalidDataException("WAV data chunk is empty.");

            AudioData a = new AudioData();
            a.SampleRate = rate;
            a.Samples = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += ReadSample(d, dataAt + (i * channels + c) * bytesPer, bits, format);
                a.Samples[i] = sum / channels;
            }
            return a;
        }

        static float ReadSample(byte[] d, int at, int bits, int format)
        {
            if (format == 3)
            {
                if (bits == 32) return BitConverter.ToSingle(d, at);
                if (bits == 64) return (float)BitConverter.ToDouble(d, at);
                throw new InvalidDataException("Unsupported float WAV depth " + bits + ".");
            }
            switch (bits)
            {
                case 8: return (d[at] - 128) / 128f;
                case 16: return BitConverter.ToInt16(d, at) / 32768f;
                case 24:
                    {
                        int v = d[at] | (d[at + 1] << 8) | (d[at + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        return v / 8388608f;
                    }
                case 32: return BitConverter.ToInt32(d, at) / 2147483648f;
                default: throw new InvalidDataException("Unsupported PCM WAV depth " + bits + ".");
            }
        }

        /// <summary>Wraps decoded interleaved samples (as Unity gives them) into mono.</summary>
        public static AudioData FromInterleaved(float[] data, int channels, int rate)
        {
            if (channels <= 0) channels = 1;
            AudioData a = new AudioData();
            a.SampleRate = rate > 0 ? rate : 16000;
            int frames = data.Length / channels;
            a.Samples = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += data[i * channels + c];
                a.Samples[i] = sum / channels;
            }
            return a;
        }
    }

    /// <summary>One viseme key: which phoneme, when it starts, and how it fades.</summary>
    public struct LipKey
    {
        public float Time;
        public float FadeIn;
        public float Hold;
        public float FadeOut;
        public float Contribution;
        public ulong Phoneme;

        public float EndTime { get { return Time + FadeIn + Hold + FadeOut; } }
    }

    /// <summary>
    /// Turns speech into a phoneme timeline with no external model: short-time energy finds phrases,
    /// zero-crossing rate and spectral shape tell fricatives and nasals from vowels, and two band
    /// energies stand in for the first two formants to choose a vowel. It is a heuristic, but it
    /// produces the fades and holds the game's lipsync data expects, and it can be hand-edited after.
    /// </summary>
    public static class SpeechAnalyzer
    {
        public const int FftSize = 512;
        const int HopMs = 10;

        /// <summary>The viseme symbols a Telltale rig can play, as found in the shipped lipsync data.</summary>
        public static readonly string[] Visemes = {
            "aA", "eE", "i", "o", "u", "ah", "ss", "sH", "fV", "tH", "mM", "nN", "lL"
        };

        /// <summary>Collapses a phoneme onto the mouth shape the rig draws for it.</summary>
        public static string ToViseme(string phoneme)
        {
            switch (phoneme)
            {
                case "AA": return "aA";
                case "AH": return "ah";
                case "EH": return "eE";
                case "IY":
                case "IH": return "i";
                case "OW": return "o";
                case "UW": return "u";
                case "ER": return "ah";
                case "M":
                case "B":
                case "P": return "mM";
                case "N":
                case "T":
                case "D":
                case "K":
                case "G": return "nN";
                case "L":
                case "R": return "lL";
                case "F":
                case "V": return "fV";
                case "SH":
                case "ZH":
                case "CH":
                case "JH": return "ss";
                case "S":
                case "Z": return "sH";
                case "TH":
                case "DH": return "tH";
                default: return "ah";
            }
        }

        /// <summary>ARPAbet names this analyser can emit.</summary>
        public static readonly string[] Phonemes = {
            "AA", "AH", "EH", "IY", "IH", "OW", "UW", "ER",
            "M", "N", "B", "P", "F", "S", "SH", "T", "silence"
        };

        public static ulong Hash(string name) { return HashDatabase.Crc64(name); }

        /// <summary>Per-frame acoustic features, shared by the classifier and the trainer.</summary>
        public const int MelBands = 12;

        public struct Frame
        {
            public float Rms, Zcr, Centroid, Low, Mid, High, Air;
            /// <summary>Log energies of 12 mel bands from 80 Hz to 6 kHz, the recogniser's main input.</summary>
            public float[] Mel;
        }

        /// <summary>Extracts one feature vector per 10 ms hop.</summary>
        public static Frame[] Frames(AudioData audio)
        {
            if (audio == null || audio.Samples.Length == 0) return new Frame[0];
            int hop = Math.Max(1, audio.SampleRate * HopMs / 1000);
            int n = Math.Max(1, (audio.Samples.Length - FftSize) / hop + 1);
            if (audio.Samples.Length < FftSize) n = 1;

            Frame[] frames = new Frame[n];
            float[] re = new float[FftSize];
            float[] im = new float[FftSize];
            float binHz = audio.SampleRate / (float)FftSize;

            for (int f = 0; f < n; f++)
            {
                int at = f * hop;
                int count = Math.Min(FftSize, audio.Samples.Length - at);
                double sum = 0, zc = 0;
                float prev = 0f;
                for (int i = 0; i < count; i++)
                {
                    float s = audio.Samples[at + i];
                    sum += s * s;
                    if (i > 0 && ((s >= 0f) != (prev >= 0f))) zc++;
                    prev = s;
                    re[i] = s * (0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / (count - 1)));
                    im[i] = 0f;
                }
                for (int i = count; i < FftSize; i++) { re[i] = 0f; im[i] = 0f; }

                Fft(re, im);

                double mag = 0, con = 0;
                float lo = 0, md = 0, hi = 0, ai = 0;
                for (int b = 1; b < FftSize / 2; b++)
                {
                    float hz = b * binHz;
                    double m = Math.Sqrt((double)re[b] * re[b] + (double)im[b] * im[b]);
                    mag += m;
                    con += m * hz;
                    if (hz < 300) lo += (float)m; else if (hz < 900) md += (float)m;
                    else if (hz < 2200) hi += (float)m;
                    else if (hz < 6000) ai += (float)m;
                }
                float total = lo + md + hi + ai + 1e-9f;
                float[] mel = new float[MelBands];
                for (int band = 0; band < MelBands; band++)
                {
                    float loHz = MelToHz(MelFromHz(80f) + (MelFromHz(6000f) - MelFromHz(80f)) * band / MelBands);
                    float hiHz = MelToHz(MelFromHz(80f) + (MelFromHz(6000f) - MelFromHz(80f)) * (band + 1) / MelBands);
                    int b0 = Math.Max(1, (int)(loHz / binHz)), b1 = Math.Max(b0 + 1, (int)(hiHz / binHz));
                    double e = 0;
                    int used = 0;
                    for (int b = b0; b < b1 && b < FftSize / 2; b++)
                    {
                        e += (double)re[b] * re[b] + (double)im[b] * im[b];
                        used++;
                    }
                    mel[band] = (float)Math.Log10(e / Math.Max(1, used) + 1e-8);
                }

                frames[f] = new Frame
                {
                    Mel = mel,
                    Rms = (float)Math.Sqrt(sum / Math.Max(1, count)),
                    Zcr = count > 1 ? (float)(zc / (count - 1)) : 0f,
                    Centroid = mag > 1e-9 ? (float)(con / mag) : 0f,
                    Low = lo / total,
                    Mid = md / total,
                    High = hi / total,
                    Air = ai / total
                };
            }
            return frames;
        }

        /// <summary>
        /// Analyses a whole clip. <paramref name="sensitivity"/> 0..1 scales the speech threshold
        /// (higher splits quieter speech). Returns keys sorted by time, covering speech and pauses.
        /// </summary>
        /// <param name="minKeyMs">Shortest viseme, in milliseconds. The shipped data averages one key per 120 ms.</param>
        /// <param name="smoothing">How many majority-smoothing passes to run over the viseme sequence.</param>
        public static List<LipKey> Analyze(AudioData audio, float sensitivity, float minPause, VisemeModel model = null,
                                           float minKeyMs = 100f, int smoothing = 1, float openScale = 1f)
        {
            List<LipKey> keys = new List<LipKey>();
            if (audio == null || audio.Samples.Length == 0) return keys;

            Frame[] fr = Frames(audio);
            int n = fr.Length;
            float[] rms = new float[n], zcr = new float[n], centroid = new float[n];
            float[] low = new float[n], mid = new float[n], high = new float[n], air = new float[n];
            for (int i = 0; i < n; i++)
            {
                rms[i] = fr[i].Rms; zcr[i] = fr[i].Zcr; centroid[i] = fr[i].Centroid;
                low[i] = fr[i].Low; mid[i] = fr[i].Mid; high[i] = fr[i].High; air[i] = fr[i].Air;
            }
            int hop = Math.Max(1, audio.SampleRate * HopMs / 1000);

            // Voice activity: threshold above the noise floor, relative to the loudest parts.
            float[] db = new float[n];
            for (int i = 0; i < n; i++) db[i] = 20f * (float)Math.Log10(rms[i] + 1e-7f);
            float floor = Percentile(db, 0.2f);
            float peak = Percentile(db, 0.95f);
            float range = Math.Max(6f, peak - floor);
            float thr = floor + range * (0.25f + 0.45f * Clamp01(sensitivity));
            bool[] voiced = new bool[n];
            for (int i = 0; i < n; i++) voiced[i] = db[i] > thr;

            // Bridge short gaps so a phrase does not break up between syllables.
            int bridge = Math.Max(1, (int)(0.15f * 1000f / HopMs));
            for (int i = 0; i < n; i++)
            {
                if (voiced[i]) continue;
                int j = i;
                while (j < n && !voiced[j]) j++;
                if (i > 0 && j < n && j - i <= bridge)
                    for (int k = i; k < j; k++) voiced[k] = true;
                i = j;
            }

            // Split speech into viseme slots, then smooth them: the shipped keys overlap heavily
            // (fade-out runs past the next key) and a mouth that closes inside every slot snaps.
            int minFrames = Math.Max(2, (int)Math.Round(minKeyMs / HopMs));
            int maxFrames = Math.Max(minFrames + 1, (int)Math.Round(190f / HopMs));

            string[] vis = new string[n];               // one viseme per 10 ms frame, null = silence
            List<int[]> pauses = new List<int[]>();     // silent runs, [start, end)
            int frame = 0;
            while (frame < n)
            {
                bool speech = voiced[frame];
                int start = frame;
                while (frame < n && voiced[frame] == speech) frame++;

                if (!speech)
                {
                    float silent = (frame - start) * HopMs / 1000f;
                    if (silent >= Math.Max(0.3f, minPause)) pauses.Add(new int[] { start, frame });
                    continue;
                }

                // Cut the phrase at energy valleys, no slot shorter than minKeyMs.
                List<int> cuts = new List<int>();
                cuts.Add(start);
                int at = start + minFrames;
                while (at < frame - minFrames)
                {
                    int best = at;
                    int lo = at - 20 / HopMs, hi = at + 20 / HopMs;
                    for (int k = Math.Max(start + minFrames, lo); k < Math.Min(frame - minFrames, hi); k++)
                        if (db[k] < db[best]) best = k;
                    if (best - cuts[cuts.Count - 1] < minFrames) best = cuts[cuts.Count - 1] + minFrames;
                    if (best >= frame - minFrames) break;
                    cuts.Add(best);
                    at = best + maxFrames;
                }
                cuts.Add(frame);

                for (int i = 0; i + 1 < cuts.Count; i++)
                {
                    int a = cuts[i], b = cuts[i + 1];
                    if (b <= a) continue;
                    string ph = model != null && model.IsTrained ? model.Classify(SlotFeatures(fr, a, b)) : null;
                    if (ph == null) ph = ToViseme(Classify(a, b, rms, zcr, centroid, low, mid, high, air));
                    for (int k = a; k < b && k < n; k++) vis[k] = ph;
                }
            }

            // Smoothing: a viseme flanked by the same viseme on both sides is noise.
            for (int pass = 0; pass < smoothing; pass++)
            {
                string[] copy = (string[])vis.Clone();
                for (int i = 1; i + 1 < n; i++)
                    if (copy[i] != null && copy[i - 1] != null && copy[i + 1] != null
                        && copy[i - 1] == copy[i + 1] && copy[i] != copy[i - 1])
                        vis[i] = copy[i - 1];
            }

            // Absorb runs that are still too short into the longer neighbour.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (vis[i] == null) continue;
                    int a = i;
                    while (i < n && vis[i] == vis[a]) i++;
                    int b = i;
                    if (b - a >= minFrames) continue;
                    int left = a - 1, right = b;
                    bool hasL = left >= 0 && vis[left] != null, hasR = right < n && vis[right] != null;
                    if (!hasL && !hasR) continue;
                    string take = hasL && hasR ? (TakeViseme(vis, left, -1) == TakeViseme(vis, right, 1) ? vis[left] : vis[left]) : hasL ? vis[left] : vis[right];
                    for (int k = a; k < b; k++) vis[k] = take;
                }
            }

            // Loudness, smoothed over a quarter second so openness does not jump either.
            float p95 = Math.Max(1e-5f, Percentile(rms, 0.95f));
            float[] loud = new float[n];
            const int LoudWin = 6;
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int c = 0;
                for (int k = Math.Max(0, i - LoudWin); k <= Math.Min(n - 1, i + LoudWin); k++) { sum += rms[k]; c++; }
                loud[i] = Math.Min(1f, sum / Math.Max(1, c) / p95);
            }

            // One key per viseme run, with the shipped fades: short hold, long overlapping fades.
            for (int i = 0; i < n; i++)
            {
                if (vis[i] == null) continue;
                int a = i;
                while (i < n && vis[i] == vis[a]) i++;
                int b = i;
                float dur = (b - a) * HopMs / 1000f;
                float t0 = a * HopMs / 1000f;
                float fadeIn = Clamp(dur * 0.6f, 0.04f, 0.12f);
                float fadeOut = Clamp(dur * 0.9f, 0.07f, 0.25f);
                float hold = Math.Max(0.01f, dur * 0.25f);
                float contribution = Clamp01((0.55f + 0.30f * loud[Math.Min(n - 1, (a + b) / 2)]) * openScale);
                keys.Add(new LipKey
                {
                    Time = t0,
                    FadeIn = fadeIn,
                    Hold = hold,
                    FadeOut = fadeOut,
                    Contribution = contribution,
                    Phoneme = Hash(vis[a])
                });
            }
            for (int i = 0; i < pauses.Count; i++) keys.Add(PauseKey(pauses[i][0] * HopMs / 1000f, (pauses[i][1] - pauses[i][0]) * HopMs / 1000f));

            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            return keys;
        }

        static string TakeViseme(string[] vis, int at, int dir)
        {
            while (at >= 0 && at < vis.Length && vis[at] == null) at += dir;
            return at >= 0 && at < vis.Length ? vis[at] : null;
        }

        static LipKey PauseKey(float t, float dur)
        {
            return new LipKey
            {
                Time = t,
                FadeIn = Clamp(dur * 0.4f, 0.04f, 0.12f),
                FadeOut = Clamp(dur * 0.6f, 0.06f, 0.20f),
                Hold = Math.Max(0.01f, dur * 0.3f),
                Contribution = 0f,
                Phoneme = Hash("default")
            };
        }

        /// <summary>Average acoustic features of frames [a, b): mel bands, ZCR, centroid and loudness.</summary>
        public static float[] SlotFeatures(Frame[] fr, int a, int b)
        {
            if (fr == null || fr.Length == 0) return null;
            if (a < 0) a = 0;
            if (b > fr.Length) b = fr.Length;
            if (b <= a) return null;
            int dim = MelBands + 3;
            float[] v = new float[dim];
            for (int i = a; i < b; i++)
            {
                for (int k = 0; k < MelBands; k++) v[k] += fr[i].Mel[k];
                v[MelBands] += fr[i].Zcr;
                v[MelBands + 1] += fr[i].Centroid / 1000f;
                v[MelBands + 2] += (float)Math.Log10(fr[i].Rms + 1e-4f);
            }
            for (int k = 0; k < dim; k++) v[k] /= (b - a);
            return v;
        }

        /// <summary>Resolves a stored phoneme hash to a viseme name, or null when it is not one.</summary>
        public static string VisemeName(ulong hash)
        {
            for (int i = 0; i < Visemes.Length; i++) if (Hash(Visemes[i]) == hash) return Visemes[i];
            return null;
        }

        static LipKey SpeechKey(float t, float dur, string phoneme, float contribution)
        {
            float fi = Math.Min(0.045f, dur * 0.3f);
            float fo = Math.Min(0.06f, dur * 0.3f);
            return new LipKey
            {
                Time = t,
                FadeIn = fi,
                FadeOut = fo,
                Hold = Math.Max(0.01f, dur - fi - fo),
                Contribution = contribution,
                Phoneme = Hash(phoneme)
            };
        }

        static string Classify(int a, int b, float[] rms, float[] zcr, float[] centroid,
                               float[] low, float[] mid, float[] high, float[] air)
        {
            float z = 0f, c = 0f, lo = 0f, md = 0f, hi = 0f, ai = 0f;
            int n = 0;
            for (int i = a; i < b; i++)
            {
                z += zcr[i]; c += centroid[i];
                lo += low[i]; md += mid[i]; hi += high[i]; ai += air[i];
                n++;
            }
            if (n == 0) return "silence";
            z /= n; c /= n; lo /= n; md /= n; hi /= n; ai /= n;

            // Fricatives: lots of air, high crossing rate, little low energy.
            if (ai > 0.34f && z > 0.18f && lo < 0.25f)
            {
                if (c > 4200f || ai > 0.5f) return "S";
                if (c > 2600f) return "SH";
                return "F";
            }
            // Stops and nasals: low band dominates, quiet top end.
            if (lo > 0.42f && z < 0.14f)
            {
                if (md > hi) return "M";
                return c < 600f ? "N" : "B";
            }
            // Vowels, roughly by where the energy sits in the F1 / F2 regions.
            float f2 = hi + ai;              // crude F2 proxy
            if (md > 0.42f && f2 < 0.18f) return "OW";
            if (md < 0.22f && f2 < 0.2f) return "UW";
            if (f2 > 0.42f && md < 0.35f) return "IY";
            if (f2 > 0.32f) return "EH";
            if (md > 0.38f) return "AA";
            return "AH";
        }

        static float Percentile(float[] values, float q)
        {
            if (values.Length == 0) return 0f;
            float[] copy = (float[])values.Clone();
            Array.Sort(copy);
            int i = (int)(q * (copy.Length - 1));
            return copy[Math.Max(0, Math.Min(copy.Length - 1, i))];
        }

        static float MelFromHz(float hz) { return 2595f * (float)Math.Log10(1f + hz / 700f); }
        static float MelToHz(float mel) { return 700f * ((float)Math.Pow(10f, mel / 2595f) - 1f); }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }

        static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }

        /// <summary>In-place iterative radix-2 FFT.</summary>
        static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1f, ci = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int p = i + k, q = i + k + len / 2;
                        float xr = re[q] * cr - im[q] * ci;
                        float xi = re[q] * ci + im[q] * cr;
                        re[q] = re[p] - xr; im[q] = im[p] - xi;
                        re[p] += xr; im[p] += xi;
                        float nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }
    }

    sealed class InvalidDataException : Exception
    {
        public InvalidDataException(string message) : base(message) { }
    }
}
