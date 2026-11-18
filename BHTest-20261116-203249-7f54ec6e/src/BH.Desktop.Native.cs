using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BH.Native
{
    // A native Windows interface: no browser, WebView runtime, HTTP bridge, or sockets.
    public static class DesktopViewer
    {
        public const string BuildId="/*__VIEWER_BUILD__*/";
        internal static readonly Dictionary<string,string[]> Guide=CreateGuide();
        internal static readonly Dictionary<string,string[]> ContextGuide=CreateContextGuide();
        private static Dictionary<string,string[]> CreateContextGuide(){
            var guide=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
            /*__DESKTOP_CONTEXT_GUIDE__*/
            return guide;
        }
        private static Dictionary<string,string[]> CreateGuide()
        {
            var guide=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
            /*__DESKTOP_GUIDE__*/
            return guide;
        }
        [STAThread]
        public static void Run(string indexPath)
        {
            Application.EnableVisualStyles();
            using(var window=new DesktopWindow(indexPath))window.ShowDialog();
        }
        internal static Dictionary<string,object> Map(object value){return (Dictionary<string,object>)value;}
        internal static string Text(Dictionary<string,object> row,string key){object value;return row.TryGetValue(key,out value)?Convert.ToString(value,CultureInfo.InvariantCulture):"";}
        internal static long Number(Dictionary<string,object> row,string key){return Convert.ToInt64(row[key],CultureInfo.InvariantCulture);}
        internal static bool Flag(Dictionary<string,object> row,string key){return row.ContainsKey(key)&&Convert.ToBoolean(row[key],CultureInfo.InvariantCulture);}
        internal static List<object> Rows(Dictionary<string,object> row){return (List<object>)row["Rows"];}
        internal static string ChangeProcess="Before changing it: Confirm the business owner and dependent services. Save the exact current ACL, membership or delegation, including inheritance.\r\n\r\nValidation: Rescan the same scope with comparable permissions and endpoint coverage. Check this chain and alternative paths, and test legitimate service/support workflows.\r\n\r\nRollback: Restore only the documented original setting through the approved change process if required access breaks. Reassess narrower delegation.";
        internal static string Explain(Dictionary<string,object> edge)
        {
            string type=Text(edge,"ty");string[] h;
            if(!Guide.TryGetValue(type,out h))h=new string[]{type,"A recorded relationship in the supplied index.","The dataset contains this relationship.","Its effect depends on the object types and source data.","Verify the relationship's source documentation and prerequisites.","Do not assume that an unfamiliar relationship establishes effective access.","Inspect the original evidence before choosing a change."};
            string[] context;string targetKind=Text(edge,"tt");if(targetKind.Length==0)targetKind=Text(edge,"tk");
            ContextGuide.TryGetValue(type+"|"+targetKind,out context);
            string derived="",evidence=Text(edge,"Evidence");
            if(evidence.Length>0 && JsonRecordReader.ExtractField(evidence,"Derivation")=="DCSyncMembershipIntersectionV1")
            {
                var a=new List<string>(JsonRecordReader.StringArray(evidence,"GetChangesMembership"));
                var b=new List<string>(JsonRecordReader.StringArray(evidence,"GetChangesAllMembership"));
                derived="\r\n\r\nHow this relationship was derived\r\nBoth replication permissions reach this principal on the same domain through recorded security-group memberships. Effective access has not been verified.\r\nGetChanges membership witness: "+string.Join(" → ",a.ToArray())+"\r\nGetChangesAll membership witness: "+string.Join(" → ",b.ToArray());
                if(JsonRecordReader.ExtractField(evidence,"WitnessLimited")=="true")derived+="\r\nWitness preview limited to 64 objects; inspect the full membership graph.";
            }
            string contextual=context==null?"":"\r\n\r\nPrerequisites to verify\r\n"+context[1]+"\r\n\r\nChange impact\r\n"+context[4];
            if(type=="GPLink"&&evidence.Length>0&&JsonRecordReader.ExtractField(evidence,"GpoLinkMetadataVersion")=="1")
                derived="\r\n\r\nRecorded link options\r\nEnabled: "+JsonRecordReader.ExtractField(evidence,"Enabled")+"; enforced: "+JsonRecordReader.ExtractField(evidence,"Enforced")+"; options: "+JsonRecordReader.ExtractField(evidence,"LinkOptions")+". Link configuration does not prove policy application."+(JsonRecordReader.ExtractField(evidence,"GpoLinkEvidenceConflict")!=null?" Conflicting snapshots suppress automatic scope inference.":"");
            if(evidence.Length>0&&JsonRecordReader.ExtractField(evidence,"Derivation")=="GpoComputerScopeV1")
            {
                derived="\r\n\r\nHow this scope was derived\r\nLink root: "+JsonRecordReader.ExtractField(evidence,"LinkRoot")+"; enforced: "+JsonRecordReader.ExtractField(evidence,"Enforced");
                foreach(string key in new[]{"ContainmentChain","ReadMembership","ApplyMembership"})derived+="\r\n"+key+": "+string.Join(" → ",new List<string>(JsonRecordReader.StringArray(evidence,key)).ToArray());
                derived+="\r\nComputer-policy scope candidate only. SYSVOL, policy contents, client processing and resultant policy were not verified. Everyone/Authenticated Users are implicit token identities; other token witnesses use recorded security-enabled memberships.";
            }
            if(evidence.Length>0&&JsonRecordReader.ExtractField(evidence,"Derivation")=="AdcsEsc1DirectRootV1")
            {
                derived="\r\n\r\nHow this relationship was derived\r\n"+JsonRecordReader.ExtractField(evidence,"EvidenceNote");if(JsonRecordReader.ExtractField(evidence,"PostExportGroupClassification")=="true")derived+="\r\nConditional mixed-time candidate: group classification was read after export; original permissions and memberships were not revalidated.";
                foreach(string field in new[]{"Template","EnterpriseCA","RootCA","NTAuthStore","HostingComputer","CertThumbprint","TemplateGrant","CAServiceGrant"})derived+="\r\n"+field+": "+JsonRecordReader.ExtractField(evidence,field);
                foreach(string field in new[]{"TemplateMembership","CAServiceMembership"})derived+="\r\n"+field+": "+string.Join(" → ",new List<string>(JsonRecordReader.StringArray(evidence,field)).ToArray());
            }
            string ownershipDerivation=evidence.Length==0?null:JsonRecordReader.ExtractField(evidence,"Derivation");
            if(ownershipDerivation=="OwnershipControlV1"||ownershipDerivation=="OwnershipLegacyV1")
            {
                string assessment=JsonRecordReader.ExtractField(evidence,"Assessment"),reason=JsonRecordReader.ExtractField(evidence,"Reason");
                derived="\r\n\r\nOwnership assessment\r\n"+assessment+": "+reason+"\r\nPolicy: "+(JsonRecordReader.ExtractField(evidence,"OwnerPolicyMode")??"not captured")+"; "+(JsonRecordReader.ExtractField(evidence,"PolicySource")??"legacy collection")+". Effective access and server enforcement were not verified.";
                if(assessment=="Suppressed projection")
                {h=(string[])h.Clone();context=null;contextual="";h[0]="Ownership observation: projection suppressed";h[1]="Raw ownership evidence is retained, but this row does not establish control.";h[2]=reason;h[3]="This observation is excluded from control-path traversal. Suppression is not proof that every possible permission is denied.";h[6]="Validate the evidence and intended delegation before considering changes.";}
            }
            return h[0]+"\r\n"+Text(edge,"sn")+" → "+Text(edge,"tn")+"\r\n"+type+derived+"\r\n\r\nWhat this means\r\n"+(context==null?h[1]:context[0])+"\r\n\r\nWhy it is shown\r\n"+h[2]+"\r\n\r\nPossible impact\r\n"+h[3]+contextual+"\r\n\r\nWhat to verify\r\n"+h[4]+"\r\n\r\nLimits\r\n"+h[5]+"\r\n\r\nRecommended change to review\r\n"+(context==null?h[6]:context[2])+(context==null?"":"\r\n\r\nHow to verify this change\r\n"+context[3])+"\r\n\r\n"+ChangeProcess+"\r\n\r\nReference\r\nBloodHound relationship documentation: https://bloodhound.specterops.io/resources/edges\r\nBHTest support and derivation limits: PATH-PARITY.md, OWNERSHIP.md, GPO-SCOPE.md and ADCS-SCOPE.md.";
        }
    }

    public sealed class DesktopWindow : Form
    {
        private static readonly Color Bg=Color.FromArgb(12,18,32),PanelColor=Color.FromArgb(19,30,48),Line=Color.FromArgb(43,58,81),Ink=Color.FromArgb(237,243,252),Muted=Color.FromArgb(164,180,202);
        private readonly string indexPath;
        private readonly FileStream fileLock;
        private volatile GraphIndex activeQuery;
        private bool busy,closing;
        private volatile bool cancelled;
        private Font helperHeadingFont;
        private readonly List<Control> commands=new List<Control>();
        private Label status,summary,findNote,objectNote,relationshipNote,pathNote,identity;
        private Button cancel,findPrevious,findNext,objectPrevious,objectNext,relationshipNext,continuePath;
        private Button toggleDetails,objectBack,objectForward;
        private SplitContainer workspaceSplit;
        private readonly ToolTip tips=new ToolTip();
        private readonly List<string> objectHistory=new List<string>();
        private int objectHistoryPosition=-1;
        private TextBox findingSearch,objectSearch,sourceId,targetId,viaOne,viaTwo,properties;
        private ComboBox direction,visitLimit,routeMode;
        private string[] allowedPathTypes;
        private Button pathFilters;
        private DataGridView findings,objects,relationships,steps;
        private RichTextBox helper;
        private TabControl tabs;
        private PathCanvas graph;
        private Dictionary<string,object> selectedObject,path;
        private Dictionary<string,object> relationshipCursor=ViewerJson.Map("target","","bucket","","source","","type","");
        private readonly DesktopPager findingPage=new DesktopPager(true),objectPage=new DesktopPager(false);
        private bool pathIsSaved;
        private List<object> targetGraphEdges;
        private FlowLayoutPanel connectionLegend;private Label connectionTitle;
        private readonly List<object> connectionLegendEdges=new List<object>();
        private long pathOffset,totalPathHops;
        private string pathOriginId,pathOriginName;
        private string analysisStatus="";
        private string gpoScopeCoverage="Coverage summary is loading.";

        public DesktopWindow(string path)
        {
            indexPath=Path.GetFullPath(path);
            fileLock=new FileStream(indexPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            try
            {
                var header=new byte[20];if(fileLock.Read(header,0,20)!=20||Encoding.ASCII.GetString(header,0,16)!="SQLite format 3\0")throw new IOException("Select a BHTest SQLite graph index.");
                if(header[18]!=1||header[19]!=1||File.Exists(indexPath+"-wal")||File.Exists(indexPath+"-journal"))throw new IOException("Select a completed standalone graph.sqlite, without a WAL or recovery journal.");
                using(var db=new GraphIndex(indexPath,false)){db.ViewerBudget();db.Node("__desktop_schema_check__");}
                Text="BHTest — Desktop graph viewer";Name="DesktopViewer";Size=new Size(1450,940);MinimumSize=new Size(1040,720);StartPosition=FormStartPosition.CenterScreen;
                BackColor=Bg;ForeColor=Ink;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Font;
                Build();Shown+=delegate{FitTabWidths();LoadOverview();};FormClosing+=CloseRequested;
            }
            catch{fileLock.Dispose();throw;}
        }
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing){fileLock.Dispose();tips.Dispose();if(helperHeadingFont!=null)helperHeadingFont.Dispose();}}
        private static TableLayoutPanel Stack(int rows)
        {var p=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=rows,BackColor=PanelColor,Padding=new Padding(10)};p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));return p;}
        private static void Fixed(TableLayoutPanel p,int height){p.RowStyles.Add(new RowStyle(SizeType.Absolute,height));}
        private static void Fill(TableLayoutPanel p){p.RowStyles.Add(new RowStyle(SizeType.Percent,100));}
        private static Label Note(string name,string text){return new Label{Name=name,Text=text,Dock=DockStyle.Fill,ForeColor=Muted,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};}
        private static TextBox Input(string name,int width){return new TextBox{Name=name,Width=width,BackColor=Bg,ForeColor=Ink,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(4,6,4,4)};}
        private static FlowLayoutPanel Bar(){return new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,AutoScroll=false,BackColor=PanelColor,Margin=new Padding(0)};}
        private Button Button(string name,string text,Action run)
        {
            var b=new Button{Name=name,Text=text.Replace("&","&&"),AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Height=33,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(25,39,60),ForeColor=Ink,Margin=new Padding(4),Padding=new Padding(6,2,6,2)};b.FlatAppearance.BorderColor=Line;
            b.AccessibleName=text;tips.SetToolTip(b,text);
            if(name=="ZoomIn"||name=="ZoomOut"){b.AccessibleName=name=="ZoomIn"?"Zoom in":"Zoom out";tips.SetToolTip(b,b.AccessibleName);}
            b.Click+=delegate{if(!busy)try{run();}catch(Exception e){SetStatus(e.Message,true);}};commands.Add(b);return b;
        }
        private static ComboBox Choice(string name,params string[] values)
        {var c=new ComboBox{Name=name,DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Bg,ForeColor=Ink,Width=145,Margin=new Padding(4,6,4,4)};c.Items.AddRange(values);c.SelectedIndex=0;return c;}
        private static DataGridView Grid(string name,params string[] columns)
        {
            var g=new DataGridView{Name=name,Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Bg,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,GridColor=Line,RowTemplate={Height=32}};
            g.DefaultCellStyle.BackColor=PanelColor;g.DefaultCellStyle.ForeColor=Ink;g.DefaultCellStyle.SelectionBackColor=Color.FromArgb(38,65,102);g.DefaultCellStyle.SelectionForeColor=Color.White;
            g.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(25,39,60);g.ColumnHeadersDefaultCellStyle.ForeColor=Muted;g.ColumnHeadersHeight=35;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor=Color.FromArgb(25,39,60);g.ColumnHeadersDefaultCellStyle.SelectionForeColor=Muted;
            g.DefaultCellStyle.Padding=new Padding(6,0,6,0);g.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(15,24,40);
            foreach(string column in columns){g.Columns.Add(column,column);g.Columns[column].SortMode=DataGridViewColumnSortMode.NotSortable;}return g;
        }
        private static Dictionary<string,object> Selected(DataGridView grid){return grid.CurrentRow==null?null:grid.CurrentRow.Tag as Dictionary<string,object>;}
        private static void AddRow(DataGridView grid,Dictionary<string,object> tag,params object[] values)
        {
            var row=grid.Rows[grid.Rows.Add(values)];row.Tag=tag;
            if(grid.Columns.Contains("Ownership"))
            {
                string evidence=DesktopViewer.Text(tag,"Evidence"),assessment=evidence.Length==0?null:JsonRecordReader.ExtractField(evidence,"Assessment");
                if(assessment!=null)
                {grid.Columns["Ownership"].HeaderText="Assessment";grid.Columns["Ownership"].Visible=true;row.Cells["Ownership"].Value=assessment.StartsWith("Conditional",StringComparison.Ordinal)?"Conditional":JsonRecordReader.ExtractField(evidence,"Derivation")=="AdcsEsc1DirectRootV1"?"Candidate":assessment=="Suppressed projection"?"Suppressed":"Supported";row.Cells["Ownership"].ToolTipText=assessment+": "+(JsonRecordReader.ExtractField(evidence,"Reason")??JsonRecordReader.ExtractField(evidence,"EvidenceNote"));}
            }
        }
        private TabPage Page(string title,Control body){var p=new TabPage(title){BackColor=PanelColor,Padding=new Padding(2)};p.Controls.Add(body);tabs.TabPages.Add(p);FitTabWidths();return p;}
        private void FitTabWidths(){if(tabs!=null&&tabs.TabPages.Count>0){int width=Math.Max(80,(workspaceSplit.Panel1.ClientSize.Width-8)/tabs.TabPages.Count);if(tabs.ItemSize.Width!=width)tabs.ItemSize=new Size(width,28);}}
        private void Build()
        {
            var outer=Stack(3);Fixed(outer,98);Fill(outer);Fixed(outer,42);outer.BackColor=Bg;Controls.Add(outer);
            var heading=Stack(3);Fixed(heading,39);Fixed(heading,25);Fill(heading);heading.Padding=new Padding(6,0,6,0);heading.BackColor=Bg;
            var titleBar=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0),BackColor=Bg};titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,285));
            titleBar.Controls.Add(new Label{Text="BH  /  Directory graph workspace",Dock=DockStyle.Fill,Font=new Font(Font.FontFamily,18,FontStyle.Bold),ForeColor=Ink},0,0);
            toggleDetails=Button("ToggleDetails","Hide details",ToggleDetails);toggleDetails.Dock=DockStyle.Fill;var headerActions=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));var helpButton=Button("HelpLibrary","Help & glossary",delegate{ShowHelpLibrary("");});helpButton.Dock=DockStyle.Fill;headerActions.Controls.Add(helpButton,0,0);headerActions.Controls.Add(toggleDetails,1,0);titleBar.Controls.Add(headerActions,1,0);heading.Controls.Add(titleBar,0,0);
            summary=Note("Summary","Read-only desktop viewer · No listening ports · "+Path.GetFileName(indexPath));heading.Controls.Add(summary,0,1);outer.Controls.Add(heading,0,0);
            string snapshotFolder=Path.GetDirectoryName(indexPath),snapshotName=Path.GetFileName(snapshotFolder);if(string.Equals(snapshotName,"report",StringComparison.OrdinalIgnoreCase))snapshotName=Path.GetFileName(Path.GetDirectoryName(snapshotFolder));
            var snapshot=Note("Snapshot","Snapshot: "+snapshotName+"  ·  Ctrl+F to search  ·  Enter to run a search");tips.SetToolTip(snapshot,indexPath);heading.Controls.Add(snapshot,0,2);
            workspaceSplit=new SplitContainer{Name="WorkspaceSplit",Dock=DockStyle.Fill,BackColor=Bg,Size=new Size(1390,730),Panel1MinSize=650,Panel2MinSize=280,SplitterWidth=9};workspaceSplit.SplitterDistance=1010;outer.Controls.Add(workspaceSplit,0,1);
            tabs=new TabControl{Name="WorkspaceTabs",Dock=DockStyle.Fill,DrawMode=TabDrawMode.OwnerDrawFixed,SizeMode=TabSizeMode.Fixed,ItemSize=new Size(210,28),Padding=new Point(12,6)};
            workspaceSplit.Panel1.SizeChanged+=delegate{FitTabWidths();};
            tabs.DrawItem+=delegate(object sender,DrawItemEventArgs e){bool selected=e.Index==tabs.SelectedIndex;using(var brush=new SolidBrush(selected?Color.FromArgb(31,50,77):PanelColor))e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,tabs.TabPages[e.Index].Text,Font,e.Bounds,selected?Ink:Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);if(selected)using(var pen=new Pen(Color.FromArgb(121,179,255),3))e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-2,e.Bounds.Right,e.Bounds.Bottom-2);};workspaceSplit.Panel1.Controls.Add(tabs);
            var helpLayout=Stack(4);Fixed(helpLayout,30);Fixed(helpLayout,44);Fill(helpLayout);Fixed(helpLayout,80);workspaceSplit.Panel2.Controls.Add(helpLayout);
            helpLayout.Controls.Add(new Label{Text="DETAILS & EVIDENCE",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(121,179,255),Font=new Font(Font,FontStyle.Bold)},0,0);
            var glossary=Choice("Glossary","Choose a relationship…");glossary.Dock=DockStyle.Fill;var types=new List<string>(DesktopViewer.Guide.Keys);types.Sort(StringComparer.OrdinalIgnoreCase);glossary.Items.AddRange(types.ToArray());
            glossary.SelectedIndexChanged+=delegate{if(glossary.SelectedIndex>0)Explain(ViewerJson.Map("ty",glossary.Text,"sn","Source principal","tn","Target object"));};helpLayout.Controls.Add(glossary,0,1);
            helper=new RichTextBox{Name="Helper",Dock=DockStyle.Fill,ReadOnly=true,BackColor=PanelColor,ForeColor=Ink,BorderStyle=BorderStyle.None,Font=Font,DetectUrls=false};helpLayout.Controls.Add(helper,0,2);
            var coverage=new LinkLabel{Name="Coverage",Text="Recorded paths need validation.\r\nView collection and path limits.",Dock=DockStyle.Fill,LinkColor=Muted,ActiveLinkColor=Ink,VisitedLinkColor=Muted,LinkBehavior=LinkBehavior.HoverUnderline,TextAlign=ContentAlignment.MiddleLeft};
            coverage.LinkClicked+=delegate{RevealDetails();SetHelp("Collection and path limits",gpoScopeCoverage+"\r\n\r\nOwnership summaries count relationships, not end-to-end paths. Supported is a modeled candidate, conditional records need additional validation, and suppressed records remain inventory. A current native scan is needed for new ownership context.\r\n\r\nGPO scope candidates use collected directory metadata. SYSVOL edit access, policy contents, client processing and resultant policy are unverified. Relevant denies, unsupported ACLs, WMI filters, unknown or conflicting metadata and bounded-work limits can suppress paths.\r\n\r\nFor new GPO scope analysis, collect linked GPOs, their containers and computers, and security-enabled group memberships with the current native collector. Older collections cannot supply the newly added fields.\r\n\r\nRead the original HTML report for scan times, input scope and broader collection limitations. A missing path is not proof that no route exists.");};helpLayout.Controls.Add(coverage,0,3);
            var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,130));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));status=Note("Status","Ready");cancel=new Button{Name="CancelQuery",Text="Cancel query",Dock=DockStyle.Fill,Enabled=false,FlatStyle=FlatStyle.Flat,ForeColor=Ink};cancel.Click+=delegate{cancelled=true;if(activeQuery!=null)activeQuery.ViewerCancel();};footer.Controls.Add(cancel,0,0);footer.Controls.Add(status,1,0);outer.Controls.Add(footer,0,2);
            BuildFindings();BuildObjects();BuildPaths();SetHelp("Explore your directory","Start with a saved finding to review a path, or search for an object and choose it as the source or target.\r\n\r\nSelect a graph connection or numbered path step to see its meaning, supporting evidence and recommended checks.\r\n\r\nHide details for more graph space. Drag the divider above the step table to resize the graph. Use Back and Forward while inspecting related objects.\r\n\r\nThis viewer reads the selected snapshot locally. It does not change the directory.");
            ApplyHelpTips(this);
            KeyPreview=true;KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.F1){Control active=FocusedHelpControl(this);var topic=active==properties?DesktopHelpCatalog.ForField(properties.SelectedText.Trim('"',':',' ')):null;if(topic==null&&active!=null)topic=DesktopHelpCatalog.ForControl(active.Name);ShowHelpLibrary(topic==null?"start":topic.Key);e.SuppressKeyPress=true;}else if(e.Control&&e.KeyCode==Keys.F){(tabs.SelectedIndex==0?findingSearch:tabs.SelectedIndex==1?objectSearch:sourceId).Focus();e.SuppressKeyPress=true;}else if(e.KeyCode==Keys.Escape&&busy){cancel.PerformClick();e.SuppressKeyPress=true;}};
        }
        private void ToggleDetails(){workspaceSplit.Panel2Collapsed=!workspaceSplit.Panel2Collapsed;toggleDetails.Text=workspaceSplit.Panel2Collapsed?"Show details":"Hide details";if(path!=null)graph.Fit();}
        private void RevealDetails(){if(workspaceSplit.Panel2Collapsed){workspaceSplit.Panel2Collapsed=false;toggleDetails.Text="Hide details";}}
        private void BuildFindings()
        {
            var p=Stack(4);Fixed(p,44);Fixed(p,42);Fill(p);Fixed(p,44);var bar=Bar();findingSearch=Input("FindingSearch",280);
            commands.Add(findingSearch);bar.Controls.AddRange(new Control[]{findingSearch,Button("SearchFindings","Search candidates",delegate{findingPage.Reset(findingSearch.Text);LoadPage(findingPage);}),Button("ResetFindings","Clear search",delegate{findingSearch.Clear();findingPage.Reset("");LoadPage(findingPage);})});p.Controls.Add(bar,0,0);
            findingSearch.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter&&!busy){findingPage.Reset(findingSearch.Text);LoadPage(findingPage);e.SuppressKeyPress=true;}};
            findNote=Note("FindingNote","Generate a Deep report to save findings. All saved candidates can be paged here.");p.Controls.Add(findNote,0,1);
            findings=Grid("Findings","Priority","Source","Target","Hops","Score");findings.Columns["Priority"].FillWeight=45;findings.Columns["Hops"].FillWeight=20;findings.Columns["Score"].FillWeight=20;findings.Columns["Score"].ToolTipText="Review ranking, not a measurement of exploitability.";p.Controls.Add(findings,0,2);
            var actions=Bar();findPrevious=Button("FindingPrevious","Previous",delegate{findingPage.Previous();LoadPage(findingPage);});findNext=Button("FindingNext","Next",delegate{findingPage.Next();LoadPage(findingPage);});
            actions.Controls.AddRange(new Control[]{findPrevious,findNext,Button("OpenFinding","Explain path & fixes",OpenFinding),Button("FindingObject","Inspect source",delegate{var f=Selected(findings);if(f!=null)Inspect(DesktopViewer.Text(DesktopViewer.Map(f["Source"]),"i"));})});p.Controls.Add(actions,0,3);findings.CellDoubleClick+=delegate{if(!busy)OpenFinding();};Page("Saved findings",p);
        }
        private void BuildObjects()
        {
            var p=Stack(4);Fixed(p,44);Fixed(p,38);p.RowStyles.Add(new RowStyle(SizeType.Percent,40));p.RowStyles.Add(new RowStyle(SizeType.Percent,60));
            var bar=Bar();objectSearch=Input("ObjectSearch",220);objectPrevious=Button("ObjectPrevious","Previous",delegate{objectPage.Previous();LoadPage(objectPage);});objectNext=Button("ObjectNext","Next",delegate{objectPage.Next();LoadPage(objectPage);});
            commands.Add(objectSearch);bar.Controls.AddRange(new Control[]{objectSearch,Button("SearchObjects","Search index",delegate{objectPage.Reset(objectSearch.Text);LoadPage(objectPage);}),objectPrevious,objectNext,Button("ResetObjects","Clear",delegate{objectSearch.Clear();objectPage.Reset("");LoadPage(objectPage);})});p.Controls.Add(bar,0,0);
            objectSearch.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter&&!busy){objectPage.Reset(objectSearch.Text);LoadPage(objectPage);e.SuppressKeyPress=true;}};
            objectNote=Note("ObjectNote","Search all object names and IDs. Empty windows have explicit continuation.");p.Controls.Add(objectNote,0,1);
            var list=Stack(2);Fill(list);Fixed(list,42);list.Padding=new Padding(0);objects=Grid("Objects","Name","Type","Identifier");list.Controls.Add(objects,0,0);
            var actions=Bar();objectBack=Button("ObjectBack","Back",delegate{Inspect(objectHistory[objectHistoryPosition-1],objectHistoryPosition-1);});objectForward=Button("ObjectForward","Forward",delegate{Inspect(objectHistory[objectHistoryPosition+1],objectHistoryPosition+1);});actions.Controls.AddRange(new Control[]{Button("InspectObject","Inspect",InspectSelected),Button("ObjectSource","Use as source",delegate{UseNode(Selected(objects),true);}),Button("ObjectTarget","Use as target",delegate{UseNode(Selected(objects),false);}),objectBack,objectForward});list.Controls.Add(actions,0,1);p.Controls.Add(list,0,2);objects.CellDoubleClick+=delegate{if(!busy)InspectSelected();};
            var detail=Stack(3);Fixed(detail,30);Fixed(detail,43);Fill(detail);identity=Note("ObjectIdentity","Select an object to inspect its properties and relationships.");detail.Controls.Add(identity,0,0);
            var rb=Bar();direction=Choice("Direction","Incoming","Outgoing");commands.Add(direction);direction.SelectedIndexChanged+=delegate{if(!busy&&selectedObject!=null){ResetRelationships();LoadRelationships();}};
            relationshipNext=Button("RelationshipNext","Next relationships",delegate{LoadRelationships();});rb.Controls.AddRange(new Control[]{direction,Button("LoadRelationships","Load relationships",delegate{ResetRelationships();LoadRelationships();}),relationshipNext});detail.Controls.Add(rb,0,1);
            var dt=new TabControl{Dock=DockStyle.Fill};var prop=new TabPage("Collected properties");properties=Input("Properties",200);properties.Multiline=true;properties.ReadOnly=true;properties.ScrollBars=ScrollBars.Both;properties.WordWrap=false;properties.Dock=DockStyle.Fill;properties.Font=new Font("Consolas",10);var propLayout=Stack(2);Fixed(propLayout,38);Fill(propLayout);propLayout.Padding=new Padding(0);var propHelp=Button("PropertyHelp","Explain a property (F1)",delegate{var topic=DesktopHelpCatalog.ForField(properties.SelectedText.Trim('"',':',' '));ShowHelpLibrary(topic==null?"Object properties":topic.Key);});propHelp.Dock=DockStyle.Fill;propLayout.Controls.Add(propHelp,0,0);propLayout.Controls.Add(properties,0,1);prop.Controls.Add(propLayout);dt.TabPages.Add(prop);
            var rel=new TabPage("Relationships");var rp=Stack(3);Fixed(rp,34);Fill(rp);Fixed(rp,42);relationshipNote=Note("RelationshipNote","");rp.Controls.Add(relationshipNote,0,0);relationships=Grid("Relationships","From","Relationship","To","Ownership");relationships.Columns["Ownership"].Visible=false;rp.Controls.Add(relationships,0,1);
            var ra=Bar();ra.Controls.AddRange(new Control[]{Button("ExplainRelationship","Explain & fixes",delegate{var e=Selected(relationships);if(e!=null)Explain(e);}),Button("GraphRelationship","Show in graph",delegate{var e=Selected(relationships);if(e!=null)ShowSingleEdge(e);}),Button("InspectOther","Inspect other object",delegate{var e=Selected(relationships);if(e!=null)Inspect(DesktopViewer.Text(e,direction.SelectedIndex==0?"s":"t"));})});rp.Controls.Add(ra,0,2);rel.Controls.Add(rp);dt.TabPages.Add(rel);detail.Controls.Add(dt,0,2);p.Controls.Add(detail,0,3);Page("Objects & relationships",p);
        }
        private void BuildPaths()
        {
            var p=Stack(6);Fixed(p,37);Fixed(p,37);Fixed(p,37);Fixed(p,44);Fixed(p,60);Fill(p);
            var via=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=new Padding(0)};
            via.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));via.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));via.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,65));via.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            viaOne=Input("ViaOne",250);viaTwo=Input("ViaTwo",250);viaOne.Dock=DockStyle.Fill;viaTwo.Dock=DockStyle.Fill;
            via.Controls.Add(Note("","Via 1"),0,0);via.Controls.Add(viaOne,1,0);via.Controls.Add(Note("","Via 2"),2,0);via.Controls.Add(viaTwo,3,0);p.Controls.Add(via,0,1);
            tips.SetToolTip(viaOne,"Optional first waypoint. Search Source → Via 1 → Via 2 → Target; blank waypoints are skipped.");tips.SetToolTip(viaTwo,"Optional second waypoint. Each consecutive leg uses all shortest routes with the selected relationship filters.");
            var s=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0)};s.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));s.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));s.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,160));s.Controls.Add(Note("","Source"),0,0);sourceId=Input("SourceId",640);sourceId.Dock=DockStyle.Fill;s.Controls.Add(sourceId,1,0);pathFilters=Button("PathFilters","Relationships: all",ChoosePathFilters);pathFilters.AutoSize=false;pathFilters.Margin=new Padding(4,2,4,2);pathFilters.Dock=DockStyle.Fill;s.Controls.Add(pathFilters,2,0);p.Controls.Add(s,0,0);tips.SetToolTip(sourceId,"Object name or ID. Leave empty to show incoming routes to the target.");
            var t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=new Padding(0)};t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,175));t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,85));t.Controls.Add(Note("","Target"),0,0);targetId=Input("TargetId",640);targetId.Dock=DockStyle.Fill;t.Controls.Add(targetId,1,0);routeMode=Choice("RouteMode","Target: shortest","Target: all connections");routeMode.Dock=DockStyle.Fill;t.Controls.Add(routeMode,2,0);var targetRoutes=Button("TargetRoutes","Routes",delegate{QueryTarget(targetId.Text.Trim());});targetRoutes.AutoSize=false;targetRoutes.Margin=new Padding(4,2,4,2);targetRoutes.Dock=DockStyle.Fill;tips.SetToolTip(targetRoutes,"Show routes to target, independently of source and waypoint fields.");t.Controls.Add(targetRoutes,3,0);commands.Add(routeMode);tips.SetToolTip(routeMode,"Shortest routes omits cyclic detours. All connections retains the full reverse-reachable subgraph. Applies to target-only searches.");p.Controls.Add(t,0,2);tips.SetToolTip(targetId,"Object name, ID, or DA for Domain Admins. Ambiguous names require an exact ID.");
            var actions=Bar();visitLimit=Choice("VisitLimit","50000","100000","250000");visitLimit.Width=95;actions.Controls.AddRange(new Control[]{Button("FindPath","Find path",delegate{QueryPath(sourceId.Text.Trim(),targetId.Text.Trim());}),Button("SwapEnds","Swap ends",delegate{string first=sourceId.Text;sourceId.Text=targetId.Text;targetId.Text=first;first=viaOne.Text;viaOne.Text=viaTwo.Text;viaTwo.Text=first;SetStatus("Source and target swapped. Choose Find path to search this direction.",false);}),NoteInline("Visit budget",90),visitLimit});
            commands.Add(sourceId);commands.Add(targetId);commands.Add(viaOne);commands.Add(viaTwo);commands.Add(visitLimit);
            KeyEventHandler pathEnter=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter&&!busy){QueryPath(sourceId.Text.Trim(),targetId.Text.Trim());e.SuppressKeyPress=true;}};sourceId.KeyDown+=pathEnter;targetId.KeyDown+=pathEnter;viaOne.KeyDown+=pathEnter;viaTwo.KeyDown+=pathEnter;
            continuePath=Button("ContinuePath","Continue path",Continue);continuePath.Enabled=false;actions.Controls.Add(continuePath);actions.Controls.Add(Button("ExportPlan","Export plan",ExportPlan));p.Controls.Add(actions,0,3);
            pathNote=Note("PathNote","Enter names or IDs; DA selects Domain Admins. Leave source empty for incoming routes, including alternative edges. Target graph: 2,000 edges; visit/time limits are reported.");p.Controls.Add(pathNote,0,4);
            var pathSplit=new SplitContainer{Name="PathSplit",Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,Size=new Size(900,500),Panel1MinSize=220,Panel2MinSize=80,SplitterWidth=8};pathSplit.SplitterDistance=300;p.Controls.Add(pathSplit,0,5);
            bool graphSplitInitialized=false;tabs.SelectedIndexChanged+=delegate{if(tabs.SelectedIndex==2)BeginInvoke(new Action(delegate{if(!graphSplitInitialized){int available=pathSplit.ClientSize.Height-pathSplit.SplitterWidth;pathSplit.SplitterDistance=Math.Max(pathSplit.Panel1MinSize,Math.Min(available-pathSplit.Panel2MinSize,(int)(available*.68)));graphSplitInitialized=true;}graph.Fit();}));};
            var graphLayout=Stack(4);Fixed(graphLayout,24);Fixed(graphLayout,38);Fill(graphLayout);Fixed(graphLayout,50);graphLayout.Padding=new Padding(0);
            connectionTitle=Note("ConnectionTitle","Select a connection to list every relationship here.");graphLayout.Controls.Add(connectionTitle,0,0);
            connectionLegend=new FlowLayoutPanel{Name="ConnectionLegend",Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true,BackColor=PanelColor,Margin=new Padding(0)};graphLayout.Controls.Add(connectionLegend,0,1);
            graph=new PathCanvas{Name="PathGraph",Dock=DockStyle.Fill};graph.NodeSelected+=delegate(Dictionary<string,object> node){SetConnectionLegend(new List<object>());NodeHelp(node);};graph.EdgeSelected+=SelectGraphRelationship;graph.ConnectionSelected+=ShowConnection;graph.RouteSelected+=ShowRoute;graphLayout.Controls.Add(graph,0,2);
            var tools=Bar();tools.Controls.AddRange(new Control[]{Button("ZoomOut","−",delegate{graph.Zoom(1/1.3f);}),Button("ZoomIn","+",delegate{graph.Zoom(1.3f);}),Button("FitGraph","Fit",delegate{graph.Fit();}),Button("ReadPath","Read",delegate{graph.FocusNode(0);}),Button("PreviousNode","Previous",delegate{graph.FocusNode(graph.SelectedIndex-1);}),Button("NextNode","Next",delegate{graph.FocusNode(graph.SelectedIndex+1);}),Button("InspectGraphNode","Inspect",delegate{if(graph.SelectedNode!=null)Inspect(DesktopViewer.Text(graph.SelectedNode,"i"));})});graphLayout.Controls.Add(tools,0,3);pathSplit.Panel1.Controls.Add(graphLayout);
            tools.Controls.Add(Button("AllGraphRelationships","All relationships",delegate{if(targetGraphEdges!=null){ShowTargetRows(targetGraphEdges);graph.Highlight(null);SetConnectionLegend(new List<object>());SetHelp("All target relationships","Showing every relationship retained in this target graph. Click a connection to inspect only that object pair. Existing administrator relationships are included as context, not automatically new escalation findings.");}}));tools.Controls.Add(Button("PreviousRoute","Prev route",delegate{graph.NextRoute(-1);}));tools.Controls.Add(Button("NextRoute","Next route",delegate{graph.NextRoute(1);}));
            steps=Grid("PathSteps","Step","From","Relationship","To","Ownership");steps.Columns["Ownership"].Visible=false;steps.Columns["Ownership"].FillWeight=65;steps.Columns["Step"].FillWeight=18;steps.Columns["Step"].MinimumWidth=48;steps.CellEnter+=delegate(object sender,DataGridViewCellEventArgs args){if(args.RowIndex<0)return;var e=steps.Rows[args.RowIndex].Tag as Dictionary<string,object>;if(e!=null){UpdateConnectionLegend(e);Explain(e);graph.Highlight(e);}};pathSplit.Panel2.Controls.Add(steps);p.Dock=DockStyle.Top;p.MinimumSize=new Size(0,560);var pathPage=Page("Paths & graph",p);pathPage.AutoScroll=true;pathPage.SizeChanged+=delegate{p.Height=Math.Max(560,pathPage.ClientSize.Height-pathPage.Padding.Vertical);};p.Height=Math.Max(560,pathPage.ClientSize.Height-pathPage.Padding.Vertical);
        }
        private void ChoosePathFilters()
        {
            using(var dialog=new Form{Text="Path relationship filters",ClientSize=new Size(410,490),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false,ShowInTaskbar=false})
            {
                var panel=Stack(3);Fixed(panel,52);Fill(panel);Fixed(panel,42);dialog.Controls.Add(panel);
                panel.Controls.Add(Note("","Choose relationships for custom path and target searches. Saved findings keep their original analysis rules."),0,0);
                var choices=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false,AccessibleName="Allowed path relationships"};
                var selected=allowedPathTypes==null?null:new HashSet<string>(allowedPathTypes,StringComparer.OrdinalIgnoreCase);
                foreach(string type in GraphRelationships.FilterTypes())choices.Items.Add(type,selected==null||selected.Contains(type));panel.Controls.Add(choices,0,1);
                var buttons=Bar();var all=new Button{Text="Select all",AutoSize=true};all.Click+=delegate{for(int i=0;i<choices.Items.Count;i++)choices.SetItemChecked(i,true);};
                var none=new Button{Text="Clear all",AutoSize=true};none.Click+=delegate{for(int i=0;i<choices.Items.Count;i++)choices.SetItemChecked(i,false);};
                var apply=new Button{Text="Apply",DialogResult=DialogResult.OK};var cancelFilter=new Button{Text="Cancel",DialogResult=DialogResult.Cancel};
                buttons.Controls.AddRange(new Control[]{all,none,apply,cancelFilter});panel.Controls.Add(buttons,0,2);dialog.AcceptButton=apply;dialog.CancelButton=cancelFilter;
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                var types=new List<string>();foreach(object item in choices.CheckedItems)types.Add((string)item);
                allowedPathTypes=types.Count==choices.Items.Count?null:types.ToArray();
                pathFilters.Text=allowedPathTypes==null?"Relationships: all":"Relationships: "+allowedPathTypes.Length;
                path=null;targetGraphEdges=null;steps.Rows.Clear();steps.Columns["Ownership"].Visible=false;graph.SetEdges(new List<object>());SetConnectionLegend(new List<object>());UpdateButtons();
                pathNote.Text="Relationship filters changed. Run Find path or Routes to target again.";
            }
        }
        private static Label NoteInline(string text,int width){var n=Note("",text);n.Dock=DockStyle.None;n.Width=width;n.Height=34;return n;}
        private void SetStatus(string message,bool error){status.Text=message;status.ForeColor=error?Color.FromArgb(255,135,150):Muted;}
        private void SetHelp(string title,string body){helper.Text=title+"\r\n\r\n"+body;helper.Select(0,title.Length);helper.SelectionColor=Color.FromArgb(121,179,255);if(helperHeadingFont==null)helperHeadingFont=new Font(Font,FontStyle.Bold);helper.SelectionFont=helperHeadingFont;helper.Select(0,0);helper.ScrollToCaret();}
        private void Explain(Dictionary<string,object> edge){RevealDetails();SetHelp("Relationship explanation & recommended fixes",DesktopViewer.Explain(edge));}
        private static Control FocusedHelpControl(Control root)
        {if(root.Focused)return root;foreach(Control child in root.Controls)if(child.ContainsFocus){var focused=FocusedHelpControl(child);if(focused!=null)return focused;}return null;}
        private void ShowHelpLibrary(string query){using(var window=new HelpLibraryWindow(query))window.ShowDialog(this);}
        private void ApplyHelpTips(Control parent)
        {
            foreach(Control control in parent.Controls)
            {
                var topic=DesktopHelpCatalog.ForControl(control.Name);
                if(topic!=null){string existing=tips.GetToolTip(control);tips.SetToolTip(control,(string.IsNullOrEmpty(existing)||existing==control.Text||existing==control.AccessibleName?topic.Summary:existing)+"\r\nF1 opens detailed help.");control.AccessibleDescription=topic.Summary;}
                ApplyHelpTips(control);
            }
        }
        private static string PrerequisiteText(List<string[]> rows)
        {
            var b=new StringBuilder("Known limitations of supported candidate combinations; not exhaustive proof about an arbitrary source/target path. Successful combinations can coexist with suppressed ones.\r\n");
            foreach(var row in rows){b.AppendLine(row[4]+" · "+row[2]);b.AppendLine(row[3]);if(row[1].Length>0)b.AppendLine("Related: "+row[5]);b.AppendLine();}
            return b.ToString();
        }
        private void NodeHelp(Dictionary<string,object> node)
        {
            var topic=DesktopHelpCatalog.ForKind(DesktopViewer.Text(node,"t"));
            SetHelp(DesktopViewer.Text(node,"n"),DesktopViewer.Text(node,"t")+"\r\n"+DesktopViewer.Text(node,"i")+"\r\n\r\n"+(topic==null?"No type-specific interpretation is available. Inspect the original object record and collection notes.":DesktopHelpCatalog.Describe(topic))+"\r\n\r\nThis object appears in the recorded data; its presence alone does not flag it as unsafe. Inspect retained properties and relationships, and select a relationship for its exact evidence. Select a property name and press F1, or use Help & glossary, for field definitions."+(node.ContainsKey("PathPrerequisites")&&((List<string[]>)node["PathPrerequisites"]).Count>0?"\r\n\r\nKnown prerequisite limitations\r\n"+PrerequisiteText((List<string[]>)node["PathPrerequisites"]):""));
        }
        private void Run(Func<GraphIndex,object> query,Action<object> complete)
        {
            if(busy)return;busy=true;cancelled=false;foreach(var command in commands)command.Enabled=false;cancel.Enabled=true;SetStatus("Reading index… The interface remains responsive. Cancel to stop this query.",false);
            // Marshal explicitly: PowerShell hosts may install their own synchronization context.
            ThreadPool.QueueUserWorkItem(delegate(object unused){object result=null;Exception failure=null;
                try{using(var db=new GraphIndex(indexPath,false)){activeQuery=db;try{db.ViewerBudget();if(cancelled)db.ViewerCancel();result=query(db);}finally{activeQuery=null;}}}catch(Exception error){failure=error;}
                if(IsDisposed)return;
                BeginInvoke(new Action(delegate{busy=false;cancel.Enabled=false;foreach(var command in commands)command.Enabled=true;
                    try{if(closing){Close();return;}if(cancelled){SetStatus("Query cancelled; the previous view remains. The search is incomplete.",true);return;}if(failure!=null){SetStatus(failure.Message.IndexOf("SQLite error 9",StringComparison.Ordinal)>=0?"Query time limit reached. Results are incomplete; narrow the query.":failure.Message,true);return;}complete(result);SetStatus("Ready · Read only · No listening ports",false);}catch(Exception error){SetStatus(error.Message,true);}finally{if(!IsDisposed)UpdateButtons();}
                }));
            });
        }
        private void CloseRequested(object sender,FormClosingEventArgs e){if(busy){e.Cancel=true;closing=true;cancelled=true;if(activeQuery!=null)activeQuery.ViewerCancel();SetStatus("Stopping the active query before closing…",false);}}
        private void UpdateButtons(){findPrevious.Enabled=!busy&&findingPage.History.Count>0;findNext.Enabled=!busy&&findingPage.More;objectPrevious.Enabled=!busy&&objectPage.History.Count>0;objectNext.Enabled=!busy&&objectPage.More;objectBack.Enabled=!busy&&objectHistoryPosition>0;objectForward.Enabled=!busy&&objectHistoryPosition>=0&&objectHistoryPosition<objectHistory.Count-1;continuePath.Enabled=!busy&&path!=null&&DesktopViewer.Flag(path,"PathLimited");relationshipNext.Enabled=!busy&&selectedObject!=null&&relationshipCursor.ContainsKey("more")&&Convert.ToBoolean(relationshipCursor["more"]);}
        private void LoadOverview(){Run(delegate(GraphIndex db){return db.ViewerSummary();},delegate(object result){var r=DesktopViewer.Map(result);gpoScopeCoverage=DesktopViewer.Text(r,"GpoScope")+"\r\n\r\n"+DesktopViewer.Text(r,"OwnershipScope")+"\r\n\r\n"+DesktopViewer.Text(r,"AdcsScope");if(r.ContainsKey("PrerequisiteGaps"))gpoScopeCoverage+="\r\n\r\nRecorded AD CS prerequisite gaps (first 30 of "+r["PrerequisiteGapCount"]+")\r\n"+PrerequisiteText((List<string[]>)r["PrerequisiteGaps"]);var state=(DiskAnalysisProgress)r["Analysis"];analysisStatus=state.Status=="NotStarted"?"No saved analysis":state.Status+(state.Status=="Completed"?"":" (incomplete)");long nodes=0,edges=0;foreach(var row in (List<string[]>)r["Nodes"])nodes+=long.Parse(row[1]);foreach(var row in (List<string[]>)r["Edges"])edges+=long.Parse(row[1]);summary.Text=nodes.ToString("N0")+" objects · "+edges.ToString("N0")+" relationships · "+state.Findings.ToString("N0")+" saved candidates · "+analysisStatus+" · Path rules "+(state.PathRules??"none")+" / current "+state.CurrentPathRules;var snapshot=Controls.Find("Snapshot",true);if(snapshot.Length==1)((Label)snapshot[0]).Text=indexPath+" · Updated "+File.GetLastWriteTime(indexPath).ToString("g");findNote.Text=state.Status+": "+state.StopReason;if(state.Status=="Outdated"){SetHelp("Saved findings need regeneration",state.StopReason+"\r\n\r\nOpened index: "+indexPath+"\r\n\r\nObject browsing and Routes to target use the collected graph with current rules. They remain available. Close older scanner/viewer windows after an update.");return;}BeginInvoke(new Action(delegate{LoadPage(findingPage);}));});}
        private void LoadPage(DesktopPager pager)
        {
            var cursor=ViewerJson.Map("score",pager.Score,"sequence",pager.Sequence,"after",pager.After);string query=pager.Query;
            Run(delegate(GraphIndex db){Dictionary<string,object> r=null;int inspected=0;for(int window=0;window<20;window++){
                r=DesktopViewer.Map(pager.Findings?db.ViewerFindings((int)DesktopViewer.Number(cursor,"score"),DesktopViewer.Number(cursor,"sequence"),query):db.ViewerSearch(query,DesktopViewer.Text(cursor,"after")));inspected+=(int)DesktopViewer.Number(r,"Scanned");
                if(pager.Findings){cursor["score"]=r["AfterScore"];cursor["sequence"]=r["AfterSequence"];}else cursor["after"]=r["After"];
                if(DesktopViewer.Rows(r).Count>0||!DesktopViewer.Flag(r,"More"))break;
            }r["Inspected"]=inspected;return r;},delegate(object result){var r=DesktopViewer.Map(result);pager.More=DesktopViewer.Flag(r,"More");pager.NextCursor=cursor;
                var g=pager.Findings?findings:objects;g.Rows.Clear();foreach(object item in DesktopViewer.Rows(r)){var row=DesktopViewer.Map(item);if(pager.Findings){bool high=DesktopViewer.Number(row,"Score")>=65;AddRow(g,row,high?"High review":"Medium",DesktopViewer.Text(DesktopViewer.Map(row["Source"]),"n"),DesktopViewer.Text(DesktopViewer.Map(row["Target"]),"n"),row["HopCount"],row["Score"]);g.Rows[g.Rows.Count-1].Cells[0].Style.ForeColor=high?Color.FromArgb(255,205,133):Color.FromArgb(121,179,255);}else AddRow(g,row,row["n"],row["t"],row["i"]);}
                (pager.Findings?findNote:objectNote).Text=(pager.Findings?analysisStatus+" · ":"")+DesktopViewer.Rows(r).Count+" matches · Page "+(pager.History.Count+1)+" · "+DesktopViewer.Number(r,"Inspected").ToString("N0")+" rows inspected. "+(pager.More?"More index data remains; choose Next.":"End of search in the remaining index.");
            });
        }
        private void OpenFinding(){var f=Selected(findings);if(f!=null)LoadRecorded(DesktopViewer.Text(DesktopViewer.Map(f["Source"]),"i"));}
        private void LoadRecorded(string id){LoadRecorded(id,false);}
        private void LoadRecorded(string id,bool continued){Run(delegate(GraphIndex db){return db.ViewerRecordedPath(id);},delegate(object result){DisplayPath(DesktopViewer.Map(result),true,continued);});}
        private void InspectSelected(){var n=Selected(objects);if(n!=null)Inspect(DesktopViewer.Text(n,"i"));}
        private void UseNode(Dictionary<string,object> node,bool source){if(node==null)return;(source?sourceId:targetId).Text=DesktopViewer.Text(node,"i");SetStatus((source?"Source: ":"Target: ")+DesktopViewer.Text(node,"n"),false);}
        private void Inspect(string id){Inspect(id,-1);}
        private void Inspect(string id,int historyPosition){Run(delegate(GraphIndex db){return db.ViewerObject(id);},delegate(object result){
            if(historyPosition>=0)objectHistoryPosition=historyPosition;
            else if(objectHistoryPosition<0 || !string.Equals(objectHistory[objectHistoryPosition],id,StringComparison.OrdinalIgnoreCase)){
                if(objectHistoryPosition+1<objectHistory.Count)objectHistory.RemoveRange(objectHistoryPosition+1,objectHistory.Count-objectHistoryPosition-1);
                objectHistory.Add(id);if(objectHistory.Count>100)objectHistory.RemoveAt(0);objectHistoryPosition=objectHistory.Count-1;
            }
            selectedObject=DesktopViewer.Map(result);identity.Text=DesktopViewer.Text(selectedObject,"n")+" · "+id;tips.SetToolTip(identity,id);properties.Text=DesktopViewer.Flag(selectedObject,"Limited")?"Record exceeds the viewer's 1 MiB decompressed / 2 MiB stored property limit. The complete record remains in the index.":FormatRecord(DesktopViewer.Text(selectedObject,"Record"));if(properties.Text.Length==0)properties.Text="No collected object properties are available for this identity.";ResetRelationships();relationships.Rows.Clear();relationships.Columns["Ownership"].Visible=false;NodeHelp(selectedObject);tabs.SelectedIndex=1;BeginInvoke(new Action(LoadRelationships));});}
        private static string FormatRecord(string record){
            var output=new StringBuilder();bool quoted=false,escaped=false;int depth=0;
            foreach(char c in record){
                if(quoted){output.Append(c);if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='"')quoted=false;}
                else if(c=='"'){quoted=true;output.Append(c);}
                else if(c=='{'||c=='['){output.Append(c);depth++;output.AppendLine();output.Append(' ',Math.Min(depth,32)*2);}
                else if(c=='}'||c==']'){depth=Math.Max(0,depth-1);output.AppendLine();output.Append(' ',Math.Min(depth,32)*2);output.Append(c);}
                else if(c==','){output.Append(c);output.AppendLine();output.Append(' ',Math.Min(depth,32)*2);}
                else if(c==':')output.Append(": ");else if(!char.IsWhiteSpace(c))output.Append(c);
                if(output.Length>2097152)return output.ToString(0,2097152)+"\r\nProperty formatting preview shortened. Inspect the retained source record for the rest.";
            }
            return output.ToString();
        }
        private void ResetRelationships(){relationshipCursor=ViewerJson.Map("target","","bucket","","source","","type","");}
        private void LoadRelationships(){if(selectedObject==null)return;string id=DesktopViewer.Text(selectedObject,"i"),dir=direction.SelectedIndex==0?"in":"out";var c=new Dictionary<string,object>(relationshipCursor);
            Run(delegate(GraphIndex db){Dictionary<string,object> r=null;int scanned=0;for(int window=0;window<20;window++){r=DesktopViewer.Map(db.ViewerRelationships(id,dir,DesktopViewer.Text(c,"target"),DesktopViewer.Text(c,"bucket"),DesktopViewer.Text(c,"source"),DesktopViewer.Text(c,"type")));scanned+=(int)DesktopViewer.Number(r,"Scanned");c=ViewerJson.Map("target",r["Target"],"bucket",r["Bucket"],"source",r["Source"],"type",r["Type"],"more",r["More"]);if(DesktopViewer.Rows(r).Count>0||!DesktopViewer.Flag(r,"More"))break;}r["Inspected"]=scanned;return r;},delegate(object result){var r=DesktopViewer.Map(result);relationshipCursor=c;relationships.Rows.Clear();relationships.Columns["Ownership"].Visible=false;foreach(object item in DesktopViewer.Rows(r)){var e=DesktopViewer.Map(item);AddRow(relationships,e,e["sn"],e["ty"],e["tn"]);}relationshipNote.Text=DesktopViewer.Rows(r).Count+" relationships · "+DesktopViewer.Number(r,"Inspected").ToString("N0")+" rows inspected · "+(DesktopViewer.Flag(r,"More")?"More data remains.":"End of relationships.")+(dir=="out"?" Outgoing queries scan bounded disk windows.":"");});}
        private void QueryPath(string source,string target){if(source.Length==0){if(viaOne.Text.Trim().Length>0||viaTwo.Text.Trim().Length>0){SetStatus("Enter a source when using waypoints.",true);return;}QueryTarget(target);return;}QueryPath(source,target,false);}
        private void ShowRoute(List<object> rows){if(targetGraphEdges==null||rows.Count==0)return;ShowTargetRows(rows);SetConnectionLegend(new List<object>());var first=DesktopViewer.Map(rows[0]);var last=DesktopViewer.Map(rows[rows.Count-1]);connectionTitle.Text="Route: "+DesktopViewer.Text(first,"sn")+" \u2192 "+DesktopViewer.Text(last,"tn")+" \u00B7 "+rows.Count+" relationships. Prev route and Next route show other routes; All relationships clears.";}private void ShowTargetRows(List<object> rows){steps.Rows.Clear();steps.Columns["Ownership"].Visible=false;int number=0;foreach(object item in rows){var edge=DesktopViewer.Map(item);AddRow(steps,edge,++number,edge["sn"],edge["ty"],edge["tn"]);}}
        private void SetConnectionLegend(List<object> rows){while(connectionLegend.Controls.Count>0){var control=connectionLegend.Controls[0];connectionLegend.Controls.RemoveAt(0);control.Dispose();}connectionLegendEdges.Clear();connectionLegendEdges.AddRange(rows);connectionTitle.Text=rows.Count==0?"Select a connection to list every relationship here.":DesktopViewer.Text(DesktopViewer.Map(rows[0]),"sn")+" → "+DesktopViewer.Text(DesktopViewer.Map(rows[0]),"tn")+" · "+rows.Count+" relationships (numbered on the curves)";for(int i=0;i<rows.Count;i++){var edge=DesktopViewer.Map(rows[i]);var button=new Button{Text=(i+1)+"  "+DesktopViewer.Text(edge,"ty"),Tag=edge,AutoSize=true,Height=30,BackColor=PanelColor,ForeColor=Ink,FlatStyle=FlatStyle.Flat,Margin=new Padding(3),AccessibleName="Relationship "+(i+1)+" of "+rows.Count+": "+DesktopViewer.Text(edge,"ty")};button.Click+=delegate(object sender,EventArgs args){SelectGraphRelationship((Dictionary<string,object>)((Button)sender).Tag);};connectionLegend.Controls.Add(button);}}
        private void UpdateConnectionLegend(Dictionary<string,object> edge){if(targetGraphEdges==null)return;var pair=new List<object>();foreach(object item in targetGraphEdges){var e=DesktopViewer.Map(item);if(string.Equals(DesktopViewer.Text(e,"s"),DesktopViewer.Text(edge,"s"),StringComparison.OrdinalIgnoreCase)&&string.Equals(DesktopViewer.Text(e,"t"),DesktopViewer.Text(edge,"t"),StringComparison.OrdinalIgnoreCase))pair.Add(item);}SetConnectionLegend(pair);}
        private void SelectGraphRelationship(Dictionary<string,object> edge){foreach(DataGridViewRow row in steps.Rows)if(ReferenceEquals(row.Tag,edge)){steps.CurrentCell=row.Cells[0];break;}Explain(edge);graph.Highlight(edge);}
        private void ShowConnection(List<object> rows){if(rows.Count==0)return;if(targetGraphEdges==null){Explain(DesktopViewer.Map(rows[0]));return;}ShowTargetRows(rows);SetConnectionLegend(rows);var first=DesktopViewer.Map(rows[0]);var types=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);foreach(object item in rows)types.Add(DesktopViewer.Text(DesktopViewer.Map(item),"ty"));RevealDetails();SetHelp("Connection · "+rows.Count+" relationships",DesktopViewer.Text(first,"sn")+" → "+DesktopViewer.Text(first,"tn")+"\r\n\r\n"+string.Join("\r\n",new List<string>(types).ToArray())+"\r\n\r\nThe table now shows the individual relationships for this direction. Select a row for its evidence, prerequisites and recommendations. Choose All relationships to restore the full table. Existing privileged access may be expected administration; this is not automatically a new escalation.");}
        private void QueryTarget(string target){
            if(target.Length==0){SetStatus("Enter a target name or ID, for example DA.",true);return;}
            int limit=int.Parse(visitLimit.Text);bool shortest=routeMode.SelectedIndex==0;var allowed=allowedPathTypes;
            Run(delegate(GraphIndex db){return db.ViewerTargetGraph(target,limit,2000,shortest,allowed);},delegate(object result){
                var r=DesktopViewer.Map(result);path=null;steps.Columns["Step"].HeaderText="Row";steps.Rows.Clear();steps.Columns["Ownership"].Visible=false;var rows=DesktopViewer.Rows(ViewerJson.Map("Rows",r["Edges"]));var nodeRows=(List<object>)r["Nodes"];
                targetGraphEdges=rows;SetConnectionLegend(new List<object>());ShowTargetRows(rows);graph.SetNetwork(rows,nodeRows,!shortest);tabs.SelectedIndex=2;var node=DesktopViewer.Map(r["Target"]);targetId.Text=DesktopViewer.Text(node,"i");
                pathNote.Text=DesktopViewer.Text(node,"n")+" · "+(shortest?"Shortest routes":"All connections")+" · "+rows.Count+" relationships · "+graph.ConnectionCount+" connections · "+r["Visited"]+" objects · "+(DesktopViewer.Flag(r,"Limited")?"INCOMPLETE: "+r["Status"]+" reached.":shortest?"Complete shortest-route overview.":"Complete reverse-reachable graph.");
                SetHelp("Routes to target",pathNote.Text+"\r\n\r\n"+(shortest?"Only edges on shortest routes toward this target are shown. Equal-length alternatives remain; cyclic and longer detours are omitted. Choose All connections to include them.":"Alternative edges and cycles are retained. This does not enumerate repeated cyclic path permutations.")+"\r\n\r\nParallel relationships share one connection per direction. Click it to expand separate curves and list the individual relationships in the table. Select a row or expanded curve for its evidence and recommendations. All relationships restores the table.\r\n\r\nExisting administrator relationships and ordinary memberships are context, not automatically new escalation findings. Endpoint and effective-access conditions still require validation.");UpdateButtons();
            });
        }
        private void QueryPath(string source,string target,bool continued){if(source.Length==0||target.Length==0){SetStatus("Select or enter both source and target object IDs.",true);return;}int limit=int.Parse(visitLimit.Text);var allowed=allowedPathTypes;var ordered=new List<string>{source};if(!continued){if(viaOne.Text.Trim().Length>0)ordered.Add(viaOne.Text.Trim());if(viaTwo.Text.Trim().Length>0)ordered.Add(viaTwo.Text.Trim());}ordered.Add(target);Run(delegate(GraphIndex db){return continued?db.ViewerShortestPath(source,target,limit,allowed):ordered.Count>2?db.ViewerWaypointPaths(ordered.ToArray(),limit,2000,allowed):db.ViewerAllShortestPaths(source,target,limit,2000,allowed);},delegate(object result){var p=DesktopViewer.Map(result);string state=DesktopViewer.Text(p,"Status");if(state=="Found"){if(continued)DisplayPath(p,false,true);else DisplayShortestRoutes(p);}else{targetGraphEdges=null;SetConnectionLegend(new List<object>());path=null;steps.Rows.Clear();steps.Columns["Ownership"].Visible=false;graph.SetEdges(new List<object>());pathNote.Text=p.ContainsKey("Failure")?DesktopViewer.Text(p,"Failure"):(state=="VisitLimit"||state=="SearchEdgeLimit")?"Search budget reached; this result is incomplete. Increase the budget or choose a closer source/target.":"No path found through the supported resolved relationships. Collection gaps or other relationship types may hide routes.";SetHelp("Path search result",pathNote.Text+(p.ContainsKey("Legs")?"\r\n\r\n"+WaypointSummary(p):""));tabs.SelectedIndex=2;}});}
        private void Continue(){if(path==null||!DesktopViewer.Flag(path,"PathLimited"))return;string next=DesktopViewer.Text(path,"NextPathId");if(pathIsSaved)LoadRecorded(next,true);else QueryPath(next,DesktopViewer.Text(DesktopViewer.Map(path["Target"]),"i"),true);}
        private void ShowSingleEdge(Dictionary<string,object> edge){var p=ViewerJson.Map("Source",ViewerJson.Map("i",edge["s"],"n",edge["sn"]),"Target",ViewerJson.Map("i",edge["t"],"n",edge["tn"]),"Steps",new List<object>{edge},"HopCount",1,"PathLimited",false);DisplayPath(p,false);string type=DesktopViewer.Text(edge,"ty"),evidence=DesktopViewer.Text(edge,"Evidence");if(type.EndsWith("Raw",StringComparison.Ordinal)||type.EndsWith("LimitedRights",StringComparison.Ordinal)||(evidence.Length>0&&JsonRecordReader.ExtractField(evidence,"Assessment")=="Suppressed projection"))pathNote.Text="Inventory inspection only — excluded from automatic control-path traversal.";Explain(edge);}
        private static string WaypointSummary(Dictionary<string,object> value)
        {
            var text=new StringBuilder();foreach(object item in (List<object>)value["Legs"])
            {
                var leg=DesktopViewer.Map(item);text.Append("Leg ").Append(leg["Number"]).Append(": ").Append(DesktopViewer.Text(DesktopViewer.Map(leg["Source"]),"n")).Append(" → ").Append(DesktopViewer.Text(DesktopViewer.Map(leg["Target"]),"n"));
                text.Append(DesktopViewer.Flag(leg,"Found")?" · "+leg["HopCount"]+" shortest hops":" · no established route");
                text.AppendLine(DesktopViewer.Flag(leg,"Limited")?" · INCOMPLETE: "+leg["Status"]:"");
            }
            return text.ToString();
        }
        private void DisplayShortestRoutes(Dictionary<string,object> value)
        {
            DisplayPath(value,false,false);
            var rows=(List<object>)value["Edges"];
            if(DesktopViewer.Flag(value,"MultiLeg")||rows.Count>DesktopViewer.Number(value,"HopCount"))
            {
                targetGraphEdges=rows;SetConnectionLegend(new List<object>());steps.Columns["Step"].HeaderText="Row";ShowTargetRows(rows);
                graph.SetNetwork(rows,(List<object>)value["Nodes"],DesktopViewer.Flag(value,"MultiLeg"));graph.Fit();
                path=new Dictionary<string,object>(value);path["Steps"]=rows;path["PathLimited"]=false;path["NextPathId"]=null;path["GraphMode"]=true;UpdateButtons();
                pathNote.Text=DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"n")+" → "+DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"n")+" · "+value["HopCount"]+" hops per shortest route · "+rows.Count+" relationships · "+graph.ConnectionCount+" connections · All equally short routes";
                if(DesktopViewer.Flag(value,"MultiLeg")){pathNote.Text=DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"n")+" → "+DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"n")+" · "+((List<object>)value["Legs"]).Count+" ordered legs · "+value["HopCount"]+" total hops · "+rows.Count+" unique relationships";SetHelp("Ordered waypoint routes",WaypointSummary(value)+"\r\nEach consecutive leg includes all equally short routes. The merged graph may revisit nodes or relationships; its rows are not a sequential chain. Filters apply to every leg. Visit budgets apply per leg, the time budget covers the entire query, and the displayed edge budget covers the merged graph. Effective access requires validation.");}else SetHelp("All shortest routes","The graph contains the union of equally short directed routes between the selected objects. Each table row is a relationship, not a sequential step in one chain. Longer detours and cycles are excluded. Select a connection to inspect its individual permissions. Recorded paths require effective-access validation.");
            }
            else if(!DesktopViewer.Flag(value,"Limited"))pathNote.Text+=" All equally short routes included.";
            if(DesktopViewer.Flag(value,"Limited"))pathNote.Text+=" INCOMPLETE: "+DesktopViewer.Text(value,"GraphStatus")+" reached; additional shortest routes may be missing.";
        }
        private void DisplayPath(Dictionary<string,object> value,bool saved){DisplayPath(value,saved,false);}
        private void DisplayPath(Dictionary<string,object> value,bool saved,bool continued){
            if(continued)pathOffset+=steps.Rows.Count;else{pathOffset=0;totalPathHops=DesktopViewer.Number(value,"HopCount");pathOriginId=DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"i");pathOriginName=DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"n");}
            targetGraphEdges=null;SetConnectionLegend(new List<object>());steps.Columns["Step"].HeaderText="Step";path=value;pathIsSaved=saved;steps.Rows.Clear();steps.Columns["Ownership"].Visible=false;var edges=(List<object>)value["Steps"];long step=pathOffset;foreach(object item in edges){var e=DesktopViewer.Map(item);AddRow(steps,e,++step,e["sn"],e["ty"],e["tn"]);}graph.SetEdges(edges);tabs.SelectedIndex=2;graph.Fit();
            sourceId.Text=pathOriginId;targetId.Text=DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"i");
            pathNote.Text=pathOriginName+" → "+DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"n")+" · "+totalPathHops+" recorded hops · "+(edges.Count==0?"No relationship steps.":"Steps "+(pathOffset+1)+"–"+(pathOffset+edges.Count)+" of "+totalPathHops+". ")+(DesktopViewer.Flag(value,"PathLimited")?"Continue for the next segment.":pathOffset>0?"Final segment.":"Complete chain.");
            var b=new StringBuilder();b.AppendLine(DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"n")+" → "+DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"n"));
            b.AppendLine("\r\n"+(saved?"Why it is flagged\r\nThe saved chain contains a permission or access capability and reaches a selected review target. This is a review candidate; effective access requires validation.":"What this establishes\r\nThe index contains this chain of supported relationships. A custom target is not necessarily privileged. This does not establish effective access."));
            if(saved){b.AppendLine("\r\nTriage score: "+DesktopViewer.Text(value,"Score"));foreach(string reason in (List<string>)value["Reasons"])b.AppendLine("• "+reason);}
            b.AppendLine("\r\nSelect a graph line or path step for its explanation and recommended fix.\r\n\r\n"+DesktopViewer.ChangeProcess);SetHelp("Recorded path & recommended fixes",b.ToString());UpdateButtons();
        }
        private void ExportPlan(){if(path==null){SetStatus("Open a path before exporting its review plan.",true);return;}using(var dialog=new SaveFileDialog{Title="Export this path segment's review plan",Filter="Text review plan (*.txt)|*.txt",FileName="bhtest-review-plan.txt"}){if(dialog.ShowDialog(this)!=DialogResult.OK)return;var b=new StringBuilder("BHTest review plan — displayed relationships\r\n");b.AppendLine("Source: "+DesktopViewer.Text(DesktopViewer.Map(path["Source"]),"i"));b.AppendLine("Target: "+DesktopViewer.Text(DesktopViewer.Map(path["Target"]),"i"));b.AppendLine((DesktopViewer.Flag(path,"GraphMode")?"Shortest route length: ":"Remaining chain hops: ")+DesktopViewer.Text(path,"HopCount"));b.AppendLine("Segment limited: "+DesktopViewer.Flag(path,"PathLimited"));b.AppendLine("Continue from: "+DesktopViewer.Text(path,"NextPathId"));b.AppendLine("Recorded relationships require validation. Original coverage is not stored in this index.\r\n");if(path.ContainsKey("Legs"))b.AppendLine(WaypointSummary(path));foreach(object item in (List<object>)path["Steps"])b.AppendLine(DesktopViewer.Explain(DesktopViewer.Map(item))+"\r\n--------------------\r\n");File.WriteAllText(dialog.FileName,b.ToString(),new UTF8Encoding(true));SetStatus("Exported the displayed path segment's review plan.",false);}}
    }
    internal sealed class DesktopPager
    {
        public readonly bool Findings;public int Score=int.MaxValue;public long Sequence;public string After="",Query="";public bool More;
        public Dictionary<string,object> NextCursor;public readonly List<Dictionary<string,object>> History=new List<Dictionary<string,object>>();
        public DesktopPager(bool findings){Findings=findings;}
        private Dictionary<string,object> Current(){return ViewerJson.Map("score",Score,"sequence",Sequence,"after",After);}
        private void Set(Dictionary<string,object> c){Score=(int)DesktopViewer.Number(c,"score");Sequence=DesktopViewer.Number(c,"sequence");After=DesktopViewer.Text(c,"after");}
        public void Reset(string query){Score=int.MaxValue;Sequence=0;After="";Query=query.Trim();More=false;History.Clear();}
        public void Next(){if(!More||NextCursor==null)return;History.Add(Current());Set(NextCursor);}
        public void Previous(){if(History.Count==0)return;Set(History[History.Count-1]);History.RemoveAt(History.Count-1);}
    }

    // Layered layout: depth layers run left to right (deepest first, the target last). A crowded layer
    // wraps into several columns, so the picture uses the width of the window instead of one very tall column.
    // Returns x and y for every node, in the order the layers were supplied (shallowest layer first).
    internal static class GraphLayout
    {
        public static void Layers(int[] sizes,float canvasWidth,float canvasHeight,out float[] xs,out float[] ys)
        {
            const float cellWidth=230f,cellHeight=84f,gap=110f;
            int total=0,largest=1;foreach(int n in sizes){total+=n;if(n>largest)largest=n;}
            xs=new float[total];ys=new float[total];if(total==0)return;
            float availableWidth=Math.Max(300f,(canvasWidth<200f?1400f:canvasWidth)-190f),availableHeight=Math.Max(200f,(canvasHeight<150f?500f:canvasHeight)-85f);
            int bestRows=1;float bestScale=-1f,bestWidth=float.MaxValue;
            for(int rows=1;rows<=largest;rows++)
            {
                float width=-gap;int tall=1;
                foreach(int n in sizes){if(n<1)continue;int columns=(n+rows-1)/rows;int used=(n+columns-1)/columns;width+=columns*cellWidth+gap;if(used>tall)tall=used;}
                float scale=Math.Min(1.15f,Math.Min(availableWidth/Math.Max(1f,width),availableHeight/Math.Max(1f,tall*cellHeight)));
                if(scale>bestScale+.0001f||(scale>bestScale-.0001f&&width<bestWidth)){bestScale=scale;bestRows=rows;bestWidth=width;}
            }
            int[] first=new int[sizes.Length];int running=0;for(int i=0;i<sizes.Length;i++){first[i]=running;running+=sizes[i];}
            float cursor=0f;
            for(int i=sizes.Length-1;i>=0;i--)
            {
                int n=sizes[i];if(n<1)continue;int columns=(n+bestRows-1)/bestRows;int used=(n+columns-1)/columns;
                for(int k=0;k<n;k++){int column=k/used,row=k%used;xs[first[i]+k]=cursor+column*cellWidth;ys[first[i]+k]=(row-(used-1)/2f)*cellHeight;}
                cursor+=columns*cellWidth+gap;
            }
        }
    }

    // Picks ONE route through a node. depth[n] is the number of hops from node n to the target (0 = the target).
    // Only connections that move toward the target (source deeper than target) are followed, so a route can
    // never loop. Where several connections qualify, the variant number chooses between them like an odometer
    // (variant 0 is the first alternative everywhere, variant 1 changes the first choice, and so on).
    // Returns connection indexes in order, from the far source to the target.
    internal static class RouteTracer
    {
        public static int[] Trace(int node,int variant,int[] depth,int[] source,int[] target)
        {
            int count=depth.Length;if(node<0||node>=count)return new int[0];
            var outgoing=new List<int>[count];var incoming=new List<int>[count];
            for(int c=0;c<source.Length;c++)
            {
                int s=source[c],t=target[c];if(s<0||t<0||s>=count||t>=count||depth[t]>=depth[s])continue;
                if(outgoing[s]==null)outgoing[s]=new List<int>();outgoing[s].Add(c);
                if(incoming[t]==null)incoming[t]=new List<int>();incoming[t].Add(c);
            }
            int remaining=Math.Max(0,variant);var down=new List<int>();var up=new List<int>();
            int current=node;
            while(outgoing[current]!=null)
            {
                var choices=new List<int>();foreach(int c in outgoing[current])if(depth[target[c]]==depth[current]-1)choices.Add(c);
                if(choices.Count==0)choices=outgoing[current];
                int pick=choices[remaining%choices.Count];remaining/=choices.Count;down.Add(pick);current=target[pick];
            }
            current=node;
            while(incoming[current]!=null)
            {
                var choices=new List<int>();foreach(int c in incoming[current])if(depth[source[c]]==depth[current]+1)choices.Add(c);
                if(choices.Count==0)choices=incoming[current];
                int pick=choices[remaining%choices.Count];remaining/=choices.Count;up.Add(pick);current=source[pick];
            }
            up.Reverse();up.AddRange(down);return up.ToArray();
        }
    }

    public sealed class PathCanvas : Control
    {
        private sealed class Connection { public int Source,Target,Index; public readonly List<object> Edges=new List<object>(); }
        private sealed class LabelHit {public RectangleF Bounds;public int Connection;public Dictionary<string,object> Edge;} private readonly List<LabelHit> labelHits=new List<LabelHit>();
        private List<Dictionary<string,object>> nodes=new List<Dictionary<string,object>>();
        private readonly List<PointF> layout=new List<PointF>();
        private readonly Dictionary<string,int> nodeIndex=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Connection> connections=new List<Connection>();
        private readonly HashSet<string> directions=new HashSet<string>();
        private List<object> edges=new List<object>();
        private float scale=1,panX=40,panY=100;
        private Point down,last;private bool dragging,moved,network,spread,dense;private readonly List<int> depthOf=new List<int>();private HashSet<int> routeSet,routeNodes;private Dictionary<int,string> routeLabels;private int routeNode,routeVariant;public event Action<List<object>> RouteSelected;
        private Dictionary<string,object> highlighted;
        public event Action<Dictionary<string,object>> NodeSelected,EdgeSelected;
        public event Action<List<object>> ConnectionSelected;
        public int SelectedIndex{get;private set;}
        public int ConnectionCount{get{return connections.Count;}}
        public int RelationshipCount{get{return edges.Count;}}
        public Dictionary<string,object> SelectedNode{get{return nodes.Count==0?null:nodes[SelectedIndex];}}
        public PathCanvas(){DoubleBuffered=true;BackColor=Color.FromArgb(13,23,40);TabStop=true;Resize+=delegate{Invalidate();};}
        private static string Key(int a,int b){return a+":"+b;}
        private void BuildConnections(){connections.Clear();directions.Clear();var lookup=new Dictionary<string,Connection>();foreach(object item in edges){var edge=DesktopViewer.Map(item);int s=nodeIndex[DesktopViewer.Text(edge,"s")],t=nodeIndex[DesktopViewer.Text(edge,"t")];string key=Key(s,t);Connection c;if(!lookup.TryGetValue(key,out c)){c=new Connection{Source=s,Target=t,Index=connections.Count};lookup.Add(key,c);connections.Add(c);directions.Add(key);}c.Edges.Add(item);}SelectedIndex=0;highlighted=null;Fit();}
        public void SetEdges(List<object> value){spread=false;dense=false;network=false;depthOf.Clear();ClearRoute();edges=value;nodes=new List<Dictionary<string,object>>();nodeIndex.Clear();layout.Clear();foreach(object item in edges){var e=DesktopViewer.Map(item);foreach(string side in new string[]{"s","t"}){string id=DesktopViewer.Text(e,side);if(!nodeIndex.ContainsKey(id)){nodeIndex.Add(id,nodes.Count);layout.Add(new PointF(nodes.Count*260,0));nodes.Add(ViewerJson.Map("i",id,"n",DesktopViewer.Text(e,side=="s"?"sn":"tn"),"t",DesktopViewer.Text(e,side=="s"?"st":"tt")));}}}BuildConnections();}
        public void SetNetwork(List<object> value,List<object> objects){SetNetwork(value,objects,false);}
        public void SetNetwork(List<object> value,List<object> objects,bool spreadOut){spread=false;network=true;edges=value;nodes=new List<Dictionary<string,object>>();nodeIndex.Clear();layout.Clear();depthOf.Clear();ClearRoute();var layers=new SortedDictionary<int,List<Dictionary<string,object>>>();foreach(object item in objects){var node=DesktopViewer.Map(item);int depth=(int)DesktopViewer.Number(node,"Depth");if(!layers.ContainsKey(depth))layers.Add(depth,new List<Dictionary<string,object>>());layers[depth].Add(node);}var sizes=new List<int>();foreach(var layer in layers){layer.Value.Sort(delegate(Dictionary<string,object> a,Dictionary<string,object> b){return StringComparer.OrdinalIgnoreCase.Compare(DesktopViewer.Text(a,"n")+DesktopViewer.Text(a,"i"),DesktopViewer.Text(b,"n")+DesktopViewer.Text(b,"i"));});sizes.Add(layer.Value.Count);for(int row=0;row<layer.Value.Count;row++){var node=layer.Value[row];nodeIndex.Add(DesktopViewer.Text(node,"i"),nodes.Count);nodes.Add(node);depthOf.Add(layer.Key);layout.Add(new PointF(0,0));}}float[] xs,ys;GraphLayout.Layers(sizes.ToArray(),ClientSize.Width,ClientSize.Height,out xs,out ys);for(int i=0;i<layout.Count;i++)layout[i]=new PointF(xs[i],ys[i]);BuildConnections();dense=connections.Count>90;Invalidate();}
        private PointF Position(int index){return new PointF(panX+layout[index].X*scale,panY+layout[index].Y*scale);}
        public void Fit(){float minX=0,maxX=0,minY=0,maxY=0;foreach(var p in layout){minX=Math.Min(minX,p.X);maxX=Math.Max(maxX,p.X);minY=Math.Min(minY,p.Y);maxY=Math.Max(maxY,p.Y);}scale=Math.Max(.025f,Math.Min(1.15f,Math.Min(Math.Max(30,ClientSize.Width-(spread?430f:190f))/Math.Max(1,maxX-minX),Math.Max(35,ClientSize.Height-(spread?110f:85f))/Math.Max(1,maxY-minY))));panX=(ClientSize.Width-(maxX+minX)*scale)/2;panY=(ClientSize.Height-(maxY+minY)*scale)/2+10;Invalidate();}
        public void Zoom(float factor){ZoomAt(factor,new PointF(ClientSize.Width/2f,ClientSize.Height/2f));}
        private void ZoomAt(float factor,PointF anchor){float next=Math.Max(.025f,Math.Min(3,scale*factor)),ratio=next/scale;panX=anchor.X-(anchor.X-panX)*ratio;panY=anchor.Y-(anchor.Y-panY)*ratio;scale=next;Invalidate();}
        public void FocusNode(int index){if(nodes.Count==0)return;SelectedIndex=Math.Max(0,Math.Min(nodes.Count-1,index));scale=1;panX=ClientSize.Width/2f-layout[SelectedIndex].X;panY=ClientSize.Height/2f-15-layout[SelectedIndex].Y;Invalidate();if(NodeSelected!=null)NodeSelected(SelectedNode);}
        public void Highlight(Dictionary<string,object> edge){highlighted=edge;if(edge==null)ClearRoute();Invalidate();} public bool RouteActive{get{return routeSet!=null;}} public void TraceRoute(int node,int variant){if(!network||node<0||node>=nodes.Count||depthOf.Count!=nodes.Count)return;var source=new int[connections.Count];var target=new int[connections.Count];for(int i=0;i<connections.Count;i++){source[i]=connections[i].Source;target[i]=connections[i].Target;}int[] chain=RouteTracer.Trace(node,variant,depthOf.ToArray(),source,target);if(chain.Length==0){ClearRoute();return;}routeNode=node;routeVariant=Math.Max(0,variant);routeSet=new HashSet<int>(chain);routeNodes=new HashSet<int>();routeNodes.Add(node);routeLabels=new Dictionary<int,string>();var rows=new List<object>();foreach(int c in chain){routeNodes.Add(connections[c].Source);routeNodes.Add(connections[c].Target);routeLabels[c]=(rows.Count+1)+(connections[c].Edges.Count>1?"+":"");foreach(object edge in connections[c].Edges)rows.Add(edge);}Invalidate();if(RouteSelected!=null)RouteSelected(rows);} public void NextRoute(int delta){if(routeSet==null)return;TraceRoute(routeNode,routeVariant+delta);} public void ClearRoute(){routeSet=null;routeNodes=null;routeLabels=null;Invalidate();}
        private bool IsHighlighted(Connection c){return highlighted!=null&&c.Edges.Contains(highlighted);}
        public void SelectConnection(int index){if(index<0||index>=connections.Count)return;var c=connections[index];highlighted=DesktopViewer.Map(c.Edges[0]);Invalidate();if(ConnectionSelected!=null)ConnectionSelected(new List<object>(c.Edges));else if(EdgeSelected!=null)EdgeSelected(highlighted);}
        // The same Bezier geometry drives drawing, labels and hit testing.
        private GraphicsPath Curve(Connection c,int lane,int count,out PointF midpoint){var a=Position(c.Source);var b=Position(c.Target);float dx=b.X-a.X,dy=b.Y-a.Y,length=(float)Math.Sqrt(dx*dx+dy*dy);var path=new GraphicsPath();if(length<1){path.AddArc(a.X-22,a.Y-44,44,44,15,325);midpoint=new PointF(a.X,a.Y-44);return path;}float nx=-dy/length,ny=dx/length;bool reverse=directions.Contains(Key(c.Target,c.Source));float offset=network?(reverse?38:Math.Abs(dx)<35?62:0):0;if(count>1)offset+=(reverse?lane+1:lane-(count-1)/2f)*32;if(network)offset*=Math.Max(.4f,Math.Min(1,scale));var p0=new PointF(a.X+dx/length*10,a.Y+dy/length*10);var p3=new PointF(b.X-dx/length*14,b.Y-dy/length*14);var p1=new PointF(a.X+dx/3+nx*offset,a.Y+dy/3+ny*offset);var p2=new PointF(a.X+dx*2/3+nx*offset,a.Y+dy*2/3+ny*offset);path.AddBezier(p0,p1,p2,p3);midpoint=new PointF((p0.X+3*p1.X+3*p2.X+p3.X)/8,(p0.Y+3*p1.Y+3*p2.Y+p3.Y)/8);return path;}
        private RectangleF DrawMarker(Graphics g,string text,PointF point,Color color){float width=text.Length>2?32:22;var rect=new RectangleF(point.X-width/2,point.Y-11,width,22);using(var brush=new SolidBrush(BackColor))g.FillEllipse(brush,rect);using(var pen=new Pen(color,1.5f))g.DrawEllipse(pen,rect);TextRenderer.DrawText(g,text,Font,Rectangle.Round(rect),color,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);return rect;}
        private PointF CurvePoint(Connection c,int lane,int count,float t){PointF unused;using(var path=Curve(c,lane,count,out unused)){var points=path.PathPoints;if(points.Length!=4)return unused;float u=1-t;return new PointF(u*u*u*points[0].X+3*u*u*t*points[1].X+3*u*t*t*points[2].X+t*t*t*points[3].X,u*u*u*points[0].Y+3*u*u*t*points[1].Y+3*u*t*t*points[2].Y+t*t*t*points[3].Y);}}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);labelHits.Clear();var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var muted=new SolidBrush(Color.FromArgb(164,180,202)))g.DrawString(nodes.Count==0?"Open a path or relationship to explore the graph.":"Drag to pan · Select a connection: names above, matching numbers on curves · "+nodes.Count+" objects · "+connections.Count+" connections · "+Math.Round(scale*100)+"%",Font,muted,12,12);
            // Finish every line before drawing markers. No later edge can overwrite a label.
            var pending=new List<LabelHit>();var pendingText=new List<string>();var pendingPoints=new List<PointF>();var pendingColors=new List<Color>();
            for(int pass=0;pass<2;pass++)foreach(var c in connections){bool onRoute=routeSet!=null&&routeSet.Contains(c.Index);bool picked=IsHighlighted(c);bool active=picked||onRoute;if(active!=(pass==1))continue;bool expanded=picked&&network&&c.Edges.Count>1;int lanes=expanded?c.Edges.Count:1;for(int lane=0;lane<lanes;lane++){var item=DesktopViewer.Map(c.Edges[expanded?lane:0]);bool exact=ReferenceEquals(item,highlighted);Color color=onRoute&&!picked?Color.FromArgb(255,207,123):active?(exact?Color.FromArgb(255,207,123):Color.FromArgb(161,203,249)):routeSet!=null?Color.FromArgb(28,89,121,160):highlighted!=null?Color.FromArgb(39,58,81):(dense?Color.FromArgb(90,89,121,160):Color.FromArgb(89,121,160));PointF midpoint;using(var curve=Curve(c,lane,lanes,out midpoint))using(var pen=new Pen(color,exact?3:onRoute?2.6f:active?2:dense?.9f:1.15f))using(var cap=new AdjustableArrowCap(4,5,true)){pen.CustomEndCap=cap;g.DrawPath(pen,curve);}
                string label=null;PointF point=midpoint;
                if(network){if(onRoute&&!picked){label=routeLabels[c.Index];point=midpoint;}else if(active){label=(lane+1).ToString();point=CurvePoint(c,lane,lanes,lanes==1?.5f:.24f+lane*.52f/(lanes-1));}else if(!spread&&c.Edges.Count>1&&scale>=.4f){label="×"+c.Edges.Count;}}
                else if(scale>=.4f){label=DesktopViewer.Text(item,"ty");point.Y-=19;}
                if(label!=null){pending.Add(new LabelHit{Connection=c.Index,Edge=active?item:null});pendingText.Add(label);pendingPoints.Add(point);pendingColors.Add(color);}
            }}
            for(int i=0;i<pending.Count;i++){RectangleF rect;if(network){rect=DrawMarker(g,pendingText[i],pendingPoints[i],pendingColors[i]);}else{var size=g.MeasureString(pendingText[i],Font);rect=new RectangleF(pendingPoints[i].X-size.Width/2-4,pendingPoints[i].Y-size.Height/2,size.Width+8,size.Height);using(var brush=new SolidBrush(BackColor))g.FillRectangle(brush,rect);TextRenderer.DrawText(g,pendingText[i],Font,Rectangle.Round(rect),pendingColors[i],TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);}pending[i].Bounds=rect;labelHits.Add(pending[i]);}
            for(int i=0;i<nodes.Count;i++){var p=Position(i);if(p.X<-200||p.X>Width+200||p.Y<-60||p.Y>Height+60)continue;string type=DesktopViewer.Text(nodes[i],"t");bool endpoint=highlighted!=null&&(DesktopViewer.Text(nodes[i],"i")==DesktopViewer.Text(highlighted,"s")||DesktopViewer.Text(nodes[i],"i")==DesktopViewer.Text(highlighted,"t"));float radius=scale<.4f?5:8;Color fill=type=="users"?Color.FromArgb(121,179,255):type=="computers"?Color.FromArgb(119,217,187):Color.FromArgb(246,199,119);if(routeNodes!=null&&!routeNodes.Contains(i))fill=Color.FromArgb(70,fill);using(var brush=new SolidBrush(fill))g.FillEllipse(brush,p.X-radius,p.Y-radius,radius*2,radius*2);if(endpoint)g.DrawEllipse(Pens.Gold,p.X-radius-3,p.Y-radius-3,radius*2+6,radius*2+6);else if(i==SelectedIndex)g.DrawEllipse(Pens.White,p.X-radius-3,p.Y-radius-3,radius*2+6,radius*2+6);
                if(routeNodes!=null?routeNodes.Contains(i):(scale>=.4f||nodes.Count<=18||i==SelectedIndex||endpoint)){float labelX=p.X-90,labelY=p.Y+14;if(spread){if(layout[i].X>80)labelX=p.X+13;else if(layout[i].X< -80)labelX=p.X-193;else if(layout[i].Y<0)labelY=p.Y-32;}else if(network){labelX=i==0?p.X+13:p.X-193;labelY=p.Y-12;}var rect=new Rectangle((int)labelX,(int)labelY,180,24);TextRenderer.DrawText(g,DesktopViewer.Text(nodes[i],"n"),Font,rect,endpoint||highlighted==null?Color.FromArgb(237,243,252):Color.FromArgb(148,167,192),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.EndEllipsis);}
            }
        }

        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;Focus();down=last=e.Location;dragging=true;moved=false;Capture=true;}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(!dragging)return;if(Math.Abs(e.X-down.X)+Math.Abs(e.Y-down.Y)>5)moved=true;panX+=e.X-last.X;panY+=e.Y-last.Y;last=e.Location;Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(!dragging)return;dragging=false;Capture=false;if(moved)return;foreach(var label in labelHits){if(!label.Bounds.Contains(e.Location))continue;if(label.Edge!=null){highlighted=label.Edge;Invalidate();if(EdgeSelected!=null)EdgeSelected(highlighted);}else SelectConnection(label.Connection);return;}for(int i=0;i<nodes.Count;i++){var p=Position(i);if(Math.Abs(e.X-p.X)<17&&Math.Abs(e.Y-p.Y)<17){SelectedIndex=i;highlighted=null;Invalidate();if(NodeSelected!=null)NodeSelected(nodes[i]);if(network)TraceRoute(i,0);return;}}for(int pass=0;pass<2;pass++)for(int index=0;index<connections.Count;index++){var c=connections[index];bool active=IsHighlighted(c);if(active!=(pass==0))continue;bool expanded=active&&network&&c.Edges.Count>1;int count=expanded?c.Edges.Count:1;for(int lane=0;lane<count;lane++){PointF midpoint;using(var curve=Curve(c,lane,count,out midpoint))using(var hit=new Pen(Color.White,14)){if(!curve.IsOutlineVisible(e.Location,hit))continue;if(expanded){highlighted=DesktopViewer.Map(c.Edges[lane]);Invalidate();if(EdgeSelected!=null)EdgeSelected(highlighted);}else SelectConnection(index);return;}}}}
        protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);ZoomAt(e.Delta>0?1.2f:1/1.2f,e.Location);}
        protected override bool IsInputKey(Keys keyData){return keyData==Keys.Left||keyData==Keys.Right||keyData==Keys.Up||keyData==Keys.Down||base.IsInputKey(keyData);}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Add||e.KeyCode==Keys.Oemplus)Zoom(1.3f);else if(e.KeyCode==Keys.Subtract||e.KeyCode==Keys.OemMinus)Zoom(1/1.3f);else if(e.KeyCode==Keys.D0)Fit();else if(e.KeyCode==Keys.Left)panX+=50;else if(e.KeyCode==Keys.Right)panX-=50;else if(e.KeyCode==Keys.Up)panY+=50;else if(e.KeyCode==Keys.Down)panY-=50;else if(e.KeyCode==Keys.Escape&&routeSet!=null)ClearRoute();else return;e.Handled=true;e.SuppressKeyPress=true;Invalidate();}
    }

}
