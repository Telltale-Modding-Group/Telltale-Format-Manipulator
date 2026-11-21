using System;
using System.Collections.Generic;

namespace TFM.Core
{
    // =====================================================================================
    // Compiled material data (the value of a T3MaterialData property).
    //
    // Layout, as measured on the TWD Definitive reference props. Every container and every
    // bitset is a "block": a u32 byte size that counts itself, followed by the body.
    //
    //   u64 material name, u64 runtime-properties name,
    //   u64 legacy render-texture property, u64 legacy blend-mode property, i32 domain
    //   block: u32 n, n x (u64 property key, u64 runtime name)          runtime properties
    //   u32 flags, i32 version
    //   block: u32 n, n x compiled variant (one per quality level)
    //
    // A compiled variant is eight counted arrays (parameters, textures, transforms, nested
    // materials, pre-shaders, static parameters, texture parameters, passes), then i32 quality,
    // six bitsets, a size-prefixed pre-shader byte buffer, u32 flags and three u32 buffer sizes.
    //
    // The block holds no values: parameter values and texture files live in the ordinary
    // properties of the same file, keyed by the names listed here.
    // =====================================================================================

    public sealed class MatRuntimeProperty
    {
        public ulong Name;
        public ulong RuntimeName;
    }

    public sealed class MatParameter
    {
        public ulong Name;
        public int PropertyType;
        public int ValueType;
        public uint Flags;
        public int ScalarOffset;
        public int PreShaderScalarOffset;
        public int NestedMaterialIndex;
    }

    public sealed class MatTexture
    {
        public ulong Name;
        public ulong TextureName;
        public ulong TextureNameWithoutExtension;
        public int Layout;
        public int PropertyType;
        public uint[] TextureTypes = new uint[1];
        public uint FirstParamIndex;
        public uint ParamCount;
        public int TextureIndex;
        public int NestedMaterialIndex;
    }

    public sealed class MatTransform
    {
        public ulong ParameterPrefix;
        public uint Flags;
        public int ScalarOffset0;
        public int ScalarOffset1;
        public int PreShaderScalarOffset0;
        public int PreShaderScalarOffset1;
        public int NestedMaterialIndex;
    }

    public sealed class MatPreShader
    {
        public int ValueType;
        public uint Flags;
        public int PreShaderOffset;
        public int ScalarParameterOffset;
    }

    public sealed class MatStaticParameter
    {
        public ulong Name;
        public int NestedMaterialIndex;
    }

    public sealed class MatPass
    {
        public int PassType;
        public int BlendMode;
        public ulong MaterialCrc;
    }

    /// <summary>
    /// A counted array whose element layout is not confirmed by any reference file. It is kept
    /// as raw bytes (count word excluded), so a file holding one still round-trips exactly.
    /// </summary>
    public sealed class MatRawArray
    {
        public uint Count;
        public byte[] Body = new byte[0];
    }

    public sealed class MatCompiled
    {
        public List<MatParameter> Parameters = new List<MatParameter>();
        public List<MatTexture> Textures = new List<MatTexture>();
        public List<MatTransform> Transforms = new List<MatTransform>();
        public MatRawArray NestedMaterials = new MatRawArray();
        public List<MatPreShader> PreShaders = new List<MatPreShader>();
        public List<MatStaticParameter> StaticParameters = new List<MatStaticParameter>();
        public MatRawArray TextureParams = new MatRawArray();
        public List<MatPass> Passes = new List<MatPass>();
        public int Quality;
        public uint[] BlendModes = new uint[1];
        public uint[] PassMask = new uint[1];
        public uint[] Channels = new uint[2];
        public uint[] ShaderInputs = new uint[3];
        public uint[] SceneTextures = new uint[1];
        public uint[] OptionalPropertyTypes = new uint[1];
        public byte[] PreShaderBuffer = new byte[0];
        public uint Flags;
        public uint ParameterBufferScalarSize0;
        public uint ParameterBufferScalarSize1;
        public uint PreShaderParameterBufferScalarSize;
    }

    public sealed class PropMaterial
    {
        /// <summary>CRC-64 of "T3MaterialData", the property type that holds this block.</summary>
        public const ulong TypeHash = 0xE2BA743E952F9338UL;

        public ulong MaterialName;
        public ulong RuntimePropertiesName;
        public ulong LegacyRenderTextureProperty;
        public ulong LegacyBlendModeRuntimeProperty;
        public int Domain;
        public List<MatRuntimeProperty> RuntimeProperties = new List<MatRuntimeProperty>();
        public uint Flags;
        public int Version;
        public List<MatCompiled> Compiled = new List<MatCompiled>();

