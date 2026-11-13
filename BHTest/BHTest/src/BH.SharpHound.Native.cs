using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace BH.Native
{
    public sealed class SharpHoundMetadata
    {
        public string Type;
        public long Count;
        public int Version;
    }

    public sealed class SharpHoundImport : IDisposable
    {
        private readonly string directory;
        private readonly Dictionary<string, NdjsonWriter> files = new Dictionary<string, NdjsonWriter>();
        private readonly JsonRecordWriter json = new JsonRecordWriter();
        public long Nodes { get; private set; }
        public long Edges { get; private set; }

        private static readonly Dictionary<string, string> NodeKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "users", "users" }, { "groups", "groups" }, { "computers", "computers" },
            { "domains", "domains" }, { "ous", "ous" }, { "containers", "ous" }, { "gpos", "gpos" },
            { "sites", "sites" }, { "siteservers", "siteservers" }, { "sitesubnets", "sitesubnets" },
            { "certtemplates", "certtemplates" }, { "enterprisecas", "enterprisecas" },
            { "rootcas", "rootcas" }, { "aiacas", "aiacas" }, { "ntauthstores", "ntauthstores" },
            { "issuancepolicies", "issuancepolicies" }
        };

        // Keep unmodeled ACE rights visible in exported data, outside traversal.
        private static readonly HashSet<string> TraversableRights = new HashSet<string>(StringComparer.Ordinal)
        {
            "GenericAll", "GenericWrite", "WriteDacl", "WriteOwner", "Owns", "AllExtendedRights",
            "ForceChangePassword", "AddMember", "AddSelf", "AddAllowedToAct", "AddKeyCredentialLink",
            "WriteSPN", "WriteAccountRestrictions", "WriteGPLink", "ReadLAPSPassword",
            "ReadGMSAPassword", "DCSync"
        };

        public SharpHoundImport(string directory) { this.directory = directory; }

        public static bool Supports(string type) { return type != null && NodeKinds.ContainsKey(type); }

        public static Stream SkipUtf8Bom(Stream input) { return new JsonStream(input); }

        // ZIP entry streams are forward-only. Replay the first bytes if they
        // are data; legacy .NET's JSON reader does not strip a UTF-8 BOM.
        private sealed class JsonStream : Stream
        {
            private readonly Stream input;
            private readonly byte[] prefix = new byte[3];
            private readonly int length;
            private int offset;

            public JsonStream(Stream input)
            {
                this.input = input;
                while (length < prefix.Length)
                {
                    int read = input.Read(prefix, length, prefix.Length - length);
                    if (read == 0) break;
                    length += read;
                }
                if (length == 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf) offset = 3;
            }

            public override int Read(byte[] buffer, int start, int count)
            {
                int copied = Math.Min(length - offset, count);
                Array.Copy(prefix, offset, buffer, start, copied);
                offset += copied;
                return copied == count ? copied : copied + input.Read(buffer, start + copied, count - copied);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        public static SharpHoundMetadata ReadMetadata(XmlReader reader)
        {
            var doc = new XmlDocument();
            reader.MoveToContent();
            if (reader.GetAttribute("type") != "object") throw new InvalidDataException("Expected a SharpHound JSON object.");
            reader.Read();
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (reader.LocalName == "meta")
                {
                    var metadata = (XmlElement)doc.ReadNode(reader);
                    int version;
                    long count;
                    string type = Text(metadata, "type");
                    if (string.IsNullOrWhiteSpace(type) ||
                        !int.TryParse(Text(metadata, "version"), NumberStyles.None, CultureInfo.InvariantCulture, out version) ||
                        !long.TryParse(Text(metadata, "count"), NumberStyles.None, CultureInfo.InvariantCulture, out count))
                        throw new InvalidDataException("SharpHound metadata must contain a type, version and record count.");
                    return new SharpHoundMetadata { Type = type, Version = version, Count = count };
                }
                reader.Skip();
            }
            throw new InvalidDataException("SharpHound metadata is missing.");
        }

        public void ReadRecords(XmlReader reader, SharpHoundMetadata meta)
        {
            string kind = NodeKinds[meta.Type];
            var doc = new XmlDocument();
            reader.MoveToContent();
            reader.Read();
            long rows = 0;
            bool found = false;
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (reader.LocalName != "data")
                {
                    reader.Skip();
                    continue;
                }
                if (found || reader.GetAttribute("type") != "array") throw new InvalidDataException("Expected one SharpHound data array.");
                found = true;
                bool empty = reader.IsEmptyElement;
                reader.Read();
                if (empty) continue;
                while (reader.MoveToContent() == XmlNodeType.Element)
                {
                    var record = (XmlElement)doc.ReadNode(reader);
                    if (record.GetAttribute("type") != "object") throw new InvalidDataException("Expected a SharpHound object record.");
                    rows++;
                    if (!Flag(record, "IsDeleted")) WriteRecord(record, kind);
                }
                reader.ReadEndElement();
            }
            reader.ReadEndElement();
            if (reader.MoveToContent() != XmlNodeType.None || !found || rows != meta.Count)
                throw new InvalidDataException("SharpHound data array or record count does not match metadata.");
        }

        private void WriteRecord(XmlElement record, string kind)
        {
            string id = Id(Text(record, "ObjectIdentifier"));
            if (string.IsNullOrEmpty(id)) throw new InvalidDataException("A SharpHound object has no ObjectIdentifier.");
            XmlElement properties = record["Properties"];
            string dn = Text(properties, "distinguishedname");
            string name = Text(properties, "name") ?? Text(properties, "displayname") ?? id;
            json.Begin().Prop("Id", id).PropS("Dn", dn).Prop("Name", name)
                .PropS("SamAccountName", Text(properties, "samaccountname")).PropS("Os", Text(properties, "operatingsystem"));
            if (kind == "users" && Text(properties, "enabled") != null) json.Prop("Disabled", !Flag(properties, "enabled"));
            if (kind == "groups" && Text(properties, "securityenabled") != null)
                json.Prop("SecurityEnabled", Flag(properties, "securityenabled"));
            FileFor("nodes_" + kind).WriteLine(json.End());
            Nodes++;

            foreach (XmlElement member in Items(record, "Members")) Edge("memberof", Principal(member), id, "MemberOf");
            Edge("memberof", id, Id(Text(record, "PrimaryGroupSID")), "MemberOf");
            foreach (XmlElement child in Items(record, "ChildObjects")) Edge("contains", id, Principal(child), "Contains");
            Edge("contains", Principal(record["ContainedBy"]), id, "Contains");
            foreach (XmlElement link in Items(record, "Links")) Edge("gplink", Id(Text(link, "Guid")), id, "GPLink");
            foreach (XmlElement target in Items(record, "AllowedToDelegate")) Edge("access", id, Principal(target), "AllowedToDelegate");
            foreach (XmlElement principal in Items(record, "AllowedToAct")) Edge("access", Principal(principal), id, "AllowedToAct");
            foreach (XmlElement principal in Items(record, "HasSIDHistory")) Edge("access", id, Principal(principal), "HasSIDHistory");
            foreach (XmlElement principal in Items(record, "DumpSMSAPassword")) Edge("access", id, Principal(principal), "DumpSMSAPassword");
            foreach (XmlElement target in Items(record, "SPNTargets"))
                if (Text(target, "Service") == "SQLAdmin") Edge("access", id, Id(Text(target, "ComputerSID")), "SQLAdmin");

            var replication = kind == "domains" ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
            foreach (XmlElement ace in Items(record, "Aces"))
            {
                string trustee = Id(Text(ace, "PrincipalSID"));
                string right = Text(ace, "RightName");
                if (string.IsNullOrEmpty(trustee) || string.IsNullOrEmpty(right)) continue;
                Edge(TraversableRights.Contains(right) ? "acl" : "observed", trustee, id, right);
                if (replication == null || (right != "GetChanges" && right != "GetChangesAll")) continue;
                int granted;
                replication.TryGetValue(trustee, out granted);
                granted |= right == "GetChanges" ? 1 : 2;
                replication[trustee] = granted;
            }
            if (replication != null)
                foreach (var trustee in replication)
                    if (trustee.Value == 3) Edge("acl", trustee.Key, id, "DCSync");

            foreach (XmlElement trust in Items(record, "Trusts"))
                Edge("observed", id, Id(Text(trust, "TargetDomainSid")), "TrustRelationship");
            foreach (XmlElement template in Items(record, "EnabledCertTemplates"))
                Edge("observed", Principal(template), id, "PublishedTo");
            if (kind == "computers") WriteComputerAccess(record, id);
            WritePolicyAccess(record["GPOChanges"]);
        }

        private void WriteComputerAccess(XmlElement record, string id)
        {
            foreach (string field in new[] { "Sessions", "PrivilegedSessions", "RegistrySessions" })
            {
                XmlElement result = record[field];
                if (!Flag(result, "Collected")) continue;
                foreach (XmlElement session in Items(result, "Results"))
                    Edge("access", Id(Text(session, "ComputerSID")) ?? id, Id(Text(session, "UserSID")), "HasSession");
            }

            foreach (XmlElement group in Items(record, "LocalGroups"))
            {
                if (!Flag(group, "Collected")) continue;
                string identifier = Text(group, "ObjectIdentifier") ?? "";
                string right = null;
                if (identifier.EndsWith("-544", StringComparison.Ordinal)) right = "AdminTo";
                else if (identifier.EndsWith("-555", StringComparison.Ordinal)) right = "CanRDP";
                else if (identifier.EndsWith("-562", StringComparison.Ordinal)) right = "ExecuteDCOM";
                else if (identifier.EndsWith("-580", StringComparison.Ordinal)) right = "CanPSRemote";
                if (right != null)
                    foreach (XmlElement member in Items(group, "Results")) Edge("access", Principal(member), id, right);
            }
            WriteCollectedGroup(record["LocalAdmins"], id, "AdminTo");
            WriteCollectedGroup(record["RemoteDesktopUsers"], id, "CanRDP");
            WriteCollectedGroup(record["DcomUsers"], id, "ExecuteDCOM");
            WriteCollectedGroup(record["PSRemoteUsers"], id, "CanPSRemote");
        }

        private void WriteCollectedGroup(XmlElement group, string id, string right)
        {
            if (!Flag(group, "Collected")) return;
            foreach (XmlElement member in Items(group, "Results")) Edge("access", Principal(member), id, right);
        }

        private void WritePolicyAccess(XmlElement policy)
        {
            if (policy == null) return;
            foreach (XmlElement computer in Items(policy, "AffectedComputers"))
            {
                string target = Principal(computer);
                foreach (XmlElement member in Items(policy, "LocalAdmins")) Edge("access", Principal(member), target, "AdminTo");
                foreach (XmlElement member in Items(policy, "RemoteDesktopUsers")) Edge("access", Principal(member), target, "CanRDP");
                foreach (XmlElement member in Items(policy, "DcomUsers")) Edge("access", Principal(member), target, "ExecuteDCOM");
                foreach (XmlElement member in Items(policy, "PSRemoteUsers")) Edge("access", Principal(member), target, "CanPSRemote");
            }
        }

        private void Edge(string bucket, string source, string target, string kind)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
            FileFor("edges_" + bucket).WriteLine(json.Begin().Prop("Source", source).Prop("Target", target).Prop("EdgeType", kind).End());
            Edges++;
        }

        private NdjsonWriter FileFor(string prefix)
        {
            NdjsonWriter writer;
            if (!files.TryGetValue(prefix, out writer))
            {
                writer = new NdjsonWriter(Path.Combine(directory, prefix + ".sharphound.ndjson"), false);
                files.Add(prefix, writer);
            }
            return writer;
        }

        private static IEnumerable<XmlElement> Items(XmlElement record, string field)
        {
            XmlElement array = record == null ? null : record[field];
            if (array == null) yield break;
            foreach (XmlNode item in array.ChildNodes)
            {
                var element = item as XmlElement;
                if (element != null) yield return element;
            }
        }

        private static string Text(XmlElement record, string field)
        {
            XmlElement value = record == null ? null : record[field];
            return value == null || value.GetAttribute("type") == "null" ? null : value.InnerText;
        }

        private static bool Flag(XmlElement record, string field) { return Text(record, field) == "true"; }
        private static string Principal(XmlElement principal) { return Id(Text(principal, "ObjectIdentifier")); }

        private static string Id(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            Guid guid;
            return Guid.TryParse(value, out guid) ? guid.ToString("B").ToUpperInvariant() : value.ToUpperInvariant();
        }

        public void Dispose()
        {
            foreach (NdjsonWriter writer in files.Values) writer.Dispose();
        }
    }
}
