using System;
using System.Collections.Generic;
using System.Globalization;

namespace BH.Native
{
    public sealed partial class GraphIndex
    {
        private readonly HashSet<string> enrichedGroupIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long adcsSeedAttempts;
        private bool adcsSeedLimited;
        private static string AF(string record,string name){return record==null?null:JsonRecordReader.ExtractField(record,name);}
        private static string[] AA(string record,string name){return record==null?new string[0]:new List<string>(JsonRecordReader.StringArray(record,name)).ToArray();}
        private static string Thumb(string value)
        {
            if(value==null||value.Length!=40)return null;
            foreach(char c in value)if(!Uri.IsHexDigit(c))return null;
            return value.ToUpperInvariant();
        }
        private void InitializeAdcsInputs()
        {
            enrichedGroupIds.Clear();
            Execute(@"DROP TABLE IF EXISTS temp.adcs_meta;
                CREATE TEMP TABLE adcs_meta(id TEXT COLLATE NOCASE PRIMARY KEY,kind TEXT,record BLOB,signature TEXT,conflict INTEGER) WITHOUT ROWID;
                CREATE INDEX adcs_kind ON adcs_meta(kind);");
        }
        private void RegisterAdcsNode(string record,string id,string kind,Statement insert,Statement conflict)
        {
            if(kind!="certtemplates"&&kind!="enterprisecas"&&kind!="rootcas"&&kind!="ntauthstores"&&kind!="computers"&&kind!="users"&&kind!="groups"&&kind!="domains")return;
            if(kind=="groups"&&AF(record,"GroupTypeSource")=="ReadOnlyLDAP")enrichedGroupIds.Add(id);
            var signature=new JsonRecordWriter().Begin().Prop("Kind",kind);
            foreach(string field in new[]{"AdcsMetadataVersion","AdcsMetadataSource","DomainSid","DomainName","Disabled","SecurityEnabled","CertThumbprint","HostingComputer","CAServicePermissionsCollected","AdcsRequiresManagerApproval","AdcsEnrolleeSuppliesSubject","AdcsAuthenticationEnabled","SchemaVersion","RequiredSignatures","AdcsExplicitDenyCount","AdcsInheritedDenyCount"})signature.Prop(field,AF(record,field));
            foreach(string field in new[]{"CertChain","CertThumbprints","AdcsServiceAces","AdcsDirectoryAces"})signature.PropArray(field,AA(record,field));
            string key=signature.End();
            conflict.Bind(1,id);conflict.Bind(2,key);conflict.Read();conflict.Reset();
            insert.Bind(1,id);insert.Bind(2,kind);insert.BindRecord(3,key);insert.Bind(4,key);insert.Bind(5,AF(record,"AdcsEvidenceConflict")=="true"?"1":"0");insert.Read();insert.Reset();
        }
        private static bool AdcsTemplateEligible(string record)
        {
            long schema,signatures;
            return AF(record,"AdcsMetadataVersion")=="1"&&AF(record,"AdcsMetadataSource")=="SharpHound"&&
                AF(record,"AdcsRequiresManagerApproval")=="false"&&AF(record,"AdcsEnrolleeSuppliesSubject")=="true"&&AF(record,"AdcsAuthenticationEnabled")=="true"&&
                long.TryParse(AF(record,"SchemaVersion"),out schema)&&schema>=1&&schema<=4&&long.TryParse(AF(record,"RequiredSignatures"),out signatures)&&signatures>=0&&(schema==1||signatures==0);
        }
        private static bool AdcsDenyFree(string record)
        {
            foreach(string field in new[]{"AdcsExplicitDenyCount","AdcsInheritedDenyCount"})
            {string value=AF(record,field);long count;if(value!=null&&(!long.TryParse(value,out count)||count!=0))return false;}
            foreach(string ace in AA(record,"AdcsDirectoryAces")){string access=AF(ace,"AccessType");if(access!=null&&access!="AccessAllowed")return false;}
            foreach(string ace in AA(record,"AdcsServiceAces")){string access=AF(ace,"AccessType");if(access!=null&&access!="AccessAllowed")return false;}
            return true;
        }
        private void AddAdcsGrants(string id,string record,bool service,Statement grant)
        {
            if(!AdcsDenyFree(record))return;
            foreach(string ace in AA(record,service?"AdcsServiceAces":"AdcsDirectoryAces"))
            {
                string right=AF(ace,"RightName"),type=AF(ace,"PrincipalType"),source=AF(ace,"PrincipalSID"),access=AF(ace,"AccessType");
                if(type=="LocalGroup"||type=="LocalUser"||source==null||(access!=null&&access!="AccessAllowed"))continue;
                if(service?right!="Enroll":right!="Enroll"&&right!="GenericAll"&&right!="AllExtendedRights")continue;
                if(type!="User"&&type!="Computer"&&type!="Group")continue;
                if(adcsSeedAttempts++>=250000){adcsSeedLimited=true;return;}
                grant.Bind(1,id);grant.Bind(2,service?"2":"1");grant.Bind(3,source);grant.Bind(4,source);grant.Bind(5,right);grant.Read();grant.Reset();
            }
        }
        private void DeriveAdcsEsc1()
        {
            adcsSeedAttempts=0;adcsSeedLimited=false;
            InitializeAdcsDiagnostics();
            Execute(@"DELETE FROM edges WHERE type COLLATE NOCASE='ADCSESC1';
                DROP TABLE IF EXISTS temp.adcs_reach;DROP TABLE IF EXISTS temp.adcs_pairs;DROP TABLE IF EXISTS temp.adcs_members;
                CREATE TEMP TABLE adcs_reach(object TEXT COLLATE NOCASE,permission INTEGER,principal TEXT COLLATE NOCASE,next_id TEXT,grant_source TEXT,right_name TEXT,depth INTEGER,
                    PRIMARY KEY(object,permission,principal)) WITHOUT ROWID;
                CREATE INDEX adcs_reach_depth ON adcs_reach(depth);
                CREATE TEMP TABLE adcs_pairs(ca TEXT COLLATE NOCASE,template TEXT COLLATE NOCASE,domain TEXT COLLATE NOCASE,root TEXT,ntauth TEXT,host TEXT,thumb TEXT,
                    PRIMARY KEY(ca,template,domain)) WITHOUT ROWID;
                CREATE TEMP TABLE adcs_members(source TEXT COLLATE NOCASE,target TEXT COLLATE NOCASE,PRIMARY KEY(source,target)) WITHOUT ROWID;
                CREATE INDEX adcs_member_target ON adcs_members(target,source);
                INSERT OR IGNORE INTO adcs_members SELECT e.source,e.target FROM edges e
                    JOIN adcs_meta g ON g.id=e.target AND g.kind='groups' AND g.conflict=0
                    JOIN nodes n ON n.id=g.id AND n.security_enabled='true'
                    JOIN adcs_meta p ON p.id=e.source AND p.kind IN ('users','computers','groups') AND p.conflict=0
                    WHERE e.bucket='memberof' AND e.type='MemberOf' AND e.source_dn=0 AND e.target_dn=0 AND e.source<>e.target;");
            long caCount=0,pairCount=0;
            using(var ca=new Statement(this,"SELECT id,record FROM adcs_meta WHERE kind='enterprisecas' AND conflict=0 ORDER BY id"))
            using(var host=new Statement(this,"SELECT record FROM adcs_meta WHERE id=?1 AND kind='computers' AND conflict=0"))
            using(var roots=new Statement(this,"SELECT id,record FROM adcs_meta WHERE kind='rootcas' AND conflict=0 ORDER BY id"))
            using(var stores=new Statement(this,"SELECT a.id,a.record FROM adcs_meta a JOIN nodes d ON d.id=?1 AND d.kind='domains' WHERE a.kind='ntauthstores' AND a.conflict=0 ORDER BY a.id"))
            using(var templates=new Statement(this,@"SELECT a.id,a.record FROM edges e JOIN adcs_meta a ON a.id=e.source AND a.kind='certtemplates' AND a.conflict=0
                WHERE e.target=?1 AND e.type='PublishedTo' AND e.bucket='observed' AND e.source_dn=0 AND e.target_dn=0 ORDER BY a.id"))
            using(var pair=new Statement(this,"INSERT OR IGNORE INTO adcs_pairs VALUES(?1,?2,?3,?4,?5,?6,?7)"))
            using(var grant=new Statement(this,"INSERT OR IGNORE INTO adcs_reach VALUES(?1,?2,?3,NULL,?4,?5,0)"))
            {
                while(ca.Read())
                {
                    caCount++;string caId=ca.Text(0),record=ca.Record(1),domain=AF(record,"DomainSid"),hosting=AF(record,"HostingComputer"),thumb=Thumb(AF(record,"CertThumbprint"));
                    string[] chain=AA(record,"CertChain");
                    var caProblems=AdcsCaProblems(record);if(caProblems.Count>0){foreach(var problem in caProblems)AdcsStop(caId,"",problem[0],problem[1]);continue;}
                    host.Bind(1,hosting);bool eligible=false;
                    if(host.Read()){string h=host.Record(0);eligible=AF(h,"Disabled")=="false"&&string.Equals(AF(h,"DomainSid"),domain,StringComparison.OrdinalIgnoreCase);}
                    host.Reset();if(!eligible){AdcsStop(caId,hosting??"","CAHostNotQualified","The exact CA host is missing, disabled, has unknown enabled state, or does not match the captured CA domain.");continue;}
                    bool rootMatched=false,ntauthMatched=false,published=false,templateEligible=false;
                    AddAdcsGrants(caId,record,true,grant);
                    while(roots.Read())
                    {
                        string rootRecord=roots.Record(1),targetDomain=AF(rootRecord,"DomainSid");string[] rootChain=AA(rootRecord,"CertChain");
                        if(AF(rootRecord,"AdcsMetadataVersion")!="1"||AF(rootRecord,"AdcsMetadataSource")!="SharpHound"||Thumb(AF(rootRecord,"CertThumbprint"))!=thumb||rootChain.Length!=1||Thumb(rootChain[0])!=thumb||targetDomain==null)continue;
                        rootMatched=true;stores.Bind(1,targetDomain);
                        while(stores.Read())
                        {
                            string storeRecord=stores.Record(1);if(AF(storeRecord,"AdcsMetadataVersion")!="1"||AF(storeRecord,"AdcsMetadataSource")!="SharpHound"||!string.Equals(AF(storeRecord,"DomainSid"),targetDomain,StringComparison.OrdinalIgnoreCase))continue;
                            bool trusted=false;foreach(string trustedThumb in AA(storeRecord,"CertThumbprints"))if(Thumb(trustedThumb)==thumb)trusted=true;
                            if(!trusted)continue;ntauthMatched=true;
                            templates.Bind(1,caId);
                            while(templates.Read())
                            {
                                string template=templates.Text(0),templateRecord=templates.Record(1);published=true;var templateProblems=AdcsTemplateProblems(templateRecord);if(templateProblems.Count>0){foreach(var problem in templateProblems)AdcsStop(template,caId,problem[0],problem[1]);continue;}templateEligible=true;
                                AddAdcsGrants(template,templateRecord,false,grant);
                                pair.Bind(1,caId);pair.Bind(2,template);pair.Bind(3,targetDomain);pair.Bind(4,roots.Text(0));pair.Bind(5,stores.Text(0));pair.Bind(6,hosting);pair.Bind(7,thumb);pair.Read();pair.Reset();
                            }
                            templates.Reset();
                        }
                        stores.Reset();
                    }
                    roots.Reset();
                    if(!rootMatched)AdcsStop(caId,"","RootTrustNotEstablished","No matching supported direct-root certificate record with a target-domain association was supplied.");
                    else if(!ntauthMatched)AdcsStop(caId,"","NTAuthTrustNotEstablished","No matching CA thumbprint in an NTAuth store associated with the same collected target domain was established.");
                    else if(!published)AdcsStop(caId,"","PublicationNotResolved","No published, resolved certificate-template record was supplied for this CA.");
                    else if(!templateEligible)AdcsStop(caId,"","NoEligibleTemplate","Published templates failed or lack supported ESC1 template prerequisites; inspect their individual reasons.");
                }
            }
            pairCount=Scalar("SELECT count(*) FROM adcs_pairs");
            // Well-known authenticated principals apply implicitly to known enabled
            // accounts in the publishing CA's domain. Do not infer anonymous or
            // cross-forest authentication, and never treat a distribution group as a token.
            using(var special=new Statement(this,@"SELECT r.object,r.permission,r.principal,r.grant_source,r.right_name,c.record FROM adcs_reach r
                JOIN adcs_meta c ON c.id=r.object WHERE r.depth=0"))
            using(var accounts=new Statement(this,"SELECT id,record FROM adcs_meta WHERE kind IN ('users','computers') AND conflict=0"))
            using(var insert=new Statement(this,"INSERT OR IGNORE INTO adcs_reach VALUES(?1,?2,?3,NULL,?4,?5,0)"))
            {
                var specials=new List<string[]>();while(special.Read())specials.Add(new[]{special.Text(0),special.Text(1),special.Text(2),special.Text(3),special.Text(4),AF(special.Record(5),"DomainSid"),AF(special.Record(5),"DomainName")});
                foreach(var s in specials)
                {
                    if(adcsSeedLimited)break;
                    string domain=s[5];if(domain==null)continue;
                    if(s[2]!="S-1-1-0"&&s[2]!="S-1-5-11"&&s[2]!=domain+"-S-1-1-0"&&s[2]!=domain+"-S-1-5-11"&&!string.Equals(s[2],s[6]+"-S-1-1-0",StringComparison.OrdinalIgnoreCase)&&!string.Equals(s[2],s[6]+"-S-1-5-11",StringComparison.OrdinalIgnoreCase))continue;
                    while(accounts.Read())
                    {
                        string a=accounts.Record(1);if(AF(a,"Disabled")!="false"||!string.Equals(AF(a,"DomainSid"),domain,StringComparison.OrdinalIgnoreCase))continue;
                        if(adcsSeedAttempts++>=250000){adcsSeedLimited=true;break;}
                        insert.Bind(1,s[0]);insert.Bind(2,s[1]);insert.Bind(3,accounts.Text(0));insert.Bind(4,s[3]);insert.Bind(5,s[4]);insert.Read();insert.Reset();
                    }
                    accounts.Reset();
                }
            }
            bool limited=adcsSeedLimited;
            for(long depth=0;!limited&&Scalar("SELECT count(*) FROM adcs_reach WHERE depth="+depth)>0;depth++)
            {
                long remaining=250000-Scalar("SELECT count(*) FROM adcs_reach");
                if(remaining<=0||depth>=1000){limited=true;break;}
                long newPrincipals=Scalar(@"SELECT count(*) FROM (SELECT DISTINCT r.object,r.permission,m.source FROM adcs_reach r
                    JOIN adcs_members m ON m.target=r.principal WHERE r.depth="+depth+@" AND NOT EXISTS(SELECT 1 FROM adcs_reach x
                    WHERE x.object=r.object AND x.permission=r.permission AND x.principal=m.source) LIMIT "+(remaining+1)+")");
                if(newPrincipals>remaining){limited=true;break;}
                Execute(@"INSERT OR IGNORE INTO adcs_reach SELECT r.object,r.permission,m.source,r.principal,r.grant_source,r.right_name,r.depth+1
                    FROM adcs_reach r JOIN adcs_members m ON m.target=r.principal WHERE r.depth="+depth+@" AND NOT EXISTS(SELECT 1 FROM adcs_reach x WHERE x.object=r.object AND x.permission=r.permission AND x.principal=m.source) ORDER BY r.object,r.permission,m.source;");
            }

            if(!limited)
            using(var candidates=new Statement(this,@"SELECT p.domain,t.principal,p.template,p.ca,p.root,p.ntauth,p.host,p.thumb,t.grant_source,c.grant_source,t.right_name,c.right_name
                FROM adcs_pairs p JOIN adcs_reach t ON t.object=p.template AND t.permission=1
                JOIN adcs_reach c ON c.object=p.ca AND c.permission=2 AND c.principal=t.principal
                JOIN adcs_meta m ON m.id=t.principal AND m.conflict=0
                JOIN nodes n ON n.id=m.id AND n.kind IN ('users','computers','groups') AND (n.kind<>'groups' OR n.security_enabled='true')
                ORDER BY p.domain,t.principal,p.ca,p.template"))
            using(var write=new Statement(this,"INSERT OR IGNORE INTO edges VALUES(?1,'access',?2,'ADCSESC1',0,0,?3)"))
            {
                var evidence=new JsonRecordWriter();
                while(candidates.Read())
                {
                    string domain=candidates.Text(0),principal=candidates.Text(1),template=candidates.Text(2),ca=candidates.Text(3);
                    evidence.Begin().Prop("Source",principal).Prop("Target",domain).Prop("EdgeType","ADCSESC1").Prop("Derivation","AdcsEsc1DirectRootV1")
                        .Prop("Assessment",enrichedGroupIds.Count>0?"Conditional candidate":"Supported candidate").Prop("PostExportGroupClassification",enrichedGroupIds.Count>0).Prop("EffectiveAccessVerified",false).Prop("CertificateIssuanceVerified",false)
                        .Prop("Template",template).Prop("EnterpriseCA",ca).Prop("RootCA",candidates.Text(4)).Prop("NTAuthStore",candidates.Text(5)).Prop("HostingComputer",candidates.Text(6)).Prop("CertThumbprint",candidates.Text(7))
                        .Prop("TemplateGrant",candidates.Text(8)).Prop("CAServiceGrant",candidates.Text(9)).Prop("TemplateRight",candidates.Text(10)).Prop("CAServiceRight",candidates.Text(11))
                        .PropArray("TemplateMembership",AdcsWitness(template,1,principal)).PropArray("CAServiceMembership",AdcsWitness(ca,2,principal))
                        .Prop("EvidenceNote","Imported SharpHound summary; same principal, publishing CA, direct-root thumbprint trust and enabled same-domain host. Full-token effective access, issuance and current authentication enforcement require validation. Membership witnesses show at most 64 objects."+(enrichedGroupIds.Count>0?" Post-export read-only LDAP group classification exists in this dataset; exported permissions and memberships were not revalidated.":""));
                    write.Bind(1,domain);write.Bind(2,principal);write.Bind(3,evidence.End());write.Read();write.Reset();
                }
            }
            using(var rows=new Statement(this,"SELECT n.id,n.record FROM adcs_meta m JOIN nodes n ON n.id=m.id WHERE m.conflict<>0 AND n.record IS NOT NULL"))
            using(var update=new Statement(this,"UPDATE nodes SET record=?1 WHERE id=?2"))
            {while(rows.Read()){update.BindRecord(1,GpoConflictRecord(rows.Record(1),"AdcsEvidenceConflict"));update.Bind(2,rows.Text(0));update.Read();update.Reset();}}
            string note="ESC1 imported direct-root scope: "+caCount+" non-conflicting CAs, "+pairCount+" eligible CA/template/domain combinations; "+Scalar("SELECT count(*) FROM edges WHERE type='ADCSESC1'")+" projected relationships. Intermediate chains, cross-domain CA hosts, LDAP-only enrollment evidence and unsupported metadata suppress inference. Effective access and certificate issuance are unverified."+(limited?" Enrollment seed or membership closure limit reached; all ESC1 projections suppressed.":"");
            StoreAdcsEnrollmentDiagnostics(limited);
            string classification=GroupTypeClassificationText();
            using(var entry=new Statement(this,"INSERT OR REPLACE INTO derivation_meta VALUES('group_classification',?1)")){entry.Bind(1,classification);entry.Read();}
            note+=" "+classification;
            long unknownGroups=Scalar("SELECT count(*) FROM nodes WHERE kind='groups' AND security_enabled IS NULL");
            if(unknownGroups>0)note+=" "+unknownGroups+" collected groups have unknown security-enabled state and cannot extend enrollment membership witnesses.";
            using(var state=new Statement(this,"INSERT OR REPLACE INTO derivation_meta VALUES('adcs',?1)")){state.Bind(1,note);state.Read();}
        }
        private string[] AdcsWitness(string target,int permission,string principal)
        {
            var result=new List<string>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using(var next=new Statement(this,"SELECT next_id,grant_source FROM adcs_reach WHERE object=?1 AND permission=?2 AND principal=?3"))
            {
                while(principal!=null&&result.Count<64&&visited.Add(principal))
                {
                    result.Add(principal);next.Bind(1,target);next.Bind(2,permission.ToString(CultureInfo.InvariantCulture));next.Bind(3,principal);
                    string id=null,grant=null;if(next.Read()){id=next.Text(0);grant=next.Text(1);}next.Reset();
                    if(id==null){if(grant!=null&&!visited.Contains(grant)&&result.Count<64)result.Add(grant);break;}principal=id;
                }
            }
            return result.ToArray();
        }
        public string AdcsScopeNote()
        {
            if(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='derivation_meta'")==0)return "Legacy index: AD CS prerequisites were not evaluated";
            using(var state=new Statement(this,"SELECT value FROM derivation_meta WHERE key='adcs'")){return state.Read()?state.Text(0):"Legacy index: AD CS prerequisites were not evaluated";}
        }
    }
}