        /// <summary>
        /// Type names a file needs in its header table to hold a material. Hashes are CRC-64 of
        /// the names, so they are computed instead of stored.
        /// </summary>
        public static readonly string[] HeaderTypeNames =
        {
            "T3MaterialData", "T3MaterialRuntimeProperty", "T3MaterialCompiledData",
            "T3MaterialParameter", "T3MaterialTexture", "T3MaterialTransform2D",
            "T3MaterialNestedMaterial", "T3MaterialPreShader", "T3MaterialStaticParameter",
            "T3MaterialTextureParam", "T3MaterialPassData", "Flags", "Symbol"
        };

        // ================================================================ reading
        /// <summary>Decodes a material value. Throws PropParseException when it does not fit.</summary>
        public static PropMaterial Parse(byte[] value)
        {
            if (value == null) throw new PropParseException("No material data.");
            Reader r = new Reader(value, 0, value.Length);
            PropMaterial m = ReadMaterial(r);
            if (r.Pos != value.Length)
                throw new PropParseException("Material data has " + (value.Length - r.Pos) + " unexplained trailing byte(s).");
            return m;
        }

        /// <summary>
        /// Measures a material value starting at <paramref name="p"/>. Returns its byte length,
        /// or -1 with a reason when the bytes do not form a material.
        /// </summary>
        public static int Measure(byte[] d, int p, int end, out string why)
        {
            why = null;
            try
            {
                Reader r = new Reader(d, p, end);
                ReadMaterial(r);
                return r.Pos - p;
            }
            catch (PropParseException e)
            {
                why = "material data: " + e.Message;
                return -1;
            }
        }

        /// <summary>Like Parse, but returns null instead of throwing.</summary>
        public static PropMaterial TryParse(byte[] value, out string why)
        {
            why = null;
            try { return Parse(value); }
            catch (PropParseException e) { why = e.Message; return null; }
        }

        static PropMaterial ReadMaterial(Reader r)
        {
            PropMaterial m = new PropMaterial();
            m.MaterialName = r.U64();
            m.RuntimePropertiesName = r.U64();
            m.LegacyRenderTextureProperty = r.U64();
            m.LegacyBlendModeRuntimeProperty = r.U64();
            m.Domain = r.I32();

            int end = r.OpenBlock("runtime properties");
            uint n = r.Count(16, end);
            for (uint i = 0; i < n; i++)
            {
                MatRuntimeProperty rp = new MatRuntimeProperty();
                rp.Name = r.U64();
                rp.RuntimeName = r.U64();
                m.RuntimeProperties.Add(rp);
            }
            r.CloseBlock(end, "runtime properties");

            m.Flags = r.U32();
            m.Version = r.I32();

            end = r.OpenBlock("compiled data");
            n = r.Count(8, end);
            for (uint i = 0; i < n; i++) m.Compiled.Add(ReadCompiled(r));
            r.CloseBlock(end, "compiled data");
            return m;
        }

