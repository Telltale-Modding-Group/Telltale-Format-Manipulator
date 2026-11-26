using System;
using System.Collections.Generic;
using System.IO;
using TFM.Core;
using TFM.Core.Lipsync;

namespace TFM.Core.Meta
{
    /// <summary>What a keyframed track animates, decided by its value type.</summary>
    public enum AnmTrackKind
    {
        Other,
        /// <summary>Viseme keys (lipsync): mPhoneme plus fade and hold times.</summary>
        Phoneme,
        /// <summary>Bone transform: rotation and translation.</summary>
        Transform,
        /// <summary>Location info (position/orientation of an object or agent).</summary>
        LocationInfo,
        Scalar,
        Toggle,
        Vector,
        /// <summary>A compressed skeleton pose: quantized sample data, not decoded yet.</summary>
        Pose
    }

    /// <summary>One keyframe of a track (a <c>KeyframedValue&lt;T&gt;::Sample</c>).</summary>
    public sealed class AnmSample
    {
        public readonly MetaValue Node;

        public AnmSample(MetaValue node) { Node = node; }

        public float Time
        {
            get { MetaValue t = Node["mTime"]; return t != null ? t.AsFloat : 0f; }
            set { MetaValue t = Node["mTime"]; if (t != null) t.AsFloat = value; }
        }

        public bool InterpolateToNext
        {
            get { MetaValue b = Node["mbInterpolateToNextKey"]; return b == null || b.AsBool; }
            set { MetaValue b = Node["mbInterpolateToNextKey"]; if (b != null) b.AsBool = value; }
        }

        public int TangentMode
        {
            get { MetaValue t = Node["mTangentMode"]; return t != null ? t.AsInt : 0; }
            set { MetaValue t = Node["mTangentMode"]; if (t != null) t.AsInt = value; }
        }

        /// <summary>The animated value: PhonemeKey, Transform, float, bool, Vector3 or LocationInfo.</summary>
        public MetaValue Value { get { return Node["mValue"]; } }

        public float RecipTimeToNextSample
        {
            get { MetaValue v = Node["mRecipTimeToNextSample"]; return v != null ? v.AsFloat : 0f; }
            set { MetaValue v = Node["mRecipTimeToNextSample"]; if (v != null) v.AsFloat = value; }
        }

        public bool HasRecip { get { return Node["mRecipTimeToNextSample"] != null; } }
    }

    /// <summary>One entry of the animation's value list: a keyframed track or a compressed pose.</summary>
    public sealed class AnmTrack
    {
        public readonly MetaValue Node;
        public readonly string Type;
        /// <summary>Template argument of the value type (Transform, PhonemeKey, float, ...), or null.</summary>
        public readonly string Element;
        public AnmTrackKind Kind;
        public readonly List<AnmSample> Samples = new List<AnmSample>();

        /// <summary>Index of this track in the file's value list; the name fallback uses it.</summary>
        public int Index;

        internal AnmDocument Doc;

        public AnmTrack(MetaValue node)
        {
            Node = node;
            Type = node.Type;
            Element = TemplateArgument(Type);
            Kind = KindOf(Type, Element);

            MetaValue samples = node["mSamples"];
            if (samples != null)
                for (int i = 0; i < samples.Items.Count; i++) Samples.Add(new AnmSample(samples.Items[i]));
        }

        public bool IsPose { get { return Kind == AnmTrackKind.Pose; } }

        /// <summary>Name hash stored on the value (mName), or the value-list name array entry.</summary>
        public ulong Name
        {
            get
            {
                MetaValue nm = BaseNameField();
                if (nm != null) return nm.Bits;
                return Doc != null ? Doc.StoredName(Index) : 0UL;
            }
            set
            {
                MetaValue nm = BaseNameField();
                if (nm != null) { nm.Bits = value; return; }
                if (Doc != null) Doc.SetStoredName(Index, value);
            }
        }

        /// <summary>The AnimationValueInterfaceBase block that carries mName / mFlags, if this value has one.</summary>
        public MetaValue BaseClassNode
        {
            get
            {
                for (int i = 0; i < Node.Fields.Count; i++)
                {
                    MetaValue inner = Node.Fields[i].Value;
                    if (inner == null || inner.Kind != MetaKind.Struct) continue;
                    MetaValue b = inner.FindField("Baseclass_AnimationValueInterfaceBase");
                    if (b != null) return b;
                }
                return null;
            }
        }

        MetaValue BaseNameField()
        {
            MetaValue b = BaseClassNode;
            return b != null ? b.FindField("mName") : null;
        }

        public int Flags
        {
            get
            {
                MetaValue b = BaseClassNode;
                MetaValue f = b != null ? b.FindField("mFlags") : null;
                return f != null ? f.AsInt : 0;
            }
        }

