using System;
using System.Collections.Generic;

namespace BH.Native
{
    public sealed partial class GraphIndex
    {
        private void InitializeAdcsDiagnostics()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS adcs_diagnostics(object TEXT COLLATE NOCASE,related TEXT COLLATE NOCASE,code TEXT,reason TEXT,
                PRIMARY KEY(object,related,code)) WITHOUT ROWID;DELETE FROM adcs_diagnostics;");
        }
        private void AdcsStop(string id,string related,string code,string reason)
        {
            using(var write=new Statement(this,"INSERT OR IGNORE INTO adcs_diagnostics VALUES(?1,?2,?3,?4)"))
            {write.Bind(1,id);write.Bind(2,related??"");write.Bind(3,code);write.Bind(4,reason);write.Read();}
        }
        private static List<string[]> AdcsCaProblems(string record)
        {
            var result=new List<string[]>();
            if(AF(record,"AdcsMetadataVersion")!="1"||AF(record,"AdcsMetadataSource")!="SharpHound")result.Add(new[]{"UnsupportedCAEvidence","Supported imported CA metadata was not collected; directory inventory alone cannot establish CA-service prerequisites."});
            if(AF(record,"CAServicePermissionsCollected")!="true")result.Add(new[]{"CAServicePermissionsMissing","A successful CA-service permission collection is missing. Directory-object ACLs cannot substitute for service Enroll evidence."});
            if(!AdcsDenyFree(record))result.Add(new[]{"CADenyContext","Reported deny or unsupported ACE context requires full effective-token review."});
            string thumb=Thumb(AF(record,"CertThumbprint"));string[] chain=AA(record,"CertChain");
            if(thumb==null)result.Add(new[]{"CACertificateIdentityMissing","The CA certificate thumbprint is missing or malformed."});
            if(chain.Length!=1||thumb==null||Thumb(chain[0])!=thumb)result.Add(new[]{"UnsupportedCAChain","A matching single-entry direct-root chain was not established. Intermediate chains and missing chain metadata remain unsupported."});
            if(string.IsNullOrEmpty(AF(record,"DomainSid")))result.Add(new[]{"CADomainMissing","The publishing CA domain SID was not captured."});
            if(string.IsNullOrEmpty(AF(record,"HostingComputer")))result.Add(new[]{"CAHostMissing","The exact CA HostingComputer identity was not captured."});
            return result;
        }
        private static List<string[]> AdcsTemplateProblems(string record)
        {
            var result=new List<string[]>();
            if(AF(record,"AdcsMetadataVersion")!="1"||AF(record,"AdcsMetadataSource")!="SharpHound")result.Add(new[]{"UnsupportedTemplateEvidence","Explicit supported imported template metadata is missing."});
            if(AF(record,"AdcsRequiresManagerApproval")!="false")result.Add(new[]{"TemplateApproval","Manager approval is required or its state is unknown."});
            if(AF(record,"AdcsEnrolleeSuppliesSubject")!="true")result.Add(new[]{"TemplateSubject","Requester-supplied subject naming is not enabled or is unknown."});
            if(AF(record,"AdcsAuthenticationEnabled")!="true")result.Add(new[]{"TemplateAuthentication","Authentication purpose is not enabled or is unknown."});
            long schema,signatures;bool validSchema=long.TryParse(AF(record,"SchemaVersion"),out schema)&&schema>=1&&schema<=4;
            if(!validSchema)result.Add(new[]{"TemplateSchema","A supported schema version 1–4 was not established."});
            if(!long.TryParse(AF(record,"RequiredSignatures"),out signatures)||signatures<0||(validSchema&&schema>1&&signatures>0))result.Add(new[]{"TemplateSignatures","The signature requirement is unknown, malformed, or requires signatures for a schema above 1."});
            if(!AdcsDenyFree(record))result.Add(new[]{"TemplateDenyContext","Reported deny or unsupported ACE context requires effective-token review."});
            return result;
        }
        private void StoreAdcsEnrollmentDiagnostics(bool limited)
        {
            using(var pairs=new Statement(this,@"SELECT p.ca,p.template,
                EXISTS(SELECT 1 FROM adcs_reach WHERE object=p.template AND permission=1),
                EXISTS(SELECT 1 FROM adcs_reach WHERE object=p.ca AND permission=2),
                EXISTS(SELECT 1 FROM adcs_reach a JOIN adcs_reach b ON b.principal=a.principal AND b.object=p.ca AND b.permission=2
                    JOIN adcs_meta m ON m.id=a.principal AND m.conflict=0 JOIN nodes n ON n.id=m.id AND n.kind IN ('users','computers','groups')
                        AND (n.kind<>'groups' OR n.security_enabled='true') WHERE a.object=p.template AND a.permission=1)
                FROM adcs_pairs p ORDER BY p.ca,p.template"))
            {
                while(pairs.Read())
                {
                    if(limited)AdcsStop(pairs.Text(0),pairs.Text(1),"MembershipWorkLimit","Enrollment seed or membership work reached its limit; all ESC1 projections were suppressed.");
                    else if(pairs.Text(2)!="1")AdcsStop(pairs.Text(0),pairs.Text(1),"TemplateEnrollmentNotEstablished","No supported template enrollment grant was established for this CA/template combination.");
                    else if(pairs.Text(3)!="1")AdcsStop(pairs.Text(0),pairs.Text(1),"CAServiceEnrollmentNotEstablished","No supported CA-service Enroll grant was established for this combination.");
                    else if(pairs.Text(4)!="1")AdcsStop(pairs.Text(0),pairs.Text(1),"TokenIntersectionNotEstablished","No common known principal was established across the template and CA-service enrollment branches. Unknown group types or missing memberships can prevent expansion.");
                }
            }
            using(var groups=new Statement(this,@"SELECT DISTINCT n.id,n.record FROM adcs_reach r JOIN nodes n ON n.id=r.principal
                WHERE n.kind='groups' AND n.security_enabled IS NULL ORDER BY n.id"))
            {while(groups.Read()){string status=AF(groups.Record(1),"GroupTypeEnrichmentStatus");AdcsStop(groups.Text(0),"","GroupClassificationUnknown","Security-enabled group state is unknown; this group cannot extend derived enrollment membership witnesses."+(status==null?"":" Enrichment status: "+status+"."));}}
            using(var conflicts=new Statement(this,"SELECT id FROM adcs_meta WHERE conflict<>0 ORDER BY id"))
            {while(conflicts.Read())AdcsStop(conflicts.Text(0),"","ConflictingEvidence","Conflicting prerequisite snapshots prevent this object from participating in supported ESC1 inference.");}
        }
        private string GroupTypeClassificationText()
        {
            return enrichedGroupIds.Count==0?"No post-export LDAP group classification was used.":
                enrichedGroupIds.Count+" group identities contain post-export read-only LDAP classification. Exported grants and memberships were not revalidated; ESC1 projections are conditional mixed-time candidates.";
        }
        public string GroupTypeScopeNote()
        {
            if(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='derivation_meta'")==0)return "Group classification provenance is unavailable in this legacy index.";
            using(var rows=new Statement(this,"SELECT value FROM derivation_meta WHERE key='group_classification'")){return rows.Read()?rows.Text(0):"Group classification provenance is unavailable in this legacy index.";}
        }
        public long AdcsPrerequisiteCount()
        {return Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='adcs_diagnostics'")==0?0:Scalar("SELECT count(*) FROM adcs_diagnostics");}
        // Rows describe unestablished candidate prerequisites, not an exhaustive
        // proof of why an arbitrary source/target query has no possible route.
        public List<string[]> AdcsPrerequisiteRows(int limit,string objectId)
        {
            if(limit<1||limit>5000)throw new ArgumentOutOfRangeException("limit");
            var result=new List<string[]>();if(AdcsPrerequisiteCount()==0)return result;
            using(var rows=new Statement(this,@"SELECT d.object,d.related,d.code,d.reason,COALESCE(a.name,d.object),COALESCE(b.name,d.related)
                FROM adcs_diagnostics d LEFT JOIN nodes a ON a.id=d.object LEFT JOIN nodes b ON b.id=d.related
                WHERE ?1='' OR d.object=?1 COLLATE NOCASE OR d.related=?1 COLLATE NOCASE
                ORDER BY CASE WHEN d.code='GroupClassificationUnknown' THEN 1 ELSE 0 END,d.object,d.related,d.code LIMIT "+limit))
            {rows.Bind(1,objectId??"");while(rows.Read())result.Add(new[]{rows.Text(0),rows.Text(1),rows.Text(2),rows.Text(3),rows.Text(4),rows.Text(5)});}
            return result;
        }
        private void AppendGroupClassificationGap(List<string[]> rows,string id,string record)
        {
            foreach(var row in rows)if(row[2]=="GroupClassificationUnknown")return;
            using(var group=new Statement(this,"SELECT name FROM nodes WHERE id=?1 AND kind='groups' AND security_enabled IS NULL"))
            {
                group.Bind(1,id);if(!group.Read())return;
                string status=record==null?null:AF(record,"GroupTypeEnrichmentStatus");
                rows.Add(new[]{id,"","GroupClassificationUnknown","The security-enabled state is unknown. Derived rules requiring a security-group token cannot expand this membership."+(status==null?"":" Enrichment status: "+status+"."),group.Text(0)??id,""});
            }
        }
    }
}