        static MatCompiled ReadCompiled(Reader r)
        {
            MatCompiled c = new MatCompiled();

            int end = r.OpenBlock("parameters");
            uint n = r.Count(32, end);
            for (uint i = 0; i < n; i++)
            {
                MatParameter p = new MatParameter();
                p.Name = r.U64();
                p.PropertyType = r.I32();
                p.ValueType = r.I32();
                p.Flags = r.U32();
                p.ScalarOffset = r.I32();
                p.PreShaderScalarOffset = r.I32();
                p.NestedMaterialIndex = r.I32();
                c.Parameters.Add(p);
            }
            r.CloseBlock(end, "parameters");

            end = r.OpenBlock("textures");
            n = r.Count(56, end);
            for (uint i = 0; i < n; i++)
            {
                MatTexture t = new MatTexture();
                t.Name = r.U64();
                t.TextureName = r.U64();
                t.TextureNameWithoutExtension = r.U64();
                t.Layout = r.I32();
                t.PropertyType = r.I32();
                t.TextureTypes = r.BitSet(1, "texture types");
                t.FirstParamIndex = r.U32();
                t.ParamCount = r.U32();
                t.TextureIndex = r.I32();
                t.NestedMaterialIndex = r.I32();
                c.Textures.Add(t);
            }
            r.CloseBlock(end, "textures");

            end = r.OpenBlock("transforms");
            n = r.Count(32, end);
            for (uint i = 0; i < n; i++)
            {
                MatTransform x = new MatTransform();
                x.ParameterPrefix = r.U64();
                x.Flags = r.U32();
                x.ScalarOffset0 = r.I32();
                x.ScalarOffset1 = r.I32();
                x.PreShaderScalarOffset0 = r.I32();
                x.PreShaderScalarOffset1 = r.I32();
                x.NestedMaterialIndex = r.I32();
                c.Transforms.Add(x);
            }
            r.CloseBlock(end, "transforms");

            c.NestedMaterials = r.RawArray("nested materials");

            end = r.OpenBlock("pre-shaders");
            n = r.Count(16, end);
            for (uint i = 0; i < n; i++)
            {
                MatPreShader s = new MatPreShader();
                s.ValueType = r.I32();
                s.Flags = r.U32();
                s.PreShaderOffset = r.I32();
                s.ScalarParameterOffset = r.I32();
                c.PreShaders.Add(s);
            }
            r.CloseBlock(end, "pre-shaders");

            end = r.OpenBlock("static parameters");
            n = r.Count(12, end);
            for (uint i = 0; i < n; i++)
            {
                MatStaticParameter s = new MatStaticParameter();
                s.Name = r.U64();
                s.NestedMaterialIndex = r.I32();
                c.StaticParameters.Add(s);
            }
            r.CloseBlock(end, "static parameters");

            c.TextureParams = r.RawArray("texture parameters");

            end = r.OpenBlock("passes");
            n = r.Count(16, end);
            for (uint i = 0; i < n; i++)
            {
                MatPass p = new MatPass();
                p.PassType = r.I32();
                p.BlendMode = r.I32();
                p.MaterialCrc = r.U64();
                c.Passes.Add(p);
            }
            r.CloseBlock(end, "passes");

            c.Quality = r.I32();
            c.BlendModes = r.BitSet(1, "blend modes");
            c.PassMask = r.BitSet(1, "passes");
            c.Channels = r.BitSet(2, "channels");
            c.ShaderInputs = r.BitSet(3, "shader inputs");
            c.SceneTextures = r.BitSet(1, "scene textures");
            c.OptionalPropertyTypes = r.BitSet(1, "optional property types");

            end = r.OpenBlock("pre-shader buffer");
            uint len = r.U32();
            if (r.Pos + (long)len != end) throw new PropParseException("pre-shader buffer length does not match its block");
            c.PreShaderBuffer = r.Bytes((int)len);
            r.CloseBlock(end, "pre-shader buffer");

            c.Flags = r.U32();
            c.ParameterBufferScalarSize0 = r.U32();
            c.ParameterBufferScalarSize1 = r.U32();
            c.PreShaderParameterBufferScalarSize = r.U32();
            return c;
        }

        // ================================================================ writing
        /// <summary>Serializes the material. Every block size is recomputed.</summary>
        public byte[] Build()
        {
            Writer w = new Writer();
            w.U64(MaterialName);
            w.U64(RuntimePropertiesName);
            w.U64(LegacyRenderTextureProperty);
            w.U64(LegacyBlendModeRuntimeProperty);
            w.I32(Domain);

            int b = w.OpenBlock();
            w.U32((uint)RuntimeProperties.Count);
            for (int i = 0; i < RuntimeProperties.Count; i++)
            {
                w.U64(RuntimeProperties[i].Name);
                w.U64(RuntimeProperties[i].RuntimeName);
            }
            w.CloseBlock(b);

            w.U32(Flags);
            w.I32(Version);

            b = w.OpenBlock();
            w.U32((uint)Compiled.Count);
            for (int i = 0; i < Compiled.Count; i++) WriteCompiled(w, Compiled[i]);
            w.CloseBlock(b);
            return w.ToArray();
        }