        public float StartTime { get { return Samples.Count > 0 ? Samples[0].Time : 0f; } }

        public float EndTime
        {
            get
            {
                float e = 0f;
                for (int i = 0; i < Samples.Count; i++) if (Samples[i].Time > e) e = Samples[i].Time;
                return e;
            }
        }

        /// <summary>Type name without the template argument, e.g. "KeyframedValue".</summary>
        public string ShortType
        {
            get
            {
                int lt = Type.IndexOf('<');
                return lt < 0 ? Type : Type.Substring(0, lt);
            }
        }

        static string TemplateArgument(string t)
        {
            int a = t.IndexOf('<');
            int b = t.LastIndexOf('>');
            return a >= 0 && b > a ? t.Substring(a + 1, b - a - 1) : null;
        }

        static AnmTrackKind KindOf(string type, string element)
        {
            if (type.StartsWith("CompressedSkeletonPoseKeys", StringComparison.Ordinal)) return AnmTrackKind.Pose;
            if (element == "PhonemeKey") return AnmTrackKind.Phoneme;
            if (element == "Transform") return AnmTrackKind.Transform;
            if (element == "LocationInfo") return AnmTrackKind.LocationInfo;
            if (element == "Vector3") return AnmTrackKind.Vector;
            if (element == "float" || element == "double") return AnmTrackKind.Scalar;
            if (element == "bool") return AnmTrackKind.Toggle;
            return AnmTrackKind.Other;
        }
    }

    /// <summary>
    /// A loaded .anm animation. Wraps the decoded MetaStream tree and edits it in place: samples,
    /// track names, added/removed tracks. Saving writes the whole file back through the codec.
    /// </summary>
    public sealed class AnmDocument
    {
        public MetaFile File;
        public MetaValue Root;
        public readonly List<AnmTrack> Tracks = new List<AnmTrack>();

        /// <summary>Name of the file this was loaded from, or null for a new document.</summary>
        public string SourcePath { get { return File.SourcePath; } }

        public float Length
        {
            get { MetaValue l = Root["mLength"]; return l != null ? l.AsFloat : 0f; }
            set { MetaValue l = Root["mLength"]; if (l != null) l.AsFloat = value; }
        }

        public ulong RootName
        {
            get { MetaValue n = Root["mName"]; return n != null ? n.Bits : 0UL; }
            set { MetaValue n = Root["mName"]; if (n != null) n.Bits = value; }
        }

        public int Version
        {
            get { MetaValue v = Root["mVersion"]; return v != null ? v.AsInt : 0; }
        }

        public static AnmDocument Load(string path)
        {
            MetaFile f = MetaFile.Load(path, "Animation");
            AnmDocument d = From(f);
            d.File.SourcePath = path;
            return d;
        }

        public static AnmDocument Parse(byte[] data, string sourcePath)
        {
            MetaFile f = MetaFile.Parse(data, "Animation", MetaLayouts.Current);
            f.SourcePath = sourcePath;
            return From(f);
        }

        public static AnmDocument From(MetaFile file)
        {
            AnmDocument d = new AnmDocument();
            d.File = file;
            d.Root = file.Root;
            d.RebuildTracks();
            return d;
        }

        void RebuildTracks()
        {
            Tracks.Clear();
            MetaValue vals = Root["#values"];
            MetaValue list = vals != null ? vals["values"] : null;
            if (list == null) return;
            for (int i = 0; i < list.Items.Count; i++)
            {
                AnmTrack t = new AnmTrack(list.Items[i]);
                t.Index = i;
                t.Doc = this;
                Tracks.Add(t);
            }
        }

        MetaValue ValueList { get { MetaValue v = Root["#values"]; return v != null ? v["values"] : null; } }

        /// <summary>Name from the value list's trailing name array (used by values with no mName field).</summary>
        public ulong StoredName(int index)
        {
            MetaValue vals = Root["#values"];
            MetaValue names = vals != null ? vals["names"] : null;
            if (names == null || index < 0 || index >= names.Items.Count) return 0UL;
            return names.Items[index].Bits;
        }

        public void SetStoredName(int index, ulong hash)
        {
            MetaValue vals = Root["#values"];
            MetaValue names = vals != null ? vals["names"] : null;
            if (names == null || index < 0 || index >= names.Items.Count) return;
            names.Items[index].Bits = hash;
        }

        // ------------------------------------------------------------- editing
        public void SetLength(float seconds) { Length = seconds; }

        /// <summary>Keeps samples sorted by time and refreshes the derived fields the game reads.</summary>
        public void SortSamples(AnmTrack t)
        {
            MetaValue arr = t.Node["mSamples"];
            if (arr == null) return;
            t.Samples.Sort((a, b) => a.Time.CompareTo(b.Time));
            arr.Items.Clear();
            for (int i = 0; i < t.Samples.Count; i++) arr.Items.Add(t.Samples[i].Node);
            RefreshRecip(t);
        }

