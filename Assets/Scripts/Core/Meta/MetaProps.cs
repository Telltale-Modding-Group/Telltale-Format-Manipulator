using System;
using System.Collections.Generic;

namespace TFM.Core.Meta
{
    /// <summary>Helpers for PropertySet values decoded by MetaCodec: groups of (key symbol -> value) per value type.</summary>
    public static class MetaProps
    {
        public static List<MetaValue> Groups(MetaValue ps)
        {
            MetaValue g = ps != null ? ps["#groups"] : null;
            return g != null && g.Items != null ? g.Items : new List<MetaValue>();
        }

        public static bool IsRaw(MetaValue ps) { return ps != null && ps["#raw"] != null; }

        /// <summary>The value stored under <paramref name="key"/>, or null. <paramref name="type"/> receives its value type.</summary>
        public static MetaValue Get(MetaValue ps, ulong key, out string type)
        {
            type = null;
            List<MetaValue> groups = Groups(ps);
            for (int g = 0; g < groups.Count; g++)
            {
                MetaValue grp = groups[g];
                for (int i = 0; i < grp.Keys.Count; i++)
                {
                    if (grp.Keys[i].Bits != key) continue;
                    type = grp.Type;
                    return grp.Items[i];
                }
            }
            return null;
        }

        public static MetaValue Get(MetaValue ps, string keyName)
        {
            string t;
            return Get(ps, HashDatabase.Crc64(keyName), out t);
        }

        /// <summary>Adds or replaces a value. The group for its type is created when missing.</summary>
        public static void Set(MetaValue ps, ulong key, string type, MetaValue value)
        {
            Remove(ps, key);
            MetaValue groups = ps["#groups"];
            if (groups == null) throw new InvalidOperationException("This property set could not be decoded, so it cannot be edited.");
            MetaValue grp = null;
            for (int g = 0; g < groups.Items.Count; g++) if (groups.Items[g].Type == type) { grp = groups.Items[g]; break; }
            if (grp == null)
            {
                grp = new MetaValue(type, MetaKind.Map);
                groups.Items.Add(grp);
            }
            grp.Keys.Add(MetaValue.NewHash("Symbol", key));
            grp.Items.Add(value);
        }

        public static bool Remove(MetaValue ps, ulong key)
        {
            List<MetaValue> groups = Groups(ps);
            for (int g = 0; g < groups.Count; g++)
            {
                MetaValue grp = groups[g];
                for (int i = 0; i < grp.Keys.Count; i++)
                {
                    if (grp.Keys[i].Bits != key) continue;
                    grp.Keys.RemoveAt(i);
                    grp.Items.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public static int Count(MetaValue ps)
        {
            int n = 0;
            List<MetaValue> groups = Groups(ps);
            for (int g = 0; g < groups.Count; g++) n += groups[g].Items.Count;
            return n;
        }
    }
}
