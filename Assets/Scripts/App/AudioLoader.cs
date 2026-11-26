using System;
using System.Collections;
using System.IO;
using TFM.Core.Lipsync;
using UnityEngine;
using UnityEngine.Networking;

namespace TFM.App
{
    /// <summary>
    /// Reads audio from disk for the lipsync generator: RIFF WAV is parsed here, MP3 and OGG are
    /// decoded by Unity's own audio backend. Telltale voice files are often FSB5 sound banks with a
    /// .wav extension; those are reported instead of being fed to the WAV parser.
    /// </summary>
    public static class AudioLoader
    {
        public static readonly string[] Extensions = { ".wav", ".mp3", ".ogg" };

        sealed class Host : MonoBehaviour { }
        static Host _host;

        public static bool IsSupported(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            for (int i = 0; i < Extensions.Length; i++) if (Extensions[i] == ext) return true;
            return false;
        }

        /// <summary>True when the file is an FSB5 sound bank, which needs extracting before use.</summary>
        public static bool IsFsb5(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = new byte[4];
                    if (fs.Read(h, 0, 4) < 4) return false;
                    return h[0] == 'F' && h[1] == 'S' && h[2] == 'B' && h[3] == '5';
                }
            }
            catch { return false; }
        }

        /// <summary>Decodes a file. The callback always runs, either with data or with an error message.</summary>
        public static void Load(string path, Action<AudioData, string> done)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { done(null, "File not found: " + path); return; }
            if (IsFsb5(path))
            {
                done(null, Path.GetFileName(path) + " is an FSB5 sound bank, not a WAV. Extract it to a real .wav first.");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".wav")
            {
                try { done(AudioData.FromWav(File.ReadAllBytes(path)), null); }
                catch (Exception e) { done(null, Path.GetFileName(path) + ": " + e.Message); }
                return;
            }
            if (ext == ".mp3" || ext == ".ogg")
            {
                if (_host == null)
                {
                    GameObject go = new GameObject("TFM AudioLoader");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _host = go.AddComponent<Host>();
                }
                _host.StartCoroutine(LoadClip(path, ext, done));
                return;
            }
            done(null, "Unsupported audio type " + ext + " (use WAV, MP3 or OGG).");
        }

        static IEnumerator LoadClip(string path, string ext, Action<AudioData, string> done)
        {
            AudioType type = ext == ".mp3" ? AudioType.MPEG : AudioType.OGGVORBIS;
            string uri;
            try { uri = new Uri(path).AbsoluteUri; }
            catch (Exception e) { done(null, "Bad path: " + e.Message); yield break; }

            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, type))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    done(null, Path.GetFileName(path) + ": " + req.error);
                    yield break;
                }
                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null || clip.samples <= 0)
                {
                    done(null, Path.GetFileName(path) + ": could not decode audio.");
                    yield break;
                }
                float[] data = new float[(long)clip.samples * clip.channels];
                clip.GetData(data, 0);
                AudioData audio = AudioData.FromInterleaved(data, clip.channels, clip.frequency);
                UnityEngine.Object.Destroy(clip);
                done(audio, null);
            }
        }
    }
}