        /// <summary>Rebuilds mRecipTimeToNextSample where the format stores it.</summary>
        public void RefreshRecip(AnmTrack t)
        {
            for (int i = 0; i < t.Samples.Count; i++)
            {
                if (!t.Samples[i].HasRecip) continue;
                float dt = i + 1 < t.Samples.Count ? t.Samples[i + 1].Time - t.Samples[i].Time : 0f;
                t.Samples[i].RecipTimeToNextSample = Math.Abs(dt) > 1e-9f ? 1f / dt : 0f;
            }
        }

        public void RefreshAllDerived()
        {
            for (int i = 0; i < Tracks.Count; i++) RefreshRecip(Tracks[i]);
        }

        /// <summary>
        /// Adds a keyframe to a track at the given time. The new sample is a copy of the nearest
        /// existing one (or of any sample of the same type in the file), so its exact field set
        /// matches the file's version. Returns null when no template exists.
        /// </summary>
        public AnmSample AddSample(AnmTrack track, float time)
        {
            MetaValue arr = track.Node["mSamples"];
            if (arr == null) return null;
            AnmSample template = NearestSample(track, time);
            if (template == null) template = AnySampleOfType(track.Type);
            if (template == null) return null;

            MetaValue node = template.Node.Clone();
            AnmSample s = new AnmSample(node);
            s.Time = time;
            track.Samples.Add(s);
            arr.Items.Add(node);
            SortSamples(track);
            return s;
        }

        AnmSample NearestSample(AnmTrack track, float time)
        {
            AnmSample best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < track.Samples.Count; i++)
            {
                float d = Math.Abs(track.Samples[i].Time - time);
                if (d < bestD) { bestD = d; best = track.Samples[i]; }
            }
            return best;
        }

        AnmSample AnySampleOfType(string type)
        {
            for (int i = 0; i < Tracks.Count; i++)
                if (Tracks[i].Type == type && Tracks[i].Samples.Count > 0) return Tracks[i].Samples[0];
            return null;
        }

        public void RemoveSample(AnmTrack track, AnmSample sample)
        {
            MetaValue arr = track.Node["mSamples"];
            if (arr == null) return;
            arr.Items.Remove(sample.Node);
            track.Samples.Remove(sample);
            RefreshRecip(track);
        }

        /// <summary>
        /// Duplicates a track under a new name, which is how a bone is added to an animation.
        /// The copy keeps the original's exact class version so it serializes like the rest.
        /// </summary>
        public AnmTrack DuplicateTrack(AnmTrack template, ulong nameHash)
        {
            MetaValue list = ValueList;
            if (list == null || template == null) return null;
            MetaValue node = template.Node.Clone();
            int at = list.Items.IndexOf(template.Node);
            list.Items.Insert(at + 1, node);

            AnmTrack t = new AnmTrack(node);
            t.Name = nameHash;
            RebuildTracks();
            for (int i = 0; i < Tracks.Count; i++) if (Tracks[i].Node == node) return Tracks[i];
            return t;
        }

        /// <summary>Creates a track of an existing type with one keyframe at 0, sharing another track's shape.</summary>
        public AnmTrack AddTrack(string type, ulong nameHash, float length)
        {
            for (int i = 0; i < Tracks.Count; i++)
                if (Tracks[i].Type == type) return DuplicateTrack(Tracks[i], nameHash);
            return null;
        }

        public void RemoveTrack(AnmTrack track)
        {
            MetaValue list = ValueList;
            if (list == null || track == null) return;
            list.Items.Remove(track.Node);
            RebuildTracks();
        }

