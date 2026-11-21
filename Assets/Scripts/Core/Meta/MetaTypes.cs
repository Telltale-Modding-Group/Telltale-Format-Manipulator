using System;
using System.Collections.Generic;

namespace TFM.Core.Meta
{
    /// <summary>Type-name helpers: intrinsic widths, template splitting, container recognition.</summary>
    public static class MetaTypes
    {
        static readonly Dictionary<string, int> IntWidths = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "int8", 1 }, { "char", 1 }, { "signedchar", 1 }, { "uint8", 1 }, { "unsignedchar", 1 },
            { "int16", 2 }, { "short", 2 }, { "uint16", 2 }, { "unsignedshort", 2 }, { "wchar_t", 2 },
            { "int32", 4 }, { "int", 4 }, { "long", 4 }, { "uint32", 4 }, { "unsignedint", 4 }, { "unsignedlong", 4 },
            { "int64", 8 }, { "__int64", 8 }, { "uint64", 8 }, { "unsigned__int64", 8 }, { "longlong", 8 },
            { "unsignedlonglong", 8 },
        };

        static readonly HashSet<string> Unsigned = new HashSet<string>(StringComparer.Ordinal)
        {
            "uint8", "unsignedchar", "uint16", "unsignedshort", "wchar_t", "uint32", "unsignedint", "unsignedlong",
            "uint64", "unsigned__int64", "unsignedlonglong",
        };

        /// <summary>Members of these types are never written (and never hashed into version CRCs).</summary>
        static readonly HashSet<string> NeverSerialized = new HashSet<string>(StringComparer.Ordinal)
        {
            "EnumBase", "Ptr<PtrBase>", "KeyframedValueInterface", "ContainerInterface", "InverseKinematicsBase",
        };

        static readonly HashSet<string> UnblockedIntrinsics = new HashSet<string>(StringComparer.Ordinal)
        {
            "bool", "float", "double", "Symbol", "Flags", "Vector2", "Vector3", "Vector4", "Quaternion", "Color",
            "ToolProps", "EnumBase",
        };

        public static bool IsInt(string t) { return IntWidths.ContainsKey(t); }

        public static int IntWidth(string t)
        {
            int w;
            return IntWidths.TryGetValue(t, out w) ? w : 4;
        }

        public static bool IsUnsigned(string t) { return Unsigned.Contains(t); }

        public static bool IsNeverSerialized(string t) { return NeverSerialized.Contains(t); }

        public static bool IsUnblockedIntrinsic(string t) { return IntWidths.ContainsKey(t) || UnblockedIntrinsics.Contains(t); }

        /// <summary>Splits "Map&lt;A,B&lt;C&gt;&gt;" into "Map" and ["A", "B&lt;C&gt;"]. Non-templates return no arguments.</summary>
        public static string SplitTemplate(string t, out List<string> args)
        {
            args = new List<string>();
            int lt = t.IndexOf('<');
            if (lt < 0 || !t.EndsWith(">", StringComparison.Ordinal)) return t;
            string inner = t.Substring(lt + 1, t.Length - lt - 2);
            int depth = 0, start = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(inner.Substring(start, i - start));
                    start = i + 1;
                }
            }
            args.Add(inner.Substring(start));
            return t.Substring(0, lt);
        }

        public static bool IsHandle(string t)
        {
            return t.StartsWith("Handle<", StringComparison.Ordinal) || t.StartsWith("HandleLock<", StringComparison.Ordinal);
        }

        /// <summary>The T of Handle&lt;T&gt;, or null.</summary>
        public static string HandleTarget(string t)
        {
            if (!IsHandle(t)) return null;
            List<string> a;
            SplitTemplate(t, out a);
            return a.Count > 0 ? a[0] : null;
        }

        public static bool IsSequence(string baseName)
        {
            return baseName == "DCArray" || baseName == "List" || baseName == "Set" || baseName == "Deque" || baseName == "SArray";
        }
    }
}
