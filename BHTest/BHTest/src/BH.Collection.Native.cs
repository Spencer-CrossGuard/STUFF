using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BH.Native
{
    public sealed class LdapEntryWriter : IDisposable
    {
        private readonly string directory;
        private readonly string shard;
        private readonly bool compress;
        private readonly string domainId;
        private readonly Func<SearchResultEntry, string, string[]> ranged;
        private readonly Dictionary<string, NdjsonWriter> writers = new Dictionary<string, NdjsonWriter>();
        private readonly JsonRecordWriter node = new JsonRecordWriter();
        private readonly JsonRecordWriter edge = new JsonRecordWriter();
        private static readonly string[] EmptyStrings = new string[0];
        private static readonly string[] KindNames = {
            null, "ous", "domains", "users", "groups", "computers", "gpos", "sites",
            "trusts", "rootcas", "enterprisecas", "certtemplates"
        };
        private static readonly Dictionary<string, int> ClassRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
            { "organizationalUnit", 1 }, { "container", 1 }, { "builtinDomain", 1 }, { "domainDNS", 2 },
            { "user", 3 }, { "group", 4 }, { "computer", 5 }, { "groupPolicyContainer", 6 },
            { "site", 7 }, { "trustedDomain", 8 }, { "certificationAuthority", 9 },
            { "pKIEnrollmentService", 10 }, { "pKICertificateTemplate", 11 }
        };
        private static readonly Regex GpLinks = new Regex(
            @"\[LDAP://cn=(\{[0-9a-f-]+\})[^;\]]*;(\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public long Objects { get; private set; }
        public long Edges { get; private set; }
        public long MissingDescriptors { get; private set; }

        public LdapEntryWriter(string directory, string shard, bool compress, string domainId,
            Func<SearchResultEntry, string, string[]> ranged)
        {
            this.directory = directory;
            this.shard = shard;
            this.compress = compress;
            this.domainId = domainId;
            this.ranged = ranged;
            if (!Regex.IsMatch(shard, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Invalid shard identifier.");
            // Remove stale data for this shard before a reconnect restarts it.
            foreach (string file in Directory.EnumerateFiles(directory, "*." + shard + ".ndjson*"))
                File.Delete(file);
        }

        private static string Text(SearchResultEntry entry, string name)
        {
            if (!entry.Attributes.Contains(name) || entry.Attributes[name].Count == 0) return null;
            object value = entry.Attributes[name][0];
            return value is byte[] ? System.Text.Encoding.UTF8.GetString((byte[])value) : (string)value;
        }

        private static byte[] Bytes(SearchResultEntry entry, string name)
        {
            if (!entry.Attributes.Contains(name) || entry.Attributes[name].Count == 0) return null;
            return (byte[])entry.Attributes[name].GetValues(typeof(byte[]))[0];
        }

        private string[] Strings(SearchResultEntry entry, string name)
        {
            if (entry.Attributes.Contains(name)) return (string[])entry.Attributes[name].GetValues(typeof(string));
            foreach (string attribute in entry.Attributes.AttributeNames)
                if (attribute.StartsWith(name + ";range=", StringComparison.OrdinalIgnoreCase)) return ranged(entry, name);
            return EmptyStrings;
        }

        private static string Kind(SearchResultEntry entry)
        {
            if (!entry.Attributes.Contains("objectClass")) return null;
            int rank = 0;
            foreach (string name in entry.Attributes["objectClass"].GetValues(typeof(string)))
            {
                int candidate;
                if (ClassRanks.TryGetValue(name, out candidate) && candidate > rank) rank = candidate;
            }
            if (rank == 9 && entry.DistinguishedName.StartsWith("CN=NTAuthCertificates,", StringComparison.OrdinalIgnoreCase)) return "ntauthstores";
            return KindNames[rank];
        }

        private NdjsonWriter Writer(string prefix)
        {
            NdjsonWriter writer;
            if (!writers.TryGetValue(prefix, out writer))
            {
                writer = new NdjsonWriter(Path.Combine(directory, prefix + "." + shard + ".ndjson" + (compress ? ".gz" : "")), false);
                writers.Add(prefix, writer);
            }
            return writer;
        }

        private void Edge(string bucket, string source, string target, string type, bool sourceDn, bool targetDn)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
            edge.Begin().Prop("Source", source).Prop("Target", target).Prop("EdgeType", type);
            if (sourceDn) edge.Prop("SourceIsDn", true);
            if (targetDn) edge.Prop("TargetIsDn", true);
            Writer("edges_" + bucket).WriteLine(edge.End());
            Edges++;
        }

        public void Write(SearchResultEntry entry)
        {
            string kind = Kind(entry);
            if (kind == null) return;
            string dn = entry.DistinguishedName;
            byte[] sidBytes = Bytes(entry, "objectSid");
            byte[] guidBytes = Bytes(entry, "objectGUID");
            string sid = sidBytes == null ? null : Sid.ToSidStringSafe(sidBytes);
            string guid = guidBytes == null ? null : new Guid(guidBytes).ToString("B").ToUpperInvariant();
            string id = sid ?? guid ?? dn;
            string name = Text(entry, "displayName") ?? Text(entry, "name");
            if (kind == "gpos")
            {
                Guid policy;
                if (Guid.TryParse(Text(entry, "name"), out policy)) id = policy.ToString("B").ToUpperInvariant();
            }
            node.Begin().Prop("Id", id).PropS("Sid", sid).PropS("Guid", guid).Prop("Dn", dn).PropS("Name", name);
            long uac;
            long.TryParse(Text(entry, "userAccountControl"), out uac);
            if (kind == "users" || kind == "computers")
            {
                node.PropS("SamAccountName", Text(entry, "sAMAccountName")).Prop("Uac", uac).Prop("Disabled", (uac & 2) != 0)
                    .Prop("UnconstrainedDelegation", (uac & 0x80000) != 0).Prop("ProtocolTransition", (uac & 0x1000000) != 0);
                if (kind == "computers") node.PropS("Dns", Text(entry, "dNSHostName")).PropS("Os", Text(entry, "operatingSystem"));
                node.PropArray("Spns", Strings(entry, "servicePrincipalName"));
            }
            if (kind == "groups")
            {
                long flags;
                if (long.TryParse(Text(entry, "groupType"), out flags)) node.Prop("SecurityEnabled", (flags & 0x80000000L) != 0);
            }
            if (kind == "trusts")
            {
                byte[] trusted = Bytes(entry, "securityIdentifier");
                string remote = trusted == null ? null : Sid.ToSidStringSafe(trusted);
                node.PropS("TargetDomainSid", remote).PropS("TrustDirection", Text(entry, "trustDirection"))
                    .PropS("TrustType", Text(entry, "trustType")).PropS("TrustAttributes", Text(entry, "trustAttributes"))
                    .PropS("FlatName", Text(entry, "flatName"));
                Edge("observed", domainId, remote, "TrustRelationship", false, false);
            }
            if (kind == "certtemplates")
            {
                node.PropS("TemplateName", Text(entry, "name")).PropS("CertificateNameFlags", Text(entry, "msPKI-Certificate-Name-Flag"))
                    .PropS("EnrollmentFlags", Text(entry, "msPKI-Enrollment-Flag")).PropS("RequiredSignatures", Text(entry, "msPKI-RA-Signature"))
                    .PropArray("ExtendedKeyUsage", Strings(entry, "pKIExtendedKeyUsage"))
                    .PropS("SchemaVersion", Text(entry, "msPKI-Template-Schema-Version"));
                // A stable alias resolves published templates across different scan folders.
                node.PropS("TemplateAlias", Text(entry, "name"));
            }
            if (kind == "enterprisecas")
            {
                node.PropS("Dns", Text(entry, "dNSHostName")).PropArray("PublishedTemplates", Strings(entry, "certificateTemplates"));
                foreach (string template in Strings(entry, "certificateTemplates"))
                    Edge("observed", "TEMPLATE:" + template.ToUpperInvariant(), id, "PublishedTo", true, false);
            }
            Writer("nodes_" + kind).WriteLine(node.End());
            Objects++;

            if (kind == "users" || kind == "computers" || kind == "groups")
            {
                foreach (string group in Strings(entry, "memberOf")) Edge("memberof", id, group, "MemberOf", false, true);
                Edge("memberof", id, DirectoryIdentity.PrimaryGroupSid(sid, Text(entry, "primaryGroupID")), "MemberOf", false, false);
                if (entry.Attributes.Contains("sIDHistory"))
                {
                    foreach (byte[] historical in entry.Attributes["sIDHistory"].GetValues(typeof(byte[])))
                    {
                        string oldSid = Sid.ToSidStringSafe(historical);
                        if (oldSid == null) throw new InvalidDataException("Invalid SID history on " + dn);
                        Edge("access", id, oldSid, "HasSIDHistory", false, false);
                    }
                }
            }
            if (kind == "computers")
                foreach (string trustee in SecurityDescriptor.AllowedToActPrincipals(Bytes(entry, "msDS-AllowedToActOnBehalfOfOtherIdentity")))
                    Edge("access", trustee, id, "AllowedToAct", false, false);
            if (kind == "computers" || kind == "users")
                foreach (string spn in Strings(entry, "msDS-AllowedToDelegateTo"))
                    Edge("access", id, "SPN:" + spn.ToUpperInvariant(), "AllowedToDelegate", false, true);

            if (kind == "domains" || kind == "ous" || kind == "sites")
            {
                string links = Text(entry, "gPLink");
                if (links != null)
                    foreach (Match link in GpLinks.Matches(links))
                        if ((int.Parse(link.Groups[2].Value, CultureInfo.InvariantCulture) & 1) == 0)
                            Edge("gplink", link.Groups[1].Value.ToUpperInvariant(), id, "GPLink", false, false);
            }
            Edge("contains", DirectoryIdentity.ParentDn(dn), id, "Contains", true, false);
            byte[] descriptor = Bytes(entry, "nTSecurityDescriptor");
            if (descriptor == null) { MissingDescriptors++; return; }
            var parsed = SecurityDescriptor.Parse(descriptor, kind == "domains" ? "domain" : kind == "groups" ? "group" : kind == "users" ? "user" : kind);
            Edge("acl", parsed.OwnerSid, id, "Owns", false, false);
            foreach (AceEdge grant in parsed.Aces)
            {
                if (grant.TrusteeSid == "S-1-3-0" || grant.TrusteeSid == "S-1-3-1" || grant.TrusteeSid == "S-1-5-10") continue;
                Edge(grant.EdgeType == "Enroll" || grant.EdgeType == "AutoEnroll" ? "observed" : "acl",
                    grant.TrusteeSid, id, grant.EdgeType, false, false);
            }
        }

        public void Flush() { foreach (NdjsonWriter writer in writers.Values) writer.Flush(); }
        public void Dispose() { foreach (NdjsonWriter writer in writers.Values) writer.Dispose(); }
    }
}