        /// <summary>
        /// Replaces every keyframe of a phoneme track with generated ones. The first existing sample
        /// is used as the prototype, so the new keys carry the file's exact sample class version.
        /// Also sets the animation length when <paramref name="newLength"/> is positive.
        /// </summary>
        public bool ReplacePhonemeKeys(AnmTrack track, IList<LipKey> keys, float newLength)
        {
            if (track == null || track.Kind != AnmTrackKind.Phoneme) return false;
            MetaValue arr = track.Node["mSamples"];
            if (arr == null) return false;
            AnmSample protoSample = track.Samples.Count > 0 ? track.Samples[0] : AnySampleOfType(track.Type);
            if (protoSample == null) return false;
            MetaValue proto = protoSample.Node.Clone();

            arr.Items.Clear();
            track.Samples.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                LipKey k = keys[i];
                MetaValue node = proto.Clone();
                AnmSample s = new AnmSample(node);
                s.Time = k.Time;
                SetPhoneme(s.Value, k.Phoneme, k.FadeIn, k.Hold, k.FadeOut, k.Contribution);
                arr.Items.Add(node);
                track.Samples.Add(s);
            }
            SortSamples(track);
            if (newLength > 0f) Length = newLength;
            return true;
        }

        /// <summary>The first phoneme track of the file, or null.</summary>
        public AnmTrack FirstPhonemeTrack()
        {
            for (int i = 0; i < Tracks.Count; i++) if (Tracks[i].Kind == AnmTrackKind.Phoneme) return Tracks[i];
            return null;
        }

        public void Save(string path)
        {
            RefreshAllDerived();
            File.Save(path);
        }

        public byte[] Serialize()
        {
            RefreshAllDerived();
            return File.Serialize();
        }

        // ------------------------------------------------------------- typed access
        /// <summary>Reads a Transform value's rotation and translation.</summary>
        public static bool TryGetTransform(MetaValue v, out Quat rot, out Vec3 trans)
        {
            rot = Quat.Identity;
            trans = new Vec3();
            if (v == null) return false;
            MetaValue r = v.FindField("mRot");
            MetaValue t = v.FindField("mTrans");
            if (r == null || t == null) return false;
            rot = new Quat(Comp(r, "x").AsFloat, Comp(r, "y").AsFloat, Comp(r, "z").AsFloat, Comp(r, "w").AsFloat);
            trans = new Vec3(Comp(t, "x").AsFloat, Comp(t, "y").AsFloat, Comp(t, "z").AsFloat);
            return true;
        }

        public static bool SetTransform(MetaValue v, Quat rot, Vec3 trans)
        {
            if (v == null) return false;
            MetaValue r = v.FindField("mRot");
            MetaValue t = v.FindField("mTrans");
            if (r == null || t == null) return false;
            Comp(r, "x").AsFloat = rot.X;
            Comp(r, "y").AsFloat = rot.Y;
            Comp(r, "z").AsFloat = rot.Z;
            Comp(r, "w").AsFloat = rot.W;
            Comp(t, "x").AsFloat = trans.X;
            Comp(t, "y").AsFloat = trans.Y;
            Comp(t, "z").AsFloat = trans.Z;
            return true;
        }

        public static bool TryGetVector(MetaValue v, out Vec3 vec)
        {
            vec = new Vec3();
            if (v == null) return false;
            MetaValue x = v.FindField("x"), y = v.FindField("y"), z = v.FindField("z");
            if (x == null || y == null || z == null) return false;
            vec = new Vec3(x.AsFloat, y.AsFloat, z.AsFloat);
            return true;
        }

        public static bool SetVector(MetaValue v, Vec3 vec)
        {
            if (v == null) return false;
            MetaValue x = v.FindField("x"), y = v.FindField("y"), z = v.FindField("z");
            if (x == null || y == null || z == null) return false;
            x.AsFloat = vec.X;
            y.AsFloat = vec.Y;
            z.AsFloat = vec.Z;
            return true;
        }

        /// <summary>Phoneme key fields.</summary>
        public static bool TryGetPhoneme(MetaValue v, out ulong phoneme, out float fadeIn, out float hold, out float fadeOut, out float contribution)
        {
            phoneme = 0; fadeIn = hold = fadeOut = 0f; contribution = 1f;
            if (v == null) return false;
            MetaValue p = v.FindField("mPhoneme");
            if (p == null) return false;
            phoneme = p.Bits;
            fadeIn = Float(v, "mFadeInTime");
            hold = Float(v, "mHoldTime");
            fadeOut = Float(v, "mFadeOutTime");
            contribution = Float(v, "mTargetContribution");
            return true;
        }

        public static void SetPhoneme(MetaValue v, ulong phoneme, float fadeIn, float hold, float fadeOut, float contribution)
        {
            MetaValue p = v != null ? v.FindField("mPhoneme") : null;
            if (p == null) return;
            p.Bits = phoneme;
            MetaValue f;
            if ((f = v.FindField("mFadeInTime")) != null) f.AsFloat = fadeIn;
            if ((f = v.FindField("mHoldTime")) != null) f.AsFloat = hold;
            if ((f = v.FindField("mFadeOutTime")) != null) f.AsFloat = fadeOut;
            if ((f = v.FindField("mTargetContribution")) != null) f.AsFloat = contribution;
        }

        static float Float(MetaValue v, string name)
        {
            MetaValue f = v.FindField(name);
            return f != null ? f.AsFloat : 0f;
        }

        static MetaValue Comp(MetaValue v, string name)
        {
            MetaValue f = v.FindField(name);
            if (f == null) throw new MetaParseException("Missing field " + name + " in " + v.Type);
            return f;
        }
    }
}
