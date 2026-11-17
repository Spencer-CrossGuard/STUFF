using System;
using System.Collections;
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
        private readonly string ownerPolicyMode;
        private readonly Func<SearchResultEntry, string, string[]> ranged;
        private readonly Dictionary<string, NdjsonWriter> writers = new Dictionary<string, NdjsonWriter>();
        private readonly JsonRecordWriter node = new JsonRecordWriter();
        private readonly JsonRecordWriter edge = new JsonRecordWriter();
        private readonly List<string> entryErrors = new List<string>();
        private static readonly string[] EmptyStrings = new string[0];
        private static readonly string[] KindNames = {
            null, "ous", "domains", "users", "groups", "computers", "gpos", "sites",
            "trusts", "rootcas", "enterprisecas", "certtemplates", "siteservers", "sitesubnets", "issuancepolicies"
        };
        private static readonly Dictionary<string, int> ClassRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
            { "organizationalUnit", 1 }, { "container", 1 }, { "configuration", 1 }, { "sitesContainer", 1 }, { "builtinDomain", 1 }, { "domainDNS", 2 },
            { "user", 3 }, { "group", 4 }, { "computer", 5 }, { "groupPolicyContainer", 6 },
            { "site", 7 }, { "trustedDomain", 8 }, { "certificationAuthority", 9 },
            { "pKIEnrollmentService", 10 }, { "pKICertificateTemplate", 11 },
            { "server", 12 }, { "subnet", 13 }, { "msPKI-Enterprise-Oid", 14 }
        };
        private static readonly Regex GpLinks = new Regex(
            @"\[LDAP://(cn=\{[0-9a-f-]+\}[^;\]]*);(\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public long Objects { get; private set; }
        public long Edges { get; private set; }
        public long MissingDescriptors { get; private set; }
        public long MissingAccountControl { get; private set; }
        public long Errors { get; private set; }
        public string[] EntryErrorSamples { get { return entryErrors.ToArray(); } }

        public LdapEntryWriter(string directory, string shard, bool compress, string domainId,
            Func<SearchResultEntry, string, string[]> ranged)
            :this(directory,shard,compress,domainId,ranged,"Unknown") { }
        public LdapEntryWriter(string directory, string shard, bool compress, string domainId,
            Func<SearchResultEntry, string, string[]> ranged,string ownerPolicyMode)
        {
            this.directory = directory;
            this.shard = shard;
            this.compress = compress;
            this.domainId = domainId;
            this.ownerPolicyMode=ownerPolicyMode=="Block"||ownerPolicyMode=="Allow"||ownerPolicyMode=="Default"||ownerPolicyMode=="Restricted"?ownerPolicyMode:"Unknown";
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
            if (rank == 9 && entry.DistinguishedName.IndexOf(",CN=AIA,CN=Public Key Services,", StringComparison.OrdinalIgnoreCase) >= 0) return "aiacas";
            if (rank == 12 && (entry.DistinguishedName.IndexOf(",CN=Servers,", StringComparison.OrdinalIgnoreCase) < 0 || entry.DistinguishedName.IndexOf(",CN=Sites,", StringComparison.OrdinalIgnoreCase) < 0)) return null;
            if (rank == 14)
            {
                long flags;
                if (long.TryParse(Text(entry, "flags"), out flags) && (flags & 2) != 0) return "issuancepolicies";
                if (entry.DistinguishedName.StartsWith("CN=OID,CN=Public Key Services,", StringComparison.OrdinalIgnoreCase)) return "ous";
                return null;
            }
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
            if (type == "ManageCA" || type == "ManageCertificates")
                edge.Prop("EvidenceSource", "LDAP nTSecurityDescriptor").Prop("Authority", "DirectoryObject").Prop("ServicePermissionVerified", false);
            if (sourceDn) edge.Prop("SourceIsDn", true);
            if (targetDn) edge.Prop("TargetIsDn", true);
            Writer("edges_" + bucket).WriteLine(edge.End());
            Edges++;
        }

        public void WritePage(IEnumerable entries, HashSet<string> excludedDns)
        {
            foreach (SearchResultEntry entry in entries)
            {
                if (excludedDns != null && excludedDns.Contains(entry.DistinguishedName)) continue;
                try { Write(entry); }
                catch (Exception error)
                {
                    // A range-reader callback can carry PowerShell cancellation
                    // through an exception wrapper. Never turn cancellation into
                    // a recoverable per-object error and continue the page.
                    for (Exception cause = error; cause != null; cause = cause.InnerException)
                        if (cause is OperationCanceledException || cause.GetType().FullName == "System.Management.Automation.PipelineStoppedException")
                            throw;
                    Errors++;
                    if (entryErrors.Count < 10)
                        entryErrors.Add("entry error on '" + entry.DistinguishedName + "': " + error.Message);
                }
            }
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
            bool principal = kind == "users" || kind == "groups" || kind == "computers" || kind == "domains";
            string id = principal ? (sid ?? guid ?? dn) : (guid ?? sid ?? dn);
            string name = Text(entry, "displayName") ?? Text(entry, "name");
            node.Begin().Prop("Id", id).PropS("Sid", sid).PropS("Guid", guid).Prop("Dn", dn).PropS("Name", name)
                .PropArray("ObjectClasses", Strings(entry, "objectClass"));
            long uac;
            bool accountStateKnown = long.TryParse(Text(entry, "userAccountControl"), out uac);
            if (kind == "users" || kind == "computers")
            {
                node.PropS("SamAccountName", Text(entry, "sAMAccountName")).Prop("AccountStateKnown", accountStateKnown);
                if (accountStateKnown)
                    node.Prop("Uac", uac).Prop("Disabled", (uac & 2) != 0)
                        .Prop("UnconstrainedDelegation", (uac & 0x80000) != 0).Prop("ProtocolTransition", (uac & 0x1000000) != 0);
                else
                {
                    MissingAccountControl++;
                    node.PropS("Uac", null).PropS("Disabled", null)
                        .PropS("UnconstrainedDelegation", null).PropS("ProtocolTransition", null);
                }
                if (kind == "computers") node.PropS("Dns", Text(entry, "dNSHostName")).PropS("Os", Text(entry, "operatingSystem"));
                node.PropArray("Spns", Strings(entry, "servicePrincipalName"));
            }
            if (kind == "groups")
            {
                long flags;
                if (long.TryParse(Text(entry, "groupType"), out flags)) node.Prop("SecurityEnabled", (flags & 0x80000000L) != 0);
                // Backlinks can be unreadable even when this group's forward
                // membership list is visible. Index/merge deduplicates both views.
                foreach (string member in Strings(entry, "member"))
                    Edge("memberof", member, id, "MemberOf", true, false);
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
                    .PropArray("ApplicationPolicies", Strings(entry, "msPKI-Certificate-Application-Policy"))
                    .PropS("SchemaVersion", Text(entry, "msPKI-Template-Schema-Version"));
                node.PropArray("IssuancePolicyOids", Strings(entry, "msPKI-Certificate-Policy"));
                foreach (string oid in Strings(entry, "msPKI-Certificate-Policy"))
                    Edge("observed", id, "OID:" + oid, "HasIssuancePolicy", false, true);
                // A stable alias resolves published templates across different scan folders.
                node.PropS("TemplateAlias", Text(entry, "name"));
            }
            if (kind == "enterprisecas")
            {
                node.PropS("Dns", Text(entry, "dNSHostName")).PropArray("PublishedTemplates", Strings(entry, "certificateTemplates"))
                    .Prop("CAServicePermissionsCollected", false);
                foreach (string template in Strings(entry, "certificateTemplates"))
                    Edge("observed", "TEMPLATE:" + template.ToUpperInvariant(), id, "PublishedTo", true, false);
            }
            if (kind == "issuancepolicies")
            {
                node.PropS("PolicyOid", Text(entry, "msPKI-Cert-Template-OID")).PropS("Flags", Text(entry, "flags"))
                    .PropS("LinkedGroupDn", Text(entry, "msDS-OIDToGroupLink"));
                Edge("observed", id, Text(entry, "msDS-OIDToGroupLink"), "OIDGroupLink", false, true);
            }
            if (kind == "siteservers")
            {
                node.PropS("Dns", Text(entry, "dNSHostName")).PropS("ServerReferenceDn", Text(entry, "serverReference"));
                Edge("observed", Text(entry, "serverReference"), id, "HostsSiteServer", true, false);
                Edge("observed", id, DirectoryIdentity.ParentDn(DirectoryIdentity.ParentDn(dn)), "SiteServerOf", false, true);
            }
            if (kind == "sitesubnets")
            {
                node.PropS("SiteDn", Text(entry, "siteObject"));
                Edge("observed", id, Text(entry, "siteObject"), "InSite", false, true);
            }
            if(kind=="gpos"||kind=="ous"||kind=="domains"||kind=="computers")node.Prop("GpoMetadataVersion","1");
            if(kind=="ous"||kind=="domains")
            {
                string som=kind=="domains"?"Domain":"Container";foreach(string cls in Strings(entry,"objectClass"))if(string.Equals(cls,"organizationalUnit",StringComparison.OrdinalIgnoreCase))som="OU";
                node.Prop("GpOptions",Text(entry,"gPOptions")??"0").Prop("GpoSomType",som);
            }
            if(kind=="gpos")
            {
                var policySecurity=GpoSecurity.Analyze(Bytes(entry,"nTSecurityDescriptor"));
                node.PropS("GpoFlags",Text(entry,"flags")).PropS("GpoFunctionalityVersion",Text(entry,"gPCFunctionalityVersion"))
                    .PropS("GpoWmiFilter",Text(entry,"gPCWQLFilter")).PropS("GpoFileSysPath",Text(entry,"gPCFileSysPath"))
                    .PropS("GpoVersionNumber",Text(entry,"versionNumber")).Prop("GpoSecurityKnown",policySecurity.Known)
                    .Prop("GpoSecurityNote",policySecurity.Reason).PropArray("GpoReaders",policySecurity.Readers).PropArray("GpoAppliers",policySecurity.Appliers);
            }
            byte[] descriptor=Bytes(entry,"nTSecurityDescriptor");
            var ownership=OwnershipDescriptor.Analyze(descriptor);bool computerDerived=kind=="computers";
            foreach(string cls in Strings(entry,"objectClass"))if(string.Equals(cls,"msDS-ManagedServiceAccount",StringComparison.OrdinalIgnoreCase)||string.Equals(cls,"msDS-GroupManagedServiceAccount",StringComparison.OrdinalIgnoreCase))computerDerived=true;
            node.Prop("OwnershipMetadataVersion","1").Prop("OwnershipAnalysisKnown",ownership.Known).PropS("OwnerSid",ownership.OwnerSid)
                .Prop("OwnerRightsPresent",ownership.OwnerRightsPresent).Prop("OwnerRightsWriteDacl",ownership.OwnerRightsWriteDacl)
                .Prop("OwnershipWriteDaclDeny",ownership.WriteDaclDeny).Prop("OwnershipComputerDerived",computerDerived)
                .Prop("OwnerPolicyMode",ownerPolicyMode).Prop("OwnershipNote",ownership.Reason);
            if(kind=="domains")node.Prop("OwnerDomainPolicyMode",ownerPolicyMode);
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
            bool gmsa=false;foreach(string cls in Strings(entry,"objectClass"))if(string.Equals(cls,"msDS-GroupManagedServiceAccount",StringComparison.OrdinalIgnoreCase))gmsa=true;
            if(gmsa){foreach(AceEdge grant in SecurityDescriptor.Parse(Bytes(entry,"msDS-GroupMSAMembership"),"gmsa-readers").Aces){
                if(grant.EdgeType!="ReadGMSAPassword")continue;
                edge.Begin().Prop("Source",grant.TrusteeSid).Prop("Target",id).Prop("EdgeType","ReadGMSAPassword").Prop("Derivation","GmsaMembershipDescriptorV1").Prop("EvidenceSource","LDAP msDS-GroupMSAMembership").Prop("EffectiveAccessVerified",false);
                Writer("edges_access").WriteLine(edge.End());Edges++;
            }}
            if (kind == "computers" || kind == "users")
                foreach (string spn in Strings(entry, "msDS-AllowedToDelegateTo"))
                {
                    string host = DirectoryIdentity.SpnHost(spn);
                    if (host != null) Edge("access", id, "SPNHOST:" + host.ToUpperInvariant(), "AllowedToDelegate", false, true);
                }

            if (kind == "domains" || kind == "ous" || kind == "sites")
            {
                string links = Text(entry, "gPLink");
                if (links != null)
                    foreach (Match link in GpLinks.Matches(links))
                    {
                        uint options;
                        if(!uint.TryParse(link.Groups[2].Value,NumberStyles.None,CultureInfo.InvariantCulture,out options))continue;
                        edge.Begin().Prop("Source",link.Groups[1].Value).Prop("Target",id).Prop("EdgeType","GPLink").Prop("SourceIsDn",true)
                            .Prop("GpoLinkMetadataVersion","1").Prop("LinkOptions",options.ToString(CultureInfo.InvariantCulture))
                            .Prop("Enabled",(options&1)==0).Prop("Enforced",(options&2)!=0).Prop("EvidenceSource","LDAP gPLink");
                        Writer((options&1)==0?"edges_gplink":"edges_observed").WriteLine(edge.End());Edges++;
                    }
            }
            Edge("contains", DirectoryIdentity.ParentDn(dn), id, "Contains", true, false);
            if (descriptor == null) { MissingDescriptors++; return; }
            var parsed = SecurityDescriptor.Parse(descriptor, kind == "domains" ? "domain" : kind == "groups" ? "group" : kind == "users" ? "user" : kind);
            if(parsed.ReplicationAnalysisBlocked)Edge("observed",id,id,"ReplicationAnalysisBlocked",false,false);
            Edge("observed",ownership.OwnerSid,id,"OwnsRaw",false,false);
            foreach (AceEdge grant in parsed.Aces)
            {
                if (grant.TrusteeSid == "S-1-3-0" || grant.TrusteeSid == "S-1-3-1" || grant.TrusteeSid == "S-1-3-4" || grant.TrusteeSid == "S-1-5-10" || grant.TrusteeSid == "S-1-5-18" || grant.TrusteeSid == "S-1-5-9") continue;
                if(grant.EdgeType=="WriteOwner"){Edge("observed",grant.TrusteeSid,id,"WriteOwnerRaw",false,false);continue;}
                bool inventoryExtended = grant.EdgeType=="GetChanges" || grant.EdgeType=="GetChangesAll" || (grant.EdgeType == "AllExtendedRights" && kind != "users" && kind != "computers" && kind != "domains") || (grant.EdgeType == "WriteOwner" && parsed.OwnerRightsPresent);
                Edge(inventoryExtended || grant.EdgeType == "Enroll" || grant.EdgeType == "AutoEnroll" || grant.EdgeType == "ManageCA" || grant.EdgeType == "ManageCertificates" ? "observed" : "acl",
                    grant.TrusteeSid, id, grant.EdgeType, false, false);
            }
        }

        public void Flush() { foreach (NdjsonWriter writer in writers.Values) writer.Flush(); }
        public void Dispose() { foreach (NdjsonWriter writer in writers.Values) writer.Dispose(); }
    }
}
