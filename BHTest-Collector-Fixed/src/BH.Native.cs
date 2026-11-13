// BH.Native.cs
//
// Embedded C# loaded in-process via Add-Type from PowerShell. No external
// assemblies beyond the .NET Framework base class library (System,
// System.Core)  -  nothing third-party, nothing that needs its own install.
//
// Responsibilities kept here instead of in PowerShell because they sit in
// the hot loop of a scan over millions of objects:
//   - SID binary -> string (every node, every ACE trustee)
//   - Security-descriptor / DACL parsing into ACL edges
//   - Buffered NDJSON writing (one allocation-light writer per node/edge type)
//
// Everything here is deliberately dependency-free and allocation-conscious:
// no LINQ in the per-record paths, no reflection, StringBuilder reuse where
// it matters.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BH.Native
{
    public static class GraphRelationships
    {
        public static string[] FilterTypes()
        {
            return new[] {
                "AddAllowedToAct", "AddKeyCredentialLink", "AddMember", "AddSelf",
                "AdminTo", "AllExtendedRights", "AllowedToAct", "AllowedToDelegate",
                "CanPSRemote", "CanRDP", "DCSync", "DumpSMSAPassword", "ExecuteDCOM",
                "ForceChangePassword", "GenericAll", "GenericWrite", "HasSession",
                "HasSIDHistory", "MemberOf", "Owns", "ReadGMSAPassword", "ReadLAPSPassword",
                "SQLAdmin", "WriteAccountRestrictions", "WriteDacl", "WriteGPLink",
                "WriteOwner", "WriteSPN"
            };
        }
    }

    public static class DirectoryIdentity
    {
        public static string ParentDn(string dn)
        {
            if (dn == null) return null;
            for (int i = 0; i < dn.Length; i++)
            {
                if (dn[i] == '\\') i++;
                else if (dn[i] == ',') return dn.Substring(i + 1);
            }
            return null;
        }

        public static string PrimaryGroupSid(string accountSid, string groupRid)
        {
            uint rid;
            if (accountSid == null || !accountSid.StartsWith("S-1-5-21-", StringComparison.Ordinal) ||
                !uint.TryParse(groupRid, NumberStyles.None, CultureInfo.InvariantCulture, out rid)) return null;
            return accountSid.Substring(0, accountSid.LastIndexOf('-') + 1) + rid.ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsPrivilegedGroup(string sid)
        {
            if (sid == null) return false;
            int builtin = sid.LastIndexOf("-S-1-5-32-", StringComparison.OrdinalIgnoreCase);
            if (builtin >= 0) sid = sid.Substring(builtin + 1);
            switch (sid)
            {
                case "S-1-5-32-544": // Administrators
                case "S-1-5-32-548": // Account Operators
                case "S-1-5-32-549": // Server Operators
                case "S-1-5-32-550": // Print Operators
                case "S-1-5-32-551": // Backup Operators
                    return true;
            }
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) return false;
            switch (sid.Substring(sid.LastIndexOf('-') + 1))
            {
                case "512": // Domain Admins
                case "516": // Domain Controllers
                case "518": // Schema Admins
                case "519": // Enterprise Admins
                case "521": // Read-only Domain Controllers
                case "526": // Key Admins
                case "527": // Enterprise Key Admins
                    return true;
                default:
                    return false;
            }
        }
    }
    /// <summary>Fast SID byte[] -> "S-1-5-21-..." conversion without ADSI object overhead.</summary>
    public static class Sid
    {
        public static string ToSidString(byte[] sid)
        {
            if (sid == null || sid.Length < 8) return null;

            byte revision = sid[0];
            byte subAuthorityCount = sid[1];

            // Identifier authority is a 48-bit big-endian value in bytes 2..7.
            ulong identifierAuthority = 0;
            for (int i = 2; i <= 7; i++)
            {
                identifierAuthority = (identifierAuthority << 8) | sid[i];
            }

            var sb = new StringBuilder(32);
            sb.Append("S-").Append(revision).Append('-').Append(identifierAuthority);

            int offset = 8;
            for (int i = 0; i < subAuthorityCount; i++)
            {
                if (offset + 4 > sid.Length) break;
                uint subAuthority = BitConverter.ToUInt32(sid, offset);
                sb.Append('-').Append(subAuthority);
                offset += 4;
            }

            return sb.ToString();
        }

        /// <summary>Resolves well-known domain RIDs (Domain Admins, Enterprise Admins, etc.) are left to the caller  -  this is pure binary decode only.</summary>
        public static string ToSidStringSafe(byte[] sid)
        {
            try { return ToSidString(sid); }
            catch { return null; }
        }
    }

    /// <summary>One decoded ACE, trimmed to what the graph needs.</summary>
    public struct AceEdge
    {
        public string TrusteeSid;
        public string EdgeType;
        public bool IsInherited;
    }

    /// <summary>
    /// Reads the owner and applicable simple or object-specific DACL entries.
    /// Overlapping denies for the same trustee suppress corresponding grants.
    /// </summary>
    public static class SecurityDescriptor
    {
        private const uint GenericAll = 0x10000000;
        private const uint GenericWrite = 0x40000000;
        private const uint MappedAll = 0x000F01FF;
        private const uint MappedWrite = 0x00020028;
        private const uint WriteDacl = 0x00040000;
        private const uint WriteOwner = 0x00080000;
        private const uint WriteProperty = 0x00000020;
        private const uint ControlAccess = 0x00000100;

        private static readonly Guid ForceChangePassword = new Guid("00299570-246d-11d0-a768-00aa006e0529");
        private static readonly Guid Member = new Guid("bf9679c0-0de6-11d0-a285-00aa003049e2");
        private static readonly Guid GetChanges = new Guid("1131f6aa-9c07-11d1-f79f-00c04fc2dcd2");
        private static readonly Guid GetChangesAll = new Guid("1131f6ad-9c07-11d1-f79f-00c04fc2dcd2");
        private static readonly Guid Enroll = new Guid("0e10c968-78fb-11d2-90d4-00c04f79dc55");
        private static readonly Guid AutoEnroll = new Guid("a05b8cc2-17bc-4802-a710-e7c15ab866a2");

        private struct Rule
        {
            public string Sid;
            public uint Mask;
            public Guid? ObjectType;
            public bool Inherited;
        }

        public struct ParsedSd
        {
            public string OwnerSid;
            public List<AceEdge> Aces;
        }

        public static ParsedSd Parse(byte[] sd) { return Parse(sd, null); }

        public static string[] AllowedToActPrincipals(byte[] descriptor)
        {
            var trustees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AceEdge grant in Parse(descriptor).Aces)
            {
                if (grant.EdgeType == "GenericAll" || grant.EdgeType == "AllExtendedRights")
                    trustees.Add(grant.TrusteeSid);
            }
            var result = new string[trustees.Count];
            trustees.CopyTo(result);
            return result;
        }

        public static ParsedSd Parse(byte[] sd, string kind)
        {
            var result = new ParsedSd { Aces = new List<AceEdge>() };
            if (sd == null || sd.Length < 20) return result;

            uint owner = BitConverter.ToUInt32(sd, 4);
            uint dacl = BitConverter.ToUInt32(sd, 16);
            if (owner > 0 && owner < sd.Length) result.OwnerSid = ReadSid(sd, (int)owner, sd.Length);
            if (dacl == 0 || dacl > sd.Length - 8) return result;

            int cursor = (int)dacl;
            int aclSize = BitConverter.ToUInt16(sd, cursor + 2);
            int aceCount = BitConverter.ToUInt16(sd, cursor + 4);
            int aclEnd = cursor + aclSize;
            if (aclSize < 8 || aclEnd > sd.Length) throw new InvalidDataException("Invalid AD DACL size.");
            cursor += 8;

            var allows = new List<Rule>();
            Dictionary<string, List<Rule>> denies = null;
            for (int i = 0; i < aceCount; i++)
            {
                if (cursor > aclEnd - 4) throw new InvalidDataException("Truncated AD ACE header.");
                int size = BitConverter.ToUInt16(sd, cursor + 2);
                if (size < 4 || size > aclEnd - cursor) throw new InvalidDataException("Invalid AD ACE size.");
                int end = cursor + size;
                int type = sd[cursor];
                int flags = sd[cursor + 1];
                bool simple = type == 0 || type == 1;
                bool objectAce = type == 5 || type == 6;
                if ((flags & 0x08) == 0 && (simple || objectAce)) // INHERIT_ONLY
                {
                    int sidOffset = cursor + (simple ? 8 : 12);
                    if (sidOffset > end) throw new InvalidDataException("Truncated AD ACE.");
                    uint mask = ExpandGeneric(BitConverter.ToUInt32(sd, cursor + 4));
                    Guid? objectType = null;
                    if (objectAce)
                    {
                        uint objectFlags = BitConverter.ToUInt32(sd, cursor + 8);
                        if ((objectFlags & 1) != 0)
                        {
                            if (sidOffset > end - 16) throw new InvalidDataException("Truncated AD object ACE GUID.");
                            var guidBytes = new byte[16];
                            Buffer.BlockCopy(sd, sidOffset, guidBytes, 0, 16);
                            objectType = new Guid(guidBytes);
                            sidOffset += 16;
                        }
                        if ((objectFlags & 2) != 0) sidOffset += 16;
                    }
                    string sid = ReadSid(sd, sidOffset, end);
                    if (sid == null) throw new InvalidDataException("Invalid AD ACE trustee SID.");
                    var rule = new Rule { Sid = sid, Mask = mask, ObjectType = objectType, Inherited = (flags & 0x10) != 0 };
                    if (type == 0 || type == 5) allows.Add(rule);
                    else
                    {
                        if (denies == null) denies = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
                        List<Rule> rules;
                        if (!denies.TryGetValue(sid, out rules)) denies.Add(sid, rules = new List<Rule>());
                        rules.Add(rule);
                    }
                }
                cursor = end;
            }

            Dictionary<string, int> replication = kind == "domain" ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
            foreach (Rule rule in allows)
            {
                List<Rule> restrictions = null;
                if (denies != null) denies.TryGetValue(rule.Sid, out restrictions);
                uint broad = rule.ObjectType.HasValue ? 0 : AllowedMask(rule, restrictions, null);
                if ((broad & MappedAll) == MappedAll) Emit(result.Aces, rule, "GenericAll");
                else
                {
                    if ((broad & MappedWrite) == MappedWrite) Emit(result.Aces, rule, "GenericWrite");
                    if ((broad & WriteDacl) != 0) Emit(result.Aces, rule, "WriteDacl");
                    if ((broad & WriteOwner) != 0) Emit(result.Aces, rule, "WriteOwner");
                    if ((broad & ControlAccess) != 0) Emit(result.Aces, rule, "AllExtendedRights");
                }

                if ((kind == null || kind == "user") && rule.ObjectType == ForceChangePassword &&
                    (AllowedMask(rule, restrictions, ForceChangePassword) & ControlAccess) != 0)
                    Emit(result.Aces, rule, "ForceChangePassword");
                if ((kind == null || kind == "group") && rule.ObjectType == Member &&
                    (AllowedMask(rule, restrictions, Member) & WriteProperty) != 0)
                    Emit(result.Aces, rule, "AddMember");
                if (kind == "certtemplates" || kind == "enterprisecas")
                {
                    if (rule.ObjectType == Enroll && (AllowedMask(rule, restrictions, Enroll) & ControlAccess) != 0)
                        Emit(result.Aces, rule, "Enroll");
                    if (rule.ObjectType == AutoEnroll && (AllowedMask(rule, restrictions, AutoEnroll) & ControlAccess) != 0)
                        Emit(result.Aces, rule, "AutoEnroll");
                }

                if (replication != null && (rule.Mask & ControlAccess) != 0)
                {
                    int rights;
                    replication.TryGetValue(rule.Sid, out rights);
                    if ((!rule.ObjectType.HasValue || rule.ObjectType == GetChanges) &&
                        (AllowedMask(rule, restrictions, GetChanges) & ControlAccess) != 0) rights |= 1;
                    if ((!rule.ObjectType.HasValue || rule.ObjectType == GetChangesAll) &&
                        (AllowedMask(rule, restrictions, GetChangesAll) & ControlAccess) != 0) rights |= 2;
                    if (rights == 3 && (!replication.ContainsKey(rule.Sid) || replication[rule.Sid] != 3))
                        Emit(result.Aces, rule, "DCSync");
                    replication[rule.Sid] = rights;
                }
            }
            return result;
        }

        private static void Emit(List<AceEdge> edges, Rule rule, string type)
        {
            edges.Add(new AceEdge { TrusteeSid = rule.Sid, EdgeType = type, IsInherited = rule.Inherited });
        }

        private static uint ExpandGeneric(uint mask)
        {
            if ((mask & GenericAll) != 0) mask |= MappedAll;
            if ((mask & GenericWrite) != 0) mask |= MappedWrite;
            return mask;
        }

        // Suppress overlapping denies for the same trustee. Effective access
        // across group tokens and conditional ACEs requires additional modeling.
        private static uint AllowedMask(Rule allow, List<Rule> denies, Guid? scope)
        {
            uint mask = allow.Mask;
            if (denies == null) return mask;
            foreach (Rule deny in denies)
            {
                if (!scope.HasValue || !deny.ObjectType.HasValue || deny.ObjectType == scope) mask &= ~deny.Mask;
            }
            return mask;
        }

        private static string ReadSid(byte[] bytes, int offset, int end)
        {
            if (offset < 0 || offset > end - 8) return null;
            int length = 8 + bytes[offset + 1] * 4;
            if (length > end - offset) return null;
            var sid = new byte[length];
            Buffer.BlockCopy(bytes, offset, sid, 0, length);
            return Sid.ToSidStringSafe(sid);
        }
    }
    /// <summary>
    /// Minimal, dependency-free JSON value writer  -  enough for flat NDJSON
    /// node/edge records (strings, bools, longs, string arrays). Not a
    /// general-purpose serializer; deliberately so, to stay fast and avoid
    /// pulling in a third-party JSON library.
    /// </summary>
    public sealed class JsonRecordWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(512);

        public JsonRecordWriter Begin()
        {
            _sb.Clear();
            _sb.Append('{');
            _first = true;
            return this;
        }

        private bool _first = true;

        private void Comma()
        {
            if (!_first) _sb.Append(',');
            _first = false;
        }

        public JsonRecordWriter Prop(string name, string value)
        {
            Comma();
            WriteEscapedKey(name);
            if (value == null) { _sb.Append("null"); return this; }
            _sb.Append('"');
            EscapeInto(value, _sb);
            _sb.Append('"');
            return this;
        }

        /// <summary>
        /// String-only variant, for values that may be null. PowerShell resolves
        /// Prop(name, $null) to the bool overload (writing false), so callers with
        /// nullable string fields use this one, which can only mean "string or null".
        /// </summary>
        public JsonRecordWriter PropS(string name, string value)
        {
            // PowerShell turns $null into "" when calling a string method, so
            // empty is treated as "missing" and written as JSON null.
            return Prop(name, string.IsNullOrEmpty(value) ? null : value);
        }

        public JsonRecordWriter Prop(string name, bool value)
        {
            Comma();
            WriteEscapedKey(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonRecordWriter Prop(string name, long value)
        {
            Comma();
            WriteEscapedKey(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonRecordWriter PropArray(string name, IEnumerable<string> values)
        {
            Comma();
            WriteEscapedKey(name);
            _sb.Append('[');
            bool firstItem = true;
            foreach (var v in values)
            {
                if (!firstItem) _sb.Append(',');
                firstItem = false;
                _sb.Append('"');
                EscapeInto(v, _sb);
                _sb.Append('"');
            }
            _sb.Append(']');
            return this;
        }

        private void WriteEscapedKey(string name)
        {
            _sb.Append('"');
            EscapeInto(name, _sb);
            _sb.Append("\":");
        }

        public string End()
        {
            _sb.Append('}');
            return _sb.ToString();
        }

        private static void EscapeInto(string s, StringBuilder sb)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Buffered append-only writer for one NDJSON file. One instance per
    /// node/edge type per scan; callers from multiple runspaces should use
    /// one writer per runspace and let the collector merge files, rather
    /// than sharing a writer  -  this class is NOT thread-safe by itself,
    /// which keeps it lock-free and fast on the single-writer path.
    /// </summary>
    public static class NdjsonReader
    {
        public static IEnumerable<string> Lines(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? (Stream)new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress) : file)
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 65536))
            {
                string line;
                while ((line = reader.ReadLine()) != null) if (line.Length > 0) yield return line;
            }
        }
    }

    public sealed class NdjsonWriter : IDisposable
    {
        private StreamWriter writer;
        private readonly string path;
        private readonly bool append;

        public NdjsonWriter(string path, bool append)
        {
            this.path = path;
            this.append = append;
            // A retried shard must replace previous data even if it is now empty.
            if (File.Exists(path)) Open();
        }

        private void Open()
        {
            var file = new FileStream(path, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
            Stream output = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? (Stream)new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest) : file;
            writer = new StreamWriter(output, new UTF8Encoding(false), 65536);
        }

        public void WriteLine(string json)
        {
            if (writer == null) Open();
            writer.Write(json);
            writer.Write('\n');
        }

        public void Flush() { if (writer != null) writer.Flush(); }
        public void Dispose() { if (writer != null) { writer.Dispose(); writer = null; } }
    }
    /// <summary>
    /// Fast, allocation-light extraction of one or two string fields from a
    /// flat JSON object written by JsonRecordWriter  -  a hand-scanner instead
    /// of a general parser, since the merge engine needs to read only a
    /// couple of fields (Id, Dn) out of every line across millions of lines
    /// and a full JSON parse there would dominate merge time for no benefit.
    /// Only handles the flat/non-nested, backslash-or-plain-string shape
    /// JsonRecordWriter itself produces  -  not a general-purpose JSON reader.
    /// </summary>
    public static class JsonRecordReader
    {
        public static string NormalizeEdge(string record, string source, string target, bool sourceAlias, bool targetAlias)
        {
            var json = new JsonRecordWriter();
            string sourceToken = json.Begin().Prop("v", source).End();
            record = ReplaceToken(record, "Source", sourceToken.Substring(5, sourceToken.Length - 6));
            string targetToken = json.Begin().Prop("v", target).End();
            record = ReplaceToken(record, "Target", targetToken.Substring(5, targetToken.Length - 6));
            record = ReplaceToken(record, "SourceIsDn", sourceAlias ? "true" : "false");
            return ReplaceToken(record, "TargetIsDn", targetAlias ? "true" : "false");
        }

        private static string ReplaceToken(string json, string field, string token)
        {
            string key = "\"" + field + "\":";
            int start = json.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return json;
            start += key.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            int end = start;
            if (end < json.Length && json[end] == '"')
            {
                end++;
                while (end < json.Length && json[end] != '"') { if (json[end] == '\\') end++; end++; }
                end++;
            }
            else
            {
                while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
            }
            if (end > json.Length) throw new InvalidDataException("Invalid edge JSON.");
            return json.Substring(0, start) + token + json.Substring(end);
        }

        public static IEnumerable<string> StringArray(string json, string fieldName)
        {
            int cursor = json.IndexOf("\"" + fieldName + "\":", StringComparison.Ordinal);
            if (cursor < 0) yield break;
            cursor += fieldName.Length + 3;
            while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
            if (cursor >= json.Length || json[cursor++] != '[') yield break;
            while (cursor < json.Length)
            {
                while (cursor < json.Length && (char.IsWhiteSpace(json[cursor]) || json[cursor] == ',')) cursor++;
                if (cursor >= json.Length || json[cursor] == ']') yield break;
                if (json[cursor] != '"') throw new InvalidDataException("Expected a string array in " + fieldName);
                int start = cursor++;
                while (cursor < json.Length && json[cursor] != '"')
                {
                    if (json[cursor] == '\\') cursor++;
                    cursor++;
                }
                if (cursor == json.Length) throw new InvalidDataException("Truncated string array in " + fieldName);
                string value = ExtractField("{\"v\":" + json.Substring(start, cursor++ - start + 1) + "}", "v");
                if (value == null) throw new InvalidDataException("Invalid escaped string in " + fieldName);
                yield return value;
            }
            throw new InvalidDataException("Truncated array in " + fieldName);
        }

        public static string ExtractField(string json, string fieldName)
        {
            string needle = "\"" + fieldName + "\":";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return null;
            int cursor = idx + needle.Length;
            while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
            if (cursor >= json.Length) return null;

            if (json[cursor] != '"')
            {
                // Bare literal: null, true, false, or a number. Scan to the
                // next structural character and return the raw token text
                // (so a boolean/number field is still usable by callers
                // that just want its string form, e.g. CSV export).
                int litStart = cursor;
                while (cursor < json.Length && json[cursor] != ',' && json[cursor] != '}' && json[cursor] != ']')
                {
                    cursor++;
                }
                string token = json.Substring(litStart, cursor - litStart).TrimEnd();
                return token == "null" ? null : token;
            }

            cursor++;
            int start = cursor;
            while (cursor < json.Length && json[cursor] != '"' && json[cursor] != '\\') cursor++;
            if (cursor == json.Length) return null;
            if (json[cursor] == '"') return json.Substring(start, cursor - start);
            var sb = new StringBuilder(64);
            sb.Append(json, start, cursor - start);
            while (cursor < json.Length)
            {
                char c = json[cursor];
                if (c == '\\' && cursor + 1 < json.Length)
                {
                    char next = json[cursor + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            ushort code;
                            if (cursor + 6 > json.Length || !ushort.TryParse(json.Substring(cursor + 2, 4),
                                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) return null;
                            sb.Append((char)code);
                            cursor += 4;
                            break;
                        default: return null;
                    }
                    cursor += 2;
                    continue;
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                cursor++;
            }
            return null;
        }
    }

    /// <summary>Thread-safe counter bag, used for live progress reporting from parallel runspaces back to the GUI.</summary>
    public sealed class ProgressCounters
    {
        private long _objectsProcessed;
        private long _edgesEmitted;
        private long _errors;

        public void AddObjects(long n) { System.Threading.Interlocked.Add(ref _objectsProcessed, n); }
        public void AddEdges(long n) { System.Threading.Interlocked.Add(ref _edgesEmitted, n); }
        public void AddError() { System.Threading.Interlocked.Increment(ref _errors); }
        public void AddErrors(long count) { System.Threading.Interlocked.Add(ref _errors, count); }

        public void Reset()
        {
            System.Threading.Interlocked.Exchange(ref _objectsProcessed, 0);
            System.Threading.Interlocked.Exchange(ref _edgesEmitted, 0);
            System.Threading.Interlocked.Exchange(ref _errors, 0);
        }

        public static ProgressCounters Sum(ProgressCounters[] shards)
        {
            var total = new ProgressCounters();
            foreach (var shard in shards)
            {
                total._objectsProcessed += shard.Objects;
                total._edgesEmitted += shard.Edges;
                total._errors += shard.Errors;
            }
            return total;
        }

        public long Objects { get { return System.Threading.Interlocked.Read(ref _objectsProcessed); } }
        public long Edges { get { return System.Threading.Interlocked.Read(ref _edgesEmitted); } }
        public long Errors { get { return System.Threading.Interlocked.Read(ref _errors); } }
    }

    /// <summary>
    /// In-memory OU hierarchy index for the GUI's OU picker, plus its on-disk
    /// cache (plain tab-separated text: a header line, then dn, name, parent
    /// per row). Parsing and indexing here instead of in PowerShell loops is
    /// what keeps opening the cached OU list fast for domains with tens of
    /// thousands of OUs. The tree itself is built lazily by the GUI, one
    /// level at a time, from GetChildren().
    /// </summary>
    public sealed class OuIndex
    {
        public string RootName;
        public string RootDn;
        public string Server;
        public string SavedUtc;
        public int Count;

        private Dictionary<string, List<string[]>> _children;

        public static OuIndex FromRows(string[] dns, string[] names, string[] parents)
        {
            var idx = new OuIndex();
            idx._children = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
            idx.Count = dns.Length;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < dns.Length; i++)
            {
                known.Add(dns[i]);
                if (idx.RootDn == null && string.IsNullOrEmpty(parents[i]))
                {
                    idx.RootDn = dns[i];
                    idx.RootName = names[i];
                }
            }
            if (idx.RootDn == null) return idx;

            for (int i = 0; i < dns.Length; i++)
            {
                if (string.Equals(dns[i], idx.RootDn, StringComparison.OrdinalIgnoreCase)) continue;

                // A parent that is not itself an OU (a plain container) is
                // attached to the root rather than dropped silently.
                string p = (parents[i] != null && known.Contains(parents[i])) ? parents[i] : idx.RootDn;
                List<string[]> list;
                if (!idx._children.TryGetValue(p, out list))
                {
                    list = new List<string[]>();
                    idx._children[p] = list;
                }
                list.Add(new string[] { names[i], dns[i] });
            }
            return idx;
        }

        /// <summary>Returns null when the file is missing/unreadable or was cached for a different server.</summary>
        public static OuIndex LoadTsv(string path, string expectedServer)
        {
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2) return null;

            string[] hdr = lines[0].Split('\t');
            if (hdr.Length < 3 || hdr[0] != "#BH-OU-CACHE") return null;
            if (!string.Equals(hdr[1], expectedServer, StringComparison.OrdinalIgnoreCase)) return null;

            int n = lines.Length - 1;
            var dns = new string[n];
            var names = new string[n];
            var parents = new string[n];
            for (int i = 0; i < n; i++)
            {
                string[] f = lines[i + 1].Split('\t');
                dns[i] = f[0];
                names[i] = f.Length > 1 ? f[1] : f[0];
                parents[i] = f.Length > 2 ? f[2] : string.Empty;
            }

            var idx = FromRows(dns, names, parents);
            idx.Server = hdr[1];
            idx.SavedUtc = hdr[2];
            return idx;
        }

        public static void SaveTsv(string path, string server, string[] dns, string[] names, string[] parents)
        {
            var sb = new StringBuilder(dns.Length * 80);
            sb.Append("#BH-OU-CACHE\t").Append(Clean(server)).Append('\t')
              .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < dns.Length; i++)
            {
                sb.Append(Clean(dns[i])).Append('\t')
                  .Append(Clean(names[i])).Append('\t')
                  .Append(Clean(parents[i])).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public bool HasChildren(string dn)
        {
            return _children != null && _children.ContainsKey(dn);
        }

        /// <summary>Direct children of dn as { name, dn } pairs, sorted by name.</summary>
        public string[][] GetChildren(string dn)
        {
            List<string[]> list;
            if (_children == null || !_children.TryGetValue(dn, out list)) return new string[0][];
            string[][] arr = list.ToArray();
            Array.Sort(arr, delegate (string[] a, string[] b) { return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase); });
            return arr;
        }

        private static string Clean(string s)
        {
            if (s == null) return string.Empty;
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }

    /// <summary>
    /// Finds the ACL trustees (permission holders) of a scan that are not any
    /// collected node. A scan of a few OUs records permissions held by
    /// principals that live elsewhere (Domain Admins, builtin groups, ...);
    /// those SIDs have no name in the scan data and are what the post-scan
    /// name lookup resolves.
    /// </summary>
    public static class UnresolvedSids
    {
        // Bound the candidate set before checking collected nodes. Name enrichment
        // is optional and must not allocate an all-directory SID set.
        public static string[] Collect(string[] nodeFiles, string[] aclEdgeFiles, int limit)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in aclEdgeFiles)
            {
                foreach (string line in NdjsonReader.Lines(file))
                {
                    string sid = JsonRecordReader.ExtractField(line, "Source");
                    if (sid != null && sid.StartsWith("S-1-", StringComparison.Ordinal)) candidates.Add(sid);
                    if (candidates.Count >= limit) break;
                }
                if (candidates.Count >= limit) break;
            }
            foreach (string file in nodeFiles)
            {
                foreach (string line in NdjsonReader.Lines(file))
                {
                    string id = JsonRecordReader.ExtractField(line, "Id");
                    if (id != null) candidates.Remove(id);
                }
            }
            var result = new List<string>(candidates);
            result.Sort(StringComparer.Ordinal);
            return result.ToArray();
        }
    }
}
