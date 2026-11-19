using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TFM.Core
{
    /// <summary>Which family an archive belongs to.</summary>
    public enum TtArchiveFormat
    {
        /// <summary>Not recognised as an archive.</summary>
        Unknown,
        /// <summary>The original .ttarch, versions 1-9 plus the pre-versioned legacy layout.</summary>
        Ttarch,
        /// <summary>.ttarch2: a TTC* container wrapped around a TTA2/TTA3/TTA4 entry table.</summary>
        Ttarch2
    }

    /// <summary>One file listed in an archive. Only what the name list needs is kept.</summary>
    public sealed class TtArchiveEntry
    {
        public readonly string Name;
        public readonly ulong Hash;
        public readonly long Offset;
        public readonly long Size;

        public TtArchiveEntry(string name, ulong hash, long offset, long size)
        {
            Name = name;
            Hash = hash;
            Offset = offset;
            Size = size;
        }
    }

    /// <summary>Result of reading an archive's entry table, including which key opened it.</summary>
    public sealed class TtArchiveResult
    {
        public string FileName = "";
        public long FileLength;
        public TtArchiveFormat Format = TtArchiveFormat.Unknown;
        public string VersionText = "";
        public string ContainerText = "";
        public string CompressionText = "";
        /// <summary>True when the entry table could only be read with a key.</summary>
        public bool Encrypted;
        /// <summary>The key that opened the archive; null when it needed none.</summary>
        public TtArchiveKey Key;
        /// <summary>Files the archive says it contains.</summary>
        public int DeclaredCount;
        /// <summary>False when some or all names could not be recovered.</summary>
        public bool NamesComplete = true;
        /// <summary>Plain-language remark about anything unusual.</summary>
        public string Note = "";
        /// <summary>Set only when the archive could not be read at all.</summary>
        public string Error;

        public readonly List<TtArchiveEntry> Entries = new List<TtArchiveEntry>();
        /// <summary>What was tried, so a failure can be explained instead of just reported.</summary>
        public readonly List<string> Log = new List<string>();

        public bool Ok { get { return Error == null; } }

        /// <summary>Entries whose name was recovered.</summary>
        public int NamedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Entries.Count; i++) if (Entries[i].Name.Length > 0) n++;
                return n;
            }
        }

        /// <summary>Distinct name hashes, i.e. how many names a dump will contain.</summary>
        public int UniqueCount
        {
            get
            {
                Dictionary<ulong, bool> seen = new Dictionary<ulong, bool>();
                for (int i = 0; i < Entries.Count; i++)
                    if (Entries[i].Name.Length > 0) seen[Entries[i].Hash] = true;
                return seen.Count;
            }
        }
    }

    /// <summary>
    /// Reads the file-entry table of a .ttarch or .ttarch2 archive: names, hashes, offsets and
    /// sizes. File data is never extracted, so this stays fast on multi-gigabyte archives.
    /// </summary>
    /// <remarks>
    /// An archive stores no game identifier. What identifies the title is which Blowfish key
    /// opens it, so in automatic mode every known key is tried and an attempt only counts as a
    /// success when the resulting entry table validates: names are printable text, offsets and
    /// sizes stay inside the file, and — for .ttarch2 — each name hashes back to the CRC-64 the
    /// archive stored for it. A wrong key fails that last check immediately.
    /// </remarks>
    public static class TtArchiveReader
    {
        // Container magics, read as a little-endian uint32. The four bytes on disk are the
        // four-character code in reverse order: a TTCE archive begins with the bytes 'E','C',
        // 'T','T', which read back little-endian give 0x54544345. Verified against a real
        // TWD: Definitive .ttarch2.
        const uint MagicTtcN = 0x5454434E; // "TTCN"
        const uint MagicTtcE = 0x54544345; // "TTCE"
        const uint MagicTtce = 0x54544365; // "TTCe"
        const uint MagicTtcZ = 0x5454435A; // "TTCZ"
        const uint MagicTtcz = 0x5454437A; // "TTCz"

        // Payload magics, same byte order rule: a TTA4 payload begins with '4','A','T','T'.
        const uint MagicTta2 = 0x54544132; // "TTA2"
        const uint MagicTta3 = 0x54544133; // "TTA3"
        const uint MagicTta4 = 0x54544134; // "TTA4"

        const int NamePageSize = 0x10000;
        const int MaxName = 256;
        const int MaxHeaderBlock = 64 * 1024 * 1024;
        const int MaxFiles = 4000000;
        const long MaxLegacyHeader = 32L * 1024 * 1024;

        /// <summary>
        /// Opens an archive and reads its entry table.
        /// </summary>
        /// <param name="path">Archive to read.</param>
        /// <param name="manualKey">Key to use in manual mode; ignored when autoDetect is true.</param>
        /// <param name="autoDetect">
        /// True: try no key, then every known key, and report the one that validates.
        /// False: try only <paramref name="manualKey"/>.
        /// </param>
        public static TtArchiveResult Load(string path, TtArchiveKey manualKey, bool autoDetect)
        {
            TtArchiveResult result = new TtArchiveResult();
            try
            {
                result.FileName = Path.GetFileName(path);
                FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    result.FileLength = fs.Length;
                    if (fs.Length < 16)
                    {
                        result.Error = "The file is only " + fs.Length + " bytes long, too small for an archive.";
                        return result;
                    }

                    uint magic = PeekU32(fs);
                    result.Format = Classify(magic, fs.Length);
                    if (result.Format == TtArchiveFormat.Unknown)
                    {
                        result.Error = "This is not a Telltale archive. The file starts with 0x"
                            + magic.ToString("X8") + " (" + Printable(magic) + "), which is neither"
                            + " a .ttarch version number nor a .ttarch2 container signature.";
                        return result;
                    }

                    List<TtArchiveKey> candidates = new List<TtArchiveKey>();
                    if (autoDetect)
                    {
                        candidates.Add(null); // unencrypted archives need no key; try that first
                        IList<TtArchiveKey> all = TtArchiveKeys.All;
                        for (int i = 0; i < all.Count; i++) candidates.Add(all[i]);
                        result.Log.Add("Automatic mode: tried no key, then all " + all.Count + " known keys.");
                    }
                    else
                    {
                        if (manualKey == null)
                        {
                            result.Error = "Manual key mode is on but no key is selected. Pick one, or switch back to Automatic.";
                            return result;
                        }
                        candidates.Add(manualKey);
                        result.Log.Add("Manual mode: tried only " + manualKey.Game + ".");
                    }

                    TtArchiveResult best = null;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        TtArchiveKey key = candidates[i];
                        TtArchiveResult attempt = Attempt(fs, result.Format, key);
                        string who = key == null ? "no key" : key.Id;
                        if (attempt.Ok)
                        {
                            result.Log.Add("  " + who + ": entry table verified, " + attempt.DeclaredCount + " files.");
                            if (best == null) best = attempt;
                            break; // one validated key is conclusive
                        }
                        result.Log.Add("  " + who + ": " + attempt.Error);
                    }

                    if (best != null)
                    {
                        best.FileName = result.FileName;
                        best.FileLength = result.FileLength;
                        best.Format = result.Format;
                        for (int i = 0; i < result.Log.Count; i++) best.Log.Insert(i, result.Log[i]);
                        return best;
                    }

                    result.Error = autoDetect
                        ? "None of the known keys opened this archive. It may use a key that is not in the table."
                        : "The selected key did not open this archive. Switch to Automatic, or pick a different key.";
                    return result;
                }
                finally
                {
                    fs.Close();
                }
            }
            catch (Exception e)
            {
                result.Error = "Could not read the archive: " + e.Message;
                return result;
            }
        }

        /// <summary>
        /// Decides the family from the first four bytes. .ttarch begins with a small version
        /// number; the pre-versioned layout begins either with a directory count (plain) or a
        /// large header size (encrypted). Anything that is not a known container signature and
        /// does not fit those shapes is rejected rather than guessed at.
        /// </summary>
        static TtArchiveFormat Classify(uint magic, long length)
        {
            if (magic == MagicTtcN || magic == MagicTtcE || magic == MagicTtce
                || magic == MagicTtcZ || magic == MagicTtcz
                || magic == MagicTta2 || magic == MagicTta3 || magic == MagicTta4)
                return TtArchiveFormat.Ttarch2;

            // Versioned .ttarch.
            if (magic >= 1 && magic <= 9) return TtArchiveFormat.Ttarch;

            // Plain legacy: the file opens with the directory count, normally zero.
            if (magic == 0) return TtArchiveFormat.Ttarch;

            // Encrypted legacy: the file opens with the encrypted header's size.
            if (magic > 128 && magic < MaxLegacyHeader && magic <= length) return TtArchiveFormat.Ttarch;

            return TtArchiveFormat.Unknown;
        }

        static string Printable(uint magic)
        {
            char[] c = new char[4];
            for (int i = 0; i < 4; i++)
            {
                char ch = (char)((magic >> (i * 8)) & 0xFF);
                c[i] = ch >= 0x20 && ch <= 0x7E ? ch : '.';
            }
            return new string(c);
        }

        static TtArchiveResult Attempt(Stream fs, TtArchiveFormat format, TtArchiveKey key)
        {
            TtArchiveResult r = new TtArchiveResult();
            try
            {
                fs.Seek(0, SeekOrigin.Begin);
                uint magic = ReadU32(fs);
                if (format == TtArchiveFormat.Ttarch2) ReadTtarch2(fs, magic, key, r);
                else ReadTtarch(fs, magic, key, r);
            }
            catch (EndOfStreamException)
            {
                r.Error = "read past the end of the file.";
            }
            catch (InvalidDataException e)
            {
                r.Error = e.Message;
            }
            catch (Exception e)
            {
                r.Error = e.Message;
            }
            return r;
        }

        // ================================================================ .ttarch

        static void ReadTtarch(Stream fs, uint first, TtArchiveKey key, TtArchiveResult r)
        {
            if (first < 1 || first > 9)
            {
                ReadTtarchLegacy(fs, key, r);
                return;
            }

            int version = (int)first;
            r.Format = TtArchiveFormat.Ttarch;
            r.VersionText = "ttarch version " + version;
            r.ContainerText = "none";

            uint decryptionMode = ReadU32(fs);
            bool encrypted = decryptionMode == 1;

            if (version >= 2) ReadU32(fs); // unknown, always zero

            uint filesMode = 1;
            if (version >= 3)
            {
                filesMode = ReadU32(fs);
                uint chunkCount = ReadU32(fs);
                if (chunkCount > 0x100000) throw new InvalidDataException("chunk count " + chunkCount + " is implausible.");
                for (uint i = 0; i < chunkCount; i++) ReadU32(fs);
                ReadU32(fs); // size of the file-data region
            }
            if (version >= 4) { ReadU32(fs); ReadU32(fs); }
            if (version >= 5) { ReadU32(fs); ReadU32(fs); }
            if (version >= 7) ReadU32(fs); // chunk size, in units of 1024
            if (version >= 8)
            {
                if (ReadByte(fs) > 0) ReadU32(fs);
            }
            if (version >= 9) ReadU32(fs); // header CRC-32

            int tableSize = (int)ReadU32(fs);
            if (tableSize < 0 || tableSize > MaxHeaderBlock)
                throw new InvalidDataException("entry-table size " + tableSize + " is implausible.");

            bool packedTable = version >= 6 && filesMode == 2;
            byte[] table;

            if (packedTable)
            {
                int packedSize = (int)ReadU32(fs);
                if (packedSize < 0 || packedSize > MaxHeaderBlock)
                    throw new InvalidDataException("packed entry-table size " + packedSize + " is implausible.");
                table = UnpackEntryTable(ReadBlock(fs, packedSize), key, encrypted, tableSize, version);
                r.CompressionText = "entry table packed";
            }
            else
            {
                table = ReadBlock(fs, tableSize);
                if (encrypted)
                {
                    if (key == null) throw new InvalidDataException("the entry table is encrypted and no key was given.");
                    new TtBlowfish(key.Bytes, version >= 7).Decipher(table, 0, table.Length);
                }
                r.CompressionText = filesMode == 2 ? "file data packed" : "none";
            }

            List<TtArchiveEntry> entries;
            string why;
            if (!ParseTtarchEntries(table, r.FileLength, out entries, out why))
                throw new InvalidDataException(why);

            r.Encrypted = encrypted;
            if (encrypted) r.Key = key;
            Store(r, entries);
        }

        /// <summary>
        /// The packed entry table can be deflated and encrypted in either order depending on the
        /// writer, so both orders are tried and the one that parses wins.
        /// </summary>
        static byte[] UnpackEntryTable(byte[] packed, TtArchiveKey key, bool encrypted, int expectedSize, int version)
        {
            if (!encrypted)
            {
                byte[] plain = Inflate(packed, expectedSize);
                if (plain != null) return plain;
                throw new InvalidDataException("the entry table could not be decompressed.");
            }

            if (key == null) throw new InvalidDataException("the entry table is encrypted and no key was given.");
            TtBlowfish cipher = new TtBlowfish(key.Bytes, version >= 7);

            // Deflate, then decrypt.
            byte[] inflated = Inflate(packed, expectedSize);
            if (inflated != null)
            {
                byte[] copy = (byte[])inflated.Clone();
                cipher.Decipher(copy, 0, copy.Length);
                List<TtArchiveEntry> probe;
                string why;
                if (ParseTtarchEntries(copy, 0, out probe, out why)) return copy;
            }

            // Decrypt, then inflate.
            byte[] decrypted = (byte[])packed.Clone();
            cipher.Decipher(decrypted, 0, decrypted.Length);
            byte[] second = Inflate(decrypted, expectedSize);
            if (second != null) return second;

            throw new InvalidDataException("the entry table could not be decrypted with this key.");
        }

        static void ReadTtarchLegacy(Stream fs, TtArchiveKey key, TtArchiveResult r)
        {
            fs.Seek(0, SeekOrigin.Begin);
            uint headerSize = ReadU32(fs);
            r.Format = TtArchiveFormat.Ttarch;
            r.VersionText = "ttarch legacy (unversioned)";
            r.ContainerText = "none";

            List<TtArchiveEntry> entries;
            string why;

            if (headerSize > 128)
            {
                if (headerSize > MaxHeaderBlock)
                    throw new InvalidDataException("header size " + headerSize + " is implausible.");
                if (key == null) throw new InvalidDataException("the entry table is encrypted and no key was given.");

                byte[] header = ReadBlock(fs, (int)headerSize);
                // The legacy layout predates the modified cipher, so always use the classic one.
                new TtBlowfish(key.Bytes, false).Decipher(header, 0, header.Length);
                if (!ParseTtarchEntries(header, r.FileLength, out entries, out why))
                    throw new InvalidDataException(why);

                r.Encrypted = true;
                r.Key = key;
                r.CompressionText = "none";
                Store(r, entries);
                return;
            }

            // Plain legacy: the entry table is at the very start, claimed by the header-size field.
            fs.Seek(0, SeekOrigin.Begin);
            int span = (int)Math.Min(fs.Length, MaxLegacyHeader);
            byte[] whole = ReadBlock(fs, span);
            if (!ParseTtarchEntries(whole, r.FileLength, out entries, out why))
                throw new InvalidDataException(why);

            r.CompressionText = "none";
            Store(r, entries);
        }

        static bool ParseTtarchEntries(byte[] h, long fileLength, out List<TtArchiveEntry> entries, out string why)
        {
            entries = new List<TtArchiveEntry>();
            why = null;
            int p = 0;

            if (h.Length < 8) { why = "entry table is truncated."; return false; }

            int dirCount = I32(h, p);
            p += 4;
            if (dirCount < 0 || dirCount > 4096) { why = "directory count " + dirCount + " is implausible."; return false; }

            for (int i = 0; i < dirCount; i++)
            {
                if (p + 4 > h.Length) { why = "directory table is truncated."; return false; }
                int len = I32(h, p);
                p += 4;
                if (len < 1 || len > 1023 || p + len > h.Length) { why = "directory name length is implausible."; return false; }
                p += len;
            }

            if (p + 4 > h.Length) { why = "entry count is missing."; return false; }
            int fileCount = I32(h, p);
            p += 4;
            if (fileCount < 0 || fileCount > MaxFiles) { why = "file count " + fileCount + " is implausible."; return false; }

            long previousOffset = -1;
            for (int i = 0; i < fileCount; i++)
            {
                if (p + 4 > h.Length) { why = "file table is truncated."; return false; }
                int nameLen = I32(h, p);
                p += 4;
                if (nameLen < 1 || nameLen > 255 || p + nameLen > h.Length)
                {
                    why = "file name length is implausible.";
                    return false;
                }

                string name = Ascii(h, p, nameLen);
                p += nameLen;
                if (!IsPlausibleName(name)) { why = "a file name is not printable text."; return false; }

                if (p + 12 > h.Length) { why = "a file entry is truncated."; return false; }
                p += 4; // folder index, always zero in practice
                long offset = U32(h, p);
                p += 4;
                long size = U32(h, p);
                p += 4;

                if (offset < previousOffset) { why = "file offsets are not in ascending order."; return false; }
                if (fileLength > 0 && offset + size > fileLength)
                {
                    why = "a file runs past the end of the archive.";
                    return false;
                }
                previousOffset = offset;

                entries.Add(new TtArchiveEntry(name, HashDatabase.Crc64(name), offset, size));
            }

            return true;
        }

        // =============================================================== .ttarch2

        static void ReadTtarch2(Stream fs, uint magic, TtArchiveKey key, TtArchiveResult r)
        {
            r.Format = TtArchiveFormat.Ttarch2;

            bool encrypted = magic == MagicTtcE || magic == MagicTtce;
            bool compressed = magic != MagicTtcN && magic != MagicTta2 && magic != MagicTta3 && magic != MagicTta4;
            int compressionType = 0; // 0 = deflate, 1 = Oodle

            if (!compressed)
            {
                r.ContainerText = magic == MagicTtcN ? "TTCN, plain" : "none, bare TTA payload";
            }
            else
            {
                if (magic == MagicTtcN) r.ContainerText = "TTCN, plain";
                else if (magic == MagicTtcE) r.ContainerText = "TTCE, encrypted";
                else if (magic == MagicTtce) r.ContainerText = "TTCe, encrypted";
                else if (magic == MagicTtcZ) r.ContainerText = "TTCZ, compressed";
                else r.ContainerText = "TTCz, compressed";

                if (magic == MagicTtce || magic == MagicTtcz)
                {
                    compressionType = (int)ReadU32(fs);
                    if (compressionType > 1)
                        throw new InvalidDataException("unknown compression type " + compressionType + ".");
                }
            }

            if (compressionType == 1)
                throw new InvalidDataException("this container uses Oodle compression, which is not supported, so its entry table cannot be read.");

            Payload payload = new Payload();
            try
            {
                payload.Source = fs;
                payload.Modified = true; // containers always use the version-7 cipher

                if (compressed)
                {
                    uint window = ReadU32(fs);
                    uint pages = ReadU32(fs);
                    if (window < 512 || window > 4 * 1024 * 1024)
                        throw new InvalidDataException("page size " + window + " is implausible.");
                    if (pages == 0 || pages > 0x400000)
                        throw new InvalidDataException("page count " + pages + " is implausible.");

                    payload.WindowSize = window;
                    payload.PageCount = (int)pages;
                    payload.PageSizes = new uint[pages];
                    payload.PageStarts = new ulong[pages];

                    // The table holds NumPages+1 cumulative offsets. Real TWD: Definitive
                    // archives store absolute file positions (the first entry equals the end of
                    // the header, e.g. 36), while some writers use offsets relative to the start
                    // of the page data (first entry 0). Both are accepted: a leading 0 means
                    // relative, anything else is taken as absolute.
                    ulong[] offsets = new ulong[pages + 1];
                    for (uint i = 0; i <= pages; i++) offsets[i] = ReadU64(fs);

                    long dataStart = fs.Position;
                    bool relative = offsets[0] == 0;
                    ulong bias = relative ? (ulong)dataStart : 0UL;

                    for (uint i = 0; i < pages; i++)
                    {
                        if (offsets[i + 1] < offsets[i])
                            throw new InvalidDataException("page offsets are not ascending.");

                        ulong size = offsets[i + 1] - offsets[i];
                        if (size > window + 4096)
                            throw new InvalidDataException("a packed page is larger than the page size.");

                        payload.PageStarts[i] = offsets[i] + bias;
                        payload.PageSizes[i] = (uint)size;
                    }

                    ulong lastEnd = offsets[pages] + bias;
                    if (lastEnd > (ulong)fs.Length)
                        throw new InvalidDataException("the page table runs past the end of the file.");

                    payload.Length = (long)pages * window;
                    payload.Compressed = true;
                }
                else if (magic == MagicTtcN)
                {
                    // TTCN wraps the payload in a plain container that states its length.
                    ulong length = ReadU64(fs);
                    long limit = fs.Length * 256L + 0x100000;
                    if (length < 16 || (length > (ulong)limit && length > (ulong)fs.Length))
                        throw new InvalidDataException("payload length " + length + " is implausible.");
                    payload.Length = (long)length;
                    payload.Compressed = false;
                }
                else
                {
                    // A bare TTA payload with no container around it runs to the end of the file.
                    payload.Length = fs.Length;
                    payload.Compressed = false;
                }

                payload.Start = fs.Position;
                payload.Key = key == null ? null : key.Bytes;

                r.Encrypted = encrypted;
                r.CompressionText = payload.Compressed ? "deflate" : "none";

                List<TtArchiveEntry> entries;
                string why;
                if (!ParseTtarch2(payload, r, out entries, out why))
                    throw new InvalidDataException(why);

                if (encrypted) r.Key = key;
                Store(r, entries);
            }
            finally
            {
                payload.Dispose();
            }
        }

        static bool ParseTtarch2(Payload payload, TtArchiveResult r, out List<TtArchiveEntry> entries, out string why)
        {
            entries = new List<TtArchiveEntry>();
            why = null;

            if (payload.Length < 16) { why = "the payload is too small."; return false; }

            byte[] head = payload.Read(0, 16);
            if (head == null) { why = "the payload header could not be read."; return false; }

            uint version = U32(head, 0);
            int entrySize;
            if (version == MagicTta4)
            {
                r.VersionText = "ttarch2, TTA4";
                entrySize = 28; // hash, offset, size, preload, two name-page fields
            }
            else if (version == MagicTta3)
            {
                r.VersionText = "ttarch2, TTA3";
                entrySize = 28;
            }
            else if (version == MagicTta2)
            {
                r.VersionText = "ttarch2, TTA2";
                entrySize = 28; // hash, offset, extra locator, size, preload
            }
            else
            {
                why = "the payload does not start with TTA2/TTA3/TTA4 (found 0x" + version.ToString("X8") + ").";
                return false;
            }

            long p = 4;
            if (version == MagicTta3 || version == MagicTta2) p += 4; // reserved

            byte[] counters = payload.Read(p, 8);
            if (counters == null) { why = "the payload counters could not be read."; return false; }
            // Size of the name stream in BYTES (a whole number of 64 KiB pages), not a page
            // count: real TWD: Definitive archives store 65536 for one page, 131072 for two.
            uint nameBytes = U32(counters, 0);
            int fileCount = I32(counters, 4);
            p += 8;

            if (fileCount < 0 || fileCount > MaxFiles) { why = "file count " + fileCount + " is implausible."; return false; }
            if (nameBytes > payload.Length) { why = "name stream size " + nameBytes + " bytes is larger than the archive contents."; return false; }

            long tableSize = (long)fileCount * entrySize;
            if (p + tableSize > payload.Length) { why = "the file table is larger than the payload."; return false; }

            byte[] table = fileCount > 0 ? payload.Read(p, (int)tableSize) : null;
            if (fileCount > 0 && table == null) { why = "the file table could not be read."; return false; }

            ulong[] hashes = new ulong[fileCount];
            long[] offsets = new long[fileCount];
            long[] sizes = new long[fileCount];
            int[] namePos = new int[fileCount];

            int q = 0;
            for (int i = 0; i < fileCount; i++)
            {
                hashes[i] = U64(table, q);
                q += 8;
                offsets[i] = (long)U64(table, q);
                q += 8;
                if (version == MagicTta2) q += 4; // alternate name locator, unused here
                sizes[i] = U32(table, q);
                q += 4;
                q += 4; // preload size
                if (version != MagicTta2)
                {
                    int pageIndex = U16(table, q);
                    q += 2;
                    int pageOffset = U16(table, q);
                    q += 2;
                    namePos[i] = pageIndex * NamePageSize + pageOffset;
                }
                else
                {
                    namePos[i] = -1;
                }
            }

            long namesStart = p + tableSize;
            long namesEnd = payload.Length;
            if (nameBytes > 0)
            {
                long byStream = namesStart + nameBytes;
                if (byStream < namesEnd) namesEnd = byStream;
            }

            r.DeclaredCount = fileCount;

            if (version == MagicTta2)
            {
                // TTA2 has no name-page offsets, so names are not reachable this way.
                r.NamesComplete = false;
                r.Note = "TTA2 entries carry no name offset, so names cannot be read from the entry table."
                    + " Hashes and sizes are still listed.";
                for (int i = 0; i < fileCount; i++)
                    entries.Add(new TtArchiveEntry("", hashes[i], offsets[i], sizes[i]));
                return true;
            }

            // Names live in a packed page stream, and only one page is held at a time, so read
            // them in page order instead of entry order. Entry order is unrelated to name order
            // in general, and reading it directly would re-decode the same page repeatedly.
            int[] order = new int[fileCount];
            for (int i = 0; i < fileCount; i++) order[i] = i;
            Array.Sort(order, (a, b) => namePos[a].CompareTo(namePos[b]));

            string[] resolved = new string[fileCount];
            int named = 0;
            int matched = 0;
            for (int k = 0; k < fileCount; k++)
            {
                int i = order[k];
                string name = "";
                long at = namesStart + namePos[i];
                if (namePos[i] >= 0 && at < namesEnd)
                {
                    name = payload.ReadCString(at, namesEnd, MaxName);
                    if (!IsPlausibleName(name)) name = "";
                }

                resolved[i] = name;
                if (name.Length > 0)
                {
                    named++;
                    if (HashDatabase.Crc64(name) == hashes[i]) matched++;
                }
            }

            for (int i = 0; i < fileCount; i++)
                entries.Add(new TtArchiveEntry(resolved[i], hashes[i], offsets[i], sizes[i]));

            // Every name must hash back to the CRC the archive stored for it. This is what
            // distinguishes the right key from a wrong one.
            if (fileCount > 0 && matched < fileCount)
            {
                why = "names do not match their stored CRC-64 (" + matched + " of " + fileCount + ").";
                return false;
            }

            if (fileCount > 0 && named < fileCount)
            {
                r.NamesComplete = false;
                r.Note = (fileCount - named) + " of " + fileCount + " names could not be read.";
            }

            return true;
        }

        // ---------------------------------------------------------------- shared

        static void Store(TtArchiveResult r, List<TtArchiveEntry> entries)
        {
            r.Entries.Clear();
            r.Entries.AddRange(entries);
            if (r.DeclaredCount <= 0) r.DeclaredCount = entries.Count;
        }

        static bool IsPlausibleName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length > MaxName) return false;
            bool hasLetter = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x20 || c > 0x7E) return false;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) hasLetter = true;
            }
            return hasLetter;
        }

        /// <summary>Returns the inflated bytes, or null if the input is not a deflate stream.</summary>
        static byte[] Inflate(byte[] data, int expectedSize)
        {
            // Some writers store the block uncompressed when packing would make it bigger.
            if (expectedSize > 0 && data.Length == expectedSize) return data;

            byte[] result = TryInflate(data, 0);
            if (result != null) return result;
            if (data.Length > 2) return TryInflate(data, 2); // zlib header
            return null;
        }

        static byte[] TryInflate(byte[] data, int skip)
        {
            try
            {
                using (MemoryStream input = new MemoryStream(data, skip, data.Length - skip, false))
                using (DeflateStream inflater = new DeflateStream(input, CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int n;
                    while ((n = inflater.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, n);
                        if (output.Length > MaxHeaderBlock) return null;
                    }
                    if (output.Length == 0) return null;
                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Ascii(byte[] d, int start, int len)
        {
            char[] chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = (char)d[start + i];
            return new string(chars);
        }

        static uint U32(byte[] d, int p)
        {
            return (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
        }

        static int I32(byte[] d, int p) { return (int)U32(d, p); }

        static ushort U16(byte[] d, int p) { return (ushort)(d[p] | (d[p + 1] << 8)); }

        static ulong U64(byte[] d, int p) { return (ulong)U32(d, p) | ((ulong)U32(d, p + 4) << 32); }

        static uint PeekU32(Stream s)
        {
            long pos = s.Position;
            uint v = ReadU32(s);
            s.Position = pos;
            return v;
        }

        static uint ReadU32(Stream s)
        {
            byte[] b = new byte[4];
            ReadExact(s, b, 0, 4);
            return U32(b, 0);
        }

        static ulong ReadU64(Stream s)
        {
            byte[] b = new byte[8];
            ReadExact(s, b, 0, 8);
            return U64(b, 0);
        }

        static byte ReadByte(Stream s)
        {
            int v = s.ReadByte();
            if (v < 0) throw new EndOfStreamException();
            return (byte)v;
        }

        static byte[] ReadBlock(Stream s, int count)
        {
            if (count < 0) throw new InvalidDataException("negative block length.");
            byte[] b = new byte[count];
            ReadExact(s, b, 0, count);
            return b;
        }

        static void ReadExact(Stream s, byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buffer, offset + done, count - done);
                if (n <= 0) throw new EndOfStreamException();
                done += n;
            }
        }

        /// <summary>
        /// Random access into the decoded payload of a .ttarch2 container. Packed pages are
        /// decrypted and inflated one at a time on demand, so memory stays at a single page.
        /// </summary>
        sealed class Payload : IDisposable
        {
            public Stream Source;
            public long Start;
            public long Length;
            public bool Compressed;
            public uint WindowSize;
            public int PageCount;
            public uint[] PageSizes;
            public ulong[] PageStarts;
            public byte[] Key;
            public bool Modified;

            long _cachedIndex = -1;
            byte[] _cached;
            TtBlowfish _cipher;
            bool _cipherReady;

            public void Dispose()
            {
                _cached = null;
                _cipher = null;
            }

            /// <summary>Reads count bytes at a payload offset, or returns null if out of range.</summary>
            public byte[] Read(long position, int count)
            {
                if (position < 0 || count < 0 || position + count > Length) return null;

                byte[] result = new byte[count];
                if (!Compressed)
                {
                    Source.Seek(Start + position, SeekOrigin.Begin);
                    ReadExact(Source, result, 0, count);
                    return result;
                }

                int copied = 0;
                while (copied < count)
                {
                    long at = position + copied;
                    int page = (int)(at / WindowSize);
                    if (page >= PageCount) return null;

                    byte[] decoded = GetPage(page);
                    if (decoded == null) return null;

                    int within = (int)(at % WindowSize);
                    if (within >= decoded.Length) return null;

                    int take = Math.Min(count - copied, decoded.Length - within);
                    Buffer.BlockCopy(decoded, within, result, copied, take);
                    copied += take;
                }
                return result;
            }

            /// <summary>Reads a NUL-terminated ASCII string, or "" if the bytes are not text.</summary>
            public string ReadCString(long position, long limit, int max)
            {
                if (position < 0 || position >= limit) return "";
                int span = (int)Math.Min(max, limit - position);
                if (span <= 0) return "";

                byte[] bytes = Read(position, span);
                if (bytes == null) return "";

                int end = bytes.Length;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] == 0) { end = i; break; }
                    if (bytes[i] < 0x20 || bytes[i] > 0x7E) return "";
                }
                return Ascii(bytes, 0, end);
            }

            byte[] GetPage(int index)
            {
                if (_cached != null && _cachedIndex == index) return _cached;

                // PageStarts holds absolute file offsets, so no base is added here.
                Source.Seek((long)PageStarts[index], SeekOrigin.Begin);
                byte[] raw = new byte[PageSizes[index]];
                ReadExact(Source, raw, 0, raw.Length);

                if (Key != null)
                {
                    // Expanding a key schedule is the expensive part of Blowfish, so it is done
                    // once per archive rather than once per page.
                    if (!_cipherReady)
                    {
                        _cipher = new TtBlowfish(Key, Modified);
                        _cipherReady = true;
                    }
                    _cipher.Decipher(raw, 0, raw.Length);
                }

                byte[] decoded = null;
                if (raw.Length == WindowSize) decoded = raw;
                else
                {
                    byte[] inflated = TryInflate(raw, 0);
                    if (inflated != null) decoded = inflated;
                }
                if (decoded == null) return null;

                _cached = decoded;
                _cachedIndex = index;
                return decoded;
            }
        }
    }
}