        static void WriteCompiled(Writer w, MatCompiled c)
        {
            int b = w.OpenBlock();
            w.U32((uint)c.Parameters.Count);
            for (int i = 0; i < c.Parameters.Count; i++)
            {
                MatParameter p = c.Parameters[i];
                w.U64(p.Name); w.I32(p.PropertyType); w.I32(p.ValueType); w.U32(p.Flags);
                w.I32(p.ScalarOffset); w.I32(p.PreShaderScalarOffset); w.I32(p.NestedMaterialIndex);
            }
            w.CloseBlock(b);

            b = w.OpenBlock();
            w.U32((uint)c.Textures.Count);
            for (int i = 0; i < c.Textures.Count; i++)
            {
                MatTexture t = c.Textures[i];
                w.U64(t.Name); w.U64(t.TextureName); w.U64(t.TextureNameWithoutExtension);
                w.I32(t.Layout); w.I32(t.PropertyType);
                w.BitSet(t.TextureTypes);
                w.U32(t.FirstParamIndex); w.U32(t.ParamCount); w.I32(t.TextureIndex); w.I32(t.NestedMaterialIndex);
            }
            w.CloseBlock(b);

            b = w.OpenBlock();
            w.U32((uint)c.Transforms.Count);
            for (int i = 0; i < c.Transforms.Count; i++)
            {
                MatTransform x = c.Transforms[i];
                w.U64(x.ParameterPrefix); w.U32(x.Flags); w.I32(x.ScalarOffset0); w.I32(x.ScalarOffset1);
                w.I32(x.PreShaderScalarOffset0); w.I32(x.PreShaderScalarOffset1); w.I32(x.NestedMaterialIndex);
            }
            w.CloseBlock(b);

            w.RawArray(c.NestedMaterials);

            b = w.OpenBlock();
            w.U32((uint)c.PreShaders.Count);
            for (int i = 0; i < c.PreShaders.Count; i++)
            {
                MatPreShader s = c.PreShaders[i];
                w.I32(s.ValueType); w.U32(s.Flags); w.I32(s.PreShaderOffset); w.I32(s.ScalarParameterOffset);
            }
            w.CloseBlock(b);

            b = w.OpenBlock();
            w.U32((uint)c.StaticParameters.Count);
            for (int i = 0; i < c.StaticParameters.Count; i++)
            {
                w.U64(c.StaticParameters[i].Name);
                w.I32(c.StaticParameters[i].NestedMaterialIndex);
            }
            w.CloseBlock(b);

            w.RawArray(c.TextureParams);

            b = w.OpenBlock();
            w.U32((uint)c.Passes.Count);
            for (int i = 0; i < c.Passes.Count; i++)
            {
                w.I32(c.Passes[i].PassType); w.I32(c.Passes[i].BlendMode); w.U64(c.Passes[i].MaterialCrc);
            }
            w.CloseBlock(b);

            w.I32(c.Quality);
            w.BitSet(c.BlendModes);
            w.BitSet(c.PassMask);
            w.BitSet(c.Channels);
            w.BitSet(c.ShaderInputs);
            w.BitSet(c.SceneTextures);
            w.BitSet(c.OptionalPropertyTypes);

            b = w.OpenBlock();
            w.U32((uint)c.PreShaderBuffer.Length);
            w.Bytes(c.PreShaderBuffer);
            w.CloseBlock(b);

            w.U32(c.Flags);
            w.U32(c.ParameterBufferScalarSize0);
            w.U32(c.ParameterBufferScalarSize1);
            w.U32(c.PreShaderParameterBufferScalarSize);
        }

        // ================================================================ queries
        /// <summary>
        /// Every property key the material reads: runtime properties, parameters, texture slots,
        /// static parameters and transform prefixes. Values for these keys sit in ordinary
        /// properties of the same file; keys with no value there fall back to defaults.
        /// </summary>
        public HashSet<ulong> ReferencedKeys()
        {
            HashSet<ulong> keys = new HashSet<ulong>();
            for (int i = 0; i < RuntimeProperties.Count; i++) keys.Add(RuntimeProperties[i].Name);
            for (int v = 0; v < Compiled.Count; v++)
            {
                MatCompiled c = Compiled[v];
                for (int i = 0; i < c.Parameters.Count; i++) keys.Add(c.Parameters[i].Name);
                for (int i = 0; i < c.Textures.Count; i++) keys.Add(c.Textures[i].Name);
                for (int i = 0; i < c.StaticParameters.Count; i++) keys.Add(c.StaticParameters[i].Name);
                for (int i = 0; i < c.Transforms.Count; i++) keys.Add(c.Transforms[i].ParameterPrefix);
            }
            keys.Remove(0UL);
            return keys;
        }

        /// <summary>
        /// Keys that pick a shader variant when the material is compiled. Changing their values
        /// without recompiling may not match the precompiled shaders, so editors treat them as
        /// read-only.
        /// </summary>
        public HashSet<ulong> StaticKeys()
        {
            HashSet<ulong> keys = new HashSet<ulong>();
            for (int v = 0; v < Compiled.Count; v++)
                for (int i = 0; i < Compiled[v].StaticParameters.Count; i++)
                    keys.Add(Compiled[v].StaticParameters[i].Name);
            return keys;
        }

        /// <summary>Texture slots of the first (highest quality) variant, or an empty list.</summary>
        public List<MatTexture> TextureSlots()
        {
            return Compiled.Count > 0 ? Compiled[0].Textures : new List<MatTexture>();
        }

        // ================================================================ binary helpers
        sealed class Reader
        {
            readonly byte[] d;
            readonly int limit;
            public int Pos;

            public Reader(byte[] data, int start, int end)
            {
                d = data;
                Pos = start;
                limit = Math.Min(end, data.Length);
            }

            void Need(int n)
            {
                if (n < 0 || Pos + (long)n > limit)
                    throw new PropParseException("runs past the end of the value at +" + Pos);
            }

            public uint U32()
            {
                Need(4);
                uint v = (uint)(d[Pos] | (d[Pos + 1] << 8) | (d[Pos + 2] << 16) | (d[Pos + 3] << 24));
                Pos += 4;
                return v;
            }

            public int I32() { return (int)U32(); }

            public ulong U64()
            {
                ulong lo = U32();
                ulong hi = U32();
                return lo | (hi << 32);
            }

            public byte[] Bytes(int n)
            {
                Need(n);
                byte[] b = new byte[n];
                Buffer.BlockCopy(d, Pos, b, 0, n);
                Pos += n;
                return b;
            }

            /// <summary>Reads a block size word and returns the offset where the block ends.</summary>
            public int OpenBlock(string what)
            {
                int start = Pos;
                uint size = U32();
                if (size < 4 || start + (long)size > limit)
                    throw new PropParseException(what + ": block size " + size + " does not fit");
                return start + (int)size;
            }

            public void CloseBlock(int end, string what)
            {
                if (Pos != end)
                    throw new PropParseException(what + ": contents end at +" + Pos + " but the block ends at +" + end);
            }

            /// <summary>Reads an element count and checks that many elements can fit before the block end.</summary>
            public uint Count(int minElementSize, int end)
            {
                uint n = U32();
                if ((long)n * minElementSize > end - Pos) throw new PropParseException("element count " + n + " does not fit its block");
                return n;
            }

            public uint[] BitSet(int words, string what)
            {
                int end = OpenBlock(what);
                uint[] v = new uint[words];
                for (int i = 0; i < words; i++) v[i] = U32();
                CloseBlock(end, what);
                return v;
            }

            public MatRawArray RawArray(string what)
            {
                int end = OpenBlock(what);
                MatRawArray a = new MatRawArray();
                a.Count = U32();
                a.Body = Bytes(end - Pos);
                if (a.Count == 0 && a.Body.Length != 0) throw new PropParseException(what + ": empty array with data");
                return a;
            }
        }

        sealed class Writer
        {
            byte[] buf = new byte[1024];
            int len;

            void Grow(int n)
            {
                if (len + n <= buf.Length) return;
                int cap = buf.Length * 2;
                while (cap < len + n) cap *= 2;
                byte[] nb = new byte[cap];
                Buffer.BlockCopy(buf, 0, nb, 0, len);
                buf = nb;
            }

            public void U32(uint v)
            {
                Grow(4);
                buf[len] = (byte)v; buf[len + 1] = (byte)(v >> 8); buf[len + 2] = (byte)(v >> 16); buf[len + 3] = (byte)(v >> 24);
                len += 4;
            }

            public void I32(int v) { U32((uint)v); }
            public void U64(ulong v) { U32((uint)v); U32((uint)(v >> 32)); }

            public void Bytes(byte[] b)
            {
                if (b == null || b.Length == 0) return;
                Grow(b.Length);
                Buffer.BlockCopy(b, 0, buf, len, b.Length);
                len += b.Length;
            }

            public int OpenBlock() { int at = len; U32(0); return at; }

            public void CloseBlock(int at)
            {
                uint size = (uint)(len - at);
                buf[at] = (byte)size; buf[at + 1] = (byte)(size >> 8); buf[at + 2] = (byte)(size >> 16); buf[at + 3] = (byte)(size >> 24);
            }

            public void BitSet(uint[] words)
            {
                int b = OpenBlock();
                for (int i = 0; i < words.Length; i++) U32(words[i]);
                CloseBlock(b);
            }

            public void RawArray(MatRawArray a)
            {
                int b = OpenBlock();
                U32(a.Count);
                Bytes(a.Body);
                CloseBlock(b);
            }

            public byte[] ToArray()
            {
                byte[] r = new byte[len];
                Buffer.BlockCopy(buf, 0, r, 0, len);
                return r;
            }
        }
    }

    /// <summary>
    /// Display names for the material enums, taken from the game's own PDB (GameApp.pdb).
    /// Values the PDB lists as 255 are stored in files as -1.
    /// </summary>
    public static class MaterialEnums
    {
        static readonly string[] Pass =
        {
            "Main", "PreZ", "GBuffer", "GBuffer_Lines", "GBuffer_Outline", "Glow", "ShadowMap",
            "Outline", "LinearDepth", "LinesCS_Generate", "LinesCS_Rasterize",
            "LinesCS_ForceLinearDepth", "ParticleMain", "ParticleGlow", "ParticleLinearDepth",
            "DecalGBuffer", "DecalEmissive", "DecalGlow", "Post", "MayaShader", "LightBake"
        };

        static readonly string[] Blend =
        {
            "Normal", "Alpha", "AlphaAlphaTest", "AlphaTest", "InvAlphaTest", "Add", "Multiply",
            "InvMultiply", "AlphaAdd", "AlphaSubtract", "AlphaInvAlphaTest", "AddAlphaTest",
            "AddInvAlphaTest", "MultiplyAlphaTest", "MultiplyInvAlphaTest"
        };

        static readonly string[] Layout = { "2D", "Cube", "3D", "2DArray", "CubeArray" };

        static readonly string[] Value = { "Float", "Float2", "Float3", "Float4", "Channels" };

        static readonly string[] Quality = { "High", "Low", "Lowest" };

        static readonly string[] Domain = { "Mesh", "Particle", "Decal", "Post", "ExportMeshShader" };

        static readonly string[] TextureType =
        {
            "PrimaryColorMap", "ColorMap", "DetailColorMap", "SharpDetailMap", "NormalMap",
            "MiscMap", "Vector2Map", "NormalDxDyMap", "LinePatternMap", "LineVisibilityMap",
            "LineGenerationStyleMap", "PackedDetailMap", "SingleChannelDetailMap", "NormalXYMap",
            "CubeMap", "VolumeMap", "ObjectNormalMap"
        };

        static readonly string[] Property =
        {
            "GlossExponent", "GlowIntensity", "SpecularPower", "OutlineColor", "OutlineInvertColor",
            "OutlineZRange", "ToonGradientCutoff", "LineGenerateCreases", "LineGenerateBoundaries",
            "LineCreaseAngle", "LineSmoothJaggedCreaseAngle", "LineGenerateSmooth",
            "LineGenerateJagged", "LineMinWidth", "LineMaxWidth", "LineWidthFromLighting",
            "LineLightingType", "LineLightingId", "LinePatternRepeat", "LitLineBias", "LitLineScale",
            "ConformNormal", "NPRLineFalloff", "NPRLineAlphaFalloff", "DrawHiddenLines",
            "AlphaMeshCullsLines", "UseArtistNormal", "HorizonFade", "HairTerms",
            "ClothOffsetScale", "LightWrap"
        };

        static string Pick(string[] names, int v, string none)
        {
            if (v == -1 || v == 255) return none;
            return v >= 0 && v < names.Length ? names[v] : "#" + v;
        }

        public static string PassName(int v) { return Pick(Pass, v, "None"); }
        public static string BlendName(int v) { return Pick(Blend, v, "Default"); }
        public static string LayoutName(int v) { return Pick(Layout, v, "Unknown"); }
        public static string ValueTypeName(int v) { return Pick(Value, v, "None"); }
        public static string QualityName(int v) { return Pick(Quality, v, "None"); }
        public static string DomainName(int v) { return Pick(Domain, v, "None"); }
        public static string PropertyTypeName(int v) { return Pick(Property, v, "None"); }

        /// <summary>Names of the texture types whose bits are set.</summary>
        public static string TextureTypeNames(uint[] bits)
        {
            List<string> names = new List<string>();
            if (bits != null)
                for (int w = 0; w < bits.Length; w++)
                    for (int b = 0; b < 32; b++)
                        if ((bits[w] & (1u << b)) != 0)
                        {
                            int i = w * 32 + b;
                            names.Add(i < TextureType.Length ? TextureType[i] : "#" + i);
                        }
            return names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
        }
    }
}
