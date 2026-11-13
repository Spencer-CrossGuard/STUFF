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
        internal static readonly Dictionary<string,string[]> Guide=CreateGuide();
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
            return h[0]+"\r\n"+Text(edge,"sn")+" → "+Text(edge,"tn")+"\r\n"+type+"\r\n\r\nWhat this means\r\n"+h[1]+"\r\n\r\nWhy it is shown\r\n"+h[2]+"\r\n\r\nPossible impact\r\n"+h[3]+"\r\n\r\nWhat to verify\r\n"+h[4]+"\r\n\r\nLimits\r\n"+h[5]+"\r\n\r\nRecommended change to review\r\n"+h[6]+"\r\n\r\n"+ChangeProcess;
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
        private TextBox findingSearch,objectSearch,sourceId,targetId,properties;
        private ComboBox direction,visitLimit;
        private DataGridView findings,objects,relationships,steps;
        private RichTextBox helper;
        private TabControl tabs;
        private PathCanvas graph;
        private Dictionary<string,object> selectedObject,path;
        private Dictionary<string,object> relationshipCursor=ViewerJson.Map("target","","bucket","","source","","type","");
        private readonly DesktopPager findingPage=new DesktopPager(true),objectPage=new DesktopPager(false);
        private bool pathIsSaved;
        private string analysisStatus="";

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
                Build();Shown+=delegate{LoadOverview();};FormClosing+=CloseRequested;
            }
            catch{fileLock.Dispose();throw;}
        }
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing){fileLock.Dispose();if(helperHeadingFont!=null)helperHeadingFont.Dispose();}}
        private static TableLayoutPanel Stack(int rows)
        {var p=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=rows,BackColor=PanelColor,Padding=new Padding(10)};p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));return p;}
        private static void Fixed(TableLayoutPanel p,int height){p.RowStyles.Add(new RowStyle(SizeType.Absolute,height));}
        private static void Fill(TableLayoutPanel p){p.RowStyles.Add(new RowStyle(SizeType.Percent,100));}
        private static Label Note(string name,string text){return new Label{Name=name,Text=text,Dock=DockStyle.Fill,ForeColor=Muted,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};}
        private static TextBox Input(string name,int width){return new TextBox{Name=name,Width=width,BackColor=Bg,ForeColor=Ink,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(4,6,4,4)};}
        private static FlowLayoutPanel Bar(){return new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,AutoScroll=false,BackColor=PanelColor,Margin=new Padding(0)};}
        private Button Button(string name,string text,Action run)
        {
            var b=new Button{Name=name,Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Height=33,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(25,39,60),ForeColor=Ink,Margin=new Padding(4),Padding=new Padding(6,2,6,2)};b.FlatAppearance.BorderColor=Line;
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
            foreach(string column in columns){g.Columns.Add(column,column);g.Columns[column].SortMode=DataGridViewColumnSortMode.NotSortable;}return g;
        }
        private static Dictionary<string,object> Selected(DataGridView grid){return grid.CurrentRow==null?null:grid.CurrentRow.Tag as Dictionary<string,object>;}
        private static void AddRow(DataGridView grid,Dictionary<string,object> tag,params object[] values){grid.Rows[grid.Rows.Add(values)].Tag=tag;}
        private TabPage Page(string title,Control body){var p=new TabPage(title){BackColor=PanelColor,Padding=new Padding(2)};p.Controls.Add(body);tabs.TabPages.Add(p);return p;}
        private void Build()
        {
            var outer=Stack(3);Fixed(outer,76);Fill(outer);Fixed(outer,42);outer.BackColor=Bg;Controls.Add(outer);
            var heading=Stack(2);Fixed(heading,31);Fill(heading);heading.Padding=new Padding(6,0,6,0);heading.BackColor=Bg;
            heading.Controls.Add(new Label{Text="BH  /  Directory graph workspace",Dock=DockStyle.Fill,Font=new Font(Font.FontFamily,18,FontStyle.Bold),ForeColor=Ink},0,0);
            summary=Note("Summary","Read-only desktop viewer · No listening ports · "+Path.GetFileName(indexPath));heading.Controls.Add(summary,0,1);outer.Controls.Add(heading,0,0);
            var split=new SplitContainer{Dock=DockStyle.Fill,BackColor=Bg,Size=new Size(1390,730),Panel1MinSize=650,Panel2MinSize=280,SplitterWidth=9};split.SplitterDistance=1010;outer.Controls.Add(split,0,1);
            tabs=new TabControl{Name="WorkspaceTabs",Dock=DockStyle.Fill};split.Panel1.Controls.Add(tabs);
            var helpLayout=Stack(4);Fixed(helpLayout,30);Fixed(helpLayout,44);Fill(helpLayout);Fixed(helpLayout,66);split.Panel2.Controls.Add(helpLayout);
            helpLayout.Controls.Add(new Label{Text="PLAIN-LANGUAGE HELPER",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(121,179,255),Font=new Font(Font,FontStyle.Bold)},0,0);
            var glossary=Choice("Glossary","Choose a relationship…");glossary.Dock=DockStyle.Fill;var types=new List<string>(DesktopViewer.Guide.Keys);types.Sort(StringComparer.OrdinalIgnoreCase);glossary.Items.AddRange(types.ToArray());
            glossary.SelectedIndexChanged+=delegate{if(glossary.SelectedIndex>0)Explain(ViewerJson.Map("ty",glossary.Text,"sn","Source principal","tn","Target object"));};helpLayout.Controls.Add(glossary,0,1);
            helper=new RichTextBox{Name="Helper",Dock=DockStyle.Fill,ReadOnly=true,BackColor=PanelColor,ForeColor=Ink,BorderStyle=BorderStyle.None,Font=Font,DetectUrls=false};helpLayout.Controls.Add(helper,0,2);
            helpLayout.Controls.Add(Note("Coverage","Coverage is not stored in this index. Review the original report and scan metadata. Recorded paths require validation."),0,3);
            var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,130));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));status=Note("Status","Ready");cancel=new Button{Name="CancelQuery",Text="Cancel query",Dock=DockStyle.Fill,Enabled=false,FlatStyle=FlatStyle.Flat,ForeColor=Ink};cancel.Click+=delegate{cancelled=true;if(activeQuery!=null)activeQuery.ViewerCancel();};footer.Controls.Add(cancel,0,0);footer.Controls.Add(status,1,0);outer.Controls.Add(footer,0,2);
            BuildFindings();BuildObjects();BuildPaths();SetHelp("Explore your full directory","Choose a saved finding or search for an object. The index stays on disk. Only one result page and one path segment are retained in this viewer.\r\n\r\nThis interface uses native Windows controls and embedded C#. It opens no listening ports and makes no network requests.\r\n\r\nRelationship explanations include meaning, evidence, impact, checks, recommended changes and validation. No directory changes are performed.");
        }
        private void BuildFindings()
        {
            var p=Stack(4);Fixed(p,44);Fixed(p,42);Fill(p);Fixed(p,44);var bar=Bar();findingSearch=Input("FindingSearch",320);
            commands.Add(findingSearch);bar.Controls.AddRange(new Control[]{findingSearch,Button("SearchFindings","Search all candidates",delegate{findingPage.Reset(findingSearch.Text);LoadPage(findingPage);})});p.Controls.Add(bar,0,0);
            findNote=Note("FindingNote","Generate a Deep report to save findings. All saved candidates can be paged here.");p.Controls.Add(findNote,0,1);
            findings=Grid("Findings","Source","Target","Hops","Score");findings.Columns["Hops"].FillWeight=25;findings.Columns["Score"].FillWeight=25;p.Controls.Add(findings,0,2);
            var actions=Bar();findPrevious=Button("FindingPrevious","Previous",delegate{findingPage.Previous();LoadPage(findingPage);});findNext=Button("FindingNext","Next",delegate{findingPage.Next();LoadPage(findingPage);});
            actions.Controls.AddRange(new Control[]{findPrevious,findNext,Button("OpenFinding","Explain path & fixes",OpenFinding),Button("FindingObject","Inspect source",delegate{var f=Selected(findings);if(f!=null)Inspect(DesktopViewer.Text(DesktopViewer.Map(f["Source"]),"i"));})});p.Controls.Add(actions,0,3);findings.CellDoubleClick+=delegate{if(!busy)OpenFinding();};Page("Saved findings",p);
        }
        private void BuildObjects()
        {
            var p=Stack(4);Fixed(p,44);Fixed(p,38);p.RowStyles.Add(new RowStyle(SizeType.Percent,40));p.RowStyles.Add(new RowStyle(SizeType.Percent,60));
            var bar=Bar();objectSearch=Input("ObjectSearch",300);objectPrevious=Button("ObjectPrevious","Previous",delegate{objectPage.Previous();LoadPage(objectPage);});objectNext=Button("ObjectNext","Next",delegate{objectPage.Next();LoadPage(objectPage);});
            commands.Add(objectSearch);bar.Controls.AddRange(new Control[]{objectSearch,Button("SearchObjects","Search index",delegate{objectPage.Reset(objectSearch.Text);LoadPage(objectPage);}),objectPrevious,objectNext});p.Controls.Add(bar,0,0);
            objectNote=Note("ObjectNote","Search all object names and IDs. Empty windows have explicit continuation.");p.Controls.Add(objectNote,0,1);
            var list=Stack(2);Fill(list);Fixed(list,42);list.Padding=new Padding(0);objects=Grid("Objects","Name","Type","Identifier");list.Controls.Add(objects,0,0);
            var actions=Bar();actions.Controls.AddRange(new Control[]{Button("InspectObject","Inspect object",InspectSelected),Button("ObjectSource","Use as source",delegate{UseNode(Selected(objects),true);}),Button("ObjectTarget","Use as target",delegate{UseNode(Selected(objects),false);})});list.Controls.Add(actions,0,1);p.Controls.Add(list,0,2);objects.CellDoubleClick+=delegate{if(!busy)InspectSelected();};
            var detail=Stack(3);Fixed(detail,30);Fixed(detail,43);Fill(detail);identity=Note("ObjectIdentity","Select an object to inspect its properties and relationships.");detail.Controls.Add(identity,0,0);
            var rb=Bar();direction=Choice("Direction","Incoming","Outgoing");commands.Add(direction);direction.SelectedIndexChanged+=delegate{if(!busy&&selectedObject!=null){ResetRelationships();LoadRelationships();}};
            relationshipNext=Button("RelationshipNext","Next relationships",delegate{LoadRelationships();});rb.Controls.AddRange(new Control[]{direction,Button("LoadRelationships","Load relationships",delegate{ResetRelationships();LoadRelationships();}),relationshipNext});detail.Controls.Add(rb,0,1);
            var dt=new TabControl{Dock=DockStyle.Fill};var prop=new TabPage("Collected properties");properties=Input("Properties",200);properties.Multiline=true;properties.ReadOnly=true;properties.ScrollBars=ScrollBars.Both;properties.WordWrap=false;properties.Dock=DockStyle.Fill;properties.Font=new Font("Consolas",10);prop.Controls.Add(properties);dt.TabPages.Add(prop);
            var rel=new TabPage("Relationships");var rp=Stack(3);Fixed(rp,34);Fill(rp);Fixed(rp,42);relationshipNote=Note("RelationshipNote","");rp.Controls.Add(relationshipNote,0,0);relationships=Grid("Relationships","From","Relationship","To");rp.Controls.Add(relationships,0,1);
            var ra=Bar();ra.Controls.AddRange(new Control[]{Button("ExplainRelationship","Explain & fixes",delegate{var e=Selected(relationships);if(e!=null)Explain(e);}),Button("GraphRelationship","Show in graph",delegate{var e=Selected(relationships);if(e!=null)ShowSingleEdge(e);}),Button("InspectOther","Inspect other object",delegate{var e=Selected(relationships);if(e!=null)Inspect(DesktopViewer.Text(e,direction.SelectedIndex==0?"s":"t"));})});rp.Controls.Add(ra,0,2);rel.Controls.Add(rp);dt.TabPages.Add(rel);detail.Controls.Add(dt,0,2);p.Controls.Add(detail,0,3);Page("Objects & relationships",p);
        }
        private void BuildPaths()
        {
            var p=Stack(6);Fixed(p,37);Fixed(p,37);Fixed(p,44);Fixed(p,44);p.RowStyles.Add(new RowStyle(SizeType.Percent,65));p.RowStyles.Add(new RowStyle(SizeType.Percent,35));
            var s=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};s.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));s.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));s.Controls.Add(Note("","Source ID"),0,0);sourceId=Input("SourceId",640);sourceId.Dock=DockStyle.Fill;s.Controls.Add(sourceId,1,0);p.Controls.Add(s,0,0);
            var t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));t.Controls.Add(Note("","Target ID"),0,0);targetId=Input("TargetId",640);targetId.Dock=DockStyle.Fill;t.Controls.Add(targetId,1,0);p.Controls.Add(t,0,1);
            var actions=Bar();visitLimit=Choice("VisitLimit","50000","100000","250000");visitLimit.Width=100;actions.Controls.AddRange(new Control[]{Button("FindPath","Find path",delegate{QueryPath(sourceId.Text.Trim(),targetId.Text.Trim());}),NoteInline("Visit budget",75),visitLimit});
            commands.Add(sourceId);commands.Add(targetId);commands.Add(visitLimit);
            continuePath=Button("ContinuePath","Continue path",Continue);continuePath.Enabled=false;actions.Controls.Add(continuePath);actions.Controls.Add(Button("ExportPlan","Export plan",ExportPlan));p.Controls.Add(actions,0,2);
            pathNote=Note("PathNote","No fixed hop cutoff. Queries have a visit budget and a 10-second deadline. Paths show recorded relationships, not confirmed exploitation.");p.Controls.Add(pathNote,0,3);
            var graphLayout=Stack(2);Fill(graphLayout);Fixed(graphLayout,50);graphLayout.Padding=new Padding(0);graph=new PathCanvas{Name="PathGraph",Dock=DockStyle.Fill};graph.NodeSelected+=NodeHelp;graph.EdgeSelected+=Explain;graphLayout.Controls.Add(graph,0,0);
            var tools=Bar();tools.Controls.AddRange(new Control[]{Button("ZoomOut","−",delegate{graph.Zoom(1/1.3f);}),Button("ZoomIn","+",delegate{graph.Zoom(1.3f);}),Button("FitGraph","Fit all",delegate{graph.Fit();}),Button("ReadPath","Read path",delegate{graph.FocusNode(0);}),Button("PreviousNode","Previous node",delegate{graph.FocusNode(graph.SelectedIndex-1);}),Button("NextNode","Next node",delegate{graph.FocusNode(graph.SelectedIndex+1);}),Button("InspectGraphNode","Inspect node",delegate{if(graph.SelectedNode!=null)Inspect(DesktopViewer.Text(graph.SelectedNode,"i"));})});graphLayout.Controls.Add(tools,0,1);p.Controls.Add(graphLayout,0,4);
            steps=Grid("PathSteps","From","Relationship","To");steps.CellEnter+=delegate(object sender,DataGridViewCellEventArgs args){if(args.RowIndex<0)return;var e=steps.Rows[args.RowIndex].Tag as Dictionary<string,object>;if(e!=null){Explain(e);graph.Highlight(e);}};p.Controls.Add(steps,0,5);Page("Paths & graph",p);
        }
        private static Label NoteInline(string text,int width){var n=Note("",text);n.Dock=DockStyle.None;n.Width=width;n.Height=34;return n;}
        private void SetStatus(string message,bool error){status.Text=message;status.ForeColor=error?Color.FromArgb(255,135,150):Muted;}
        private void SetHelp(string title,string body){helper.Text=title+"\r\n\r\n"+body;helper.Select(0,title.Length);helper.SelectionColor=Color.FromArgb(121,179,255);if(helperHeadingFont==null)helperHeadingFont=new Font(Font,FontStyle.Bold);helper.SelectionFont=helperHeadingFont;helper.Select(0,0);helper.ScrollToCaret();}
        private void Explain(Dictionary<string,object> edge){SetHelp("Relationship explanation & recommended fixes",DesktopViewer.Explain(edge));}
        private void NodeHelp(Dictionary<string,object> node){SetHelp(DesktopViewer.Text(node,"n"),DesktopViewer.Text(node,"t")+"\r\n"+DesktopViewer.Text(node,"i")+"\r\n\r\nThis identity appears in the displayed recorded path. Its presence alone does not flag it as unsafe. Use Inspect node for retained object properties and paged relationships.\r\n\r\nSelect a line or path step to understand the relationship, checks and recommended fix.");}
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
        private void UpdateButtons(){findPrevious.Enabled=!busy&&findingPage.History.Count>0;findNext.Enabled=!busy&&findingPage.More;objectPrevious.Enabled=!busy&&objectPage.History.Count>0;objectNext.Enabled=!busy&&objectPage.More;continuePath.Enabled=!busy&&path!=null&&DesktopViewer.Flag(path,"PathLimited");relationshipNext.Enabled=!busy&&selectedObject!=null&&relationshipCursor.ContainsKey("more")&&Convert.ToBoolean(relationshipCursor["more"]);}
        private void LoadOverview(){Run(delegate(GraphIndex db){return db.ViewerSummary();},delegate(object result){var r=DesktopViewer.Map(result);var state=(DiskAnalysisProgress)r["Analysis"];analysisStatus=state.Status=="NotStarted"?"No saved analysis":state.Status+(state.Status=="Completed"?"":" (incomplete)");long nodes=0,edges=0;foreach(var row in (List<string[]>)r["Nodes"])nodes+=long.Parse(row[1]);foreach(var row in (List<string[]>)r["Edges"])edges+=long.Parse(row[1]);summary.Text=nodes.ToString("N0")+" objects · "+edges.ToString("N0")+" relationships · "+state.Findings.ToString("N0")+" saved candidates · "+analysisStatus+" · No listening ports";findNote.Text=state.Status+": "+state.StopReason+". All saved candidates are available; incomplete analysis may have undiscovered candidates.";BeginInvoke(new Action(delegate{LoadPage(findingPage);}));});}
        private void LoadPage(DesktopPager pager)
        {
            var cursor=ViewerJson.Map("score",pager.Score,"sequence",pager.Sequence,"after",pager.After);string query=pager.Query;
            Run(delegate(GraphIndex db){Dictionary<string,object> r=null;int inspected=0;for(int window=0;window<20;window++){
                r=DesktopViewer.Map(pager.Findings?db.ViewerFindings((int)DesktopViewer.Number(cursor,"score"),DesktopViewer.Number(cursor,"sequence"),query):db.ViewerSearch(query,DesktopViewer.Text(cursor,"after")));inspected+=(int)DesktopViewer.Number(r,"Scanned");
                if(pager.Findings){cursor["score"]=r["AfterScore"];cursor["sequence"]=r["AfterSequence"];}else cursor["after"]=r["After"];
                if(DesktopViewer.Rows(r).Count>0||!DesktopViewer.Flag(r,"More"))break;
            }r["Inspected"]=inspected;return r;},delegate(object result){var r=DesktopViewer.Map(result);pager.More=DesktopViewer.Flag(r,"More");pager.NextCursor=cursor;
                var g=pager.Findings?findings:objects;g.Rows.Clear();foreach(object item in DesktopViewer.Rows(r)){var row=DesktopViewer.Map(item);if(pager.Findings)AddRow(g,row,DesktopViewer.Text(DesktopViewer.Map(row["Source"]),"n"),DesktopViewer.Text(DesktopViewer.Map(row["Target"]),"n"),row["HopCount"],row["Score"]);else AddRow(g,row,row["n"],row["t"],row["i"]);}
                (pager.Findings?findNote:objectNote).Text=(pager.Findings?analysisStatus+" · ":"")+DesktopViewer.Rows(r).Count+" matches · Page "+(pager.History.Count+1)+" · "+DesktopViewer.Number(r,"Inspected").ToString("N0")+" rows inspected. "+(pager.More?"More index data remains; choose Next.":"End of search in the remaining index.");
            });
        }
        private void OpenFinding(){var f=Selected(findings);if(f!=null)LoadRecorded(DesktopViewer.Text(DesktopViewer.Map(f["Source"]),"i"));}
        private void LoadRecorded(string id){Run(delegate(GraphIndex db){return db.ViewerRecordedPath(id);},delegate(object result){DisplayPath(DesktopViewer.Map(result),true);});}
        private void InspectSelected(){var n=Selected(objects);if(n!=null)Inspect(DesktopViewer.Text(n,"i"));}
        private void UseNode(Dictionary<string,object> node,bool source){if(node==null)return;(source?sourceId:targetId).Text=DesktopViewer.Text(node,"i");SetStatus((source?"Source: ":"Target: ")+DesktopViewer.Text(node,"n"),false);}
        private void Inspect(string id){Run(delegate(GraphIndex db){return db.ViewerObject(id);},delegate(object result){selectedObject=DesktopViewer.Map(result);identity.Text=DesktopViewer.Text(selectedObject,"n")+" · "+id;properties.Text=DesktopViewer.Flag(selectedObject,"Limited")?"Record exceeds the viewer's 1 MiB decompressed / 2 MiB stored property limit. The complete record remains in the index.":DesktopViewer.Text(selectedObject,"Record");if(properties.Text.Length==0)properties.Text="No collected object properties are available for this identity.";ResetRelationships();relationships.Rows.Clear();NodeHelp(selectedObject);tabs.SelectedIndex=1;BeginInvoke(new Action(LoadRelationships));});}
        private void ResetRelationships(){relationshipCursor=ViewerJson.Map("target","","bucket","","source","","type","");}
        private void LoadRelationships(){if(selectedObject==null)return;string id=DesktopViewer.Text(selectedObject,"i"),dir=direction.SelectedIndex==0?"in":"out";var c=new Dictionary<string,object>(relationshipCursor);
            Run(delegate(GraphIndex db){Dictionary<string,object> r=null;int scanned=0;for(int window=0;window<20;window++){r=DesktopViewer.Map(db.ViewerRelationships(id,dir,DesktopViewer.Text(c,"target"),DesktopViewer.Text(c,"bucket"),DesktopViewer.Text(c,"source"),DesktopViewer.Text(c,"type")));scanned+=(int)DesktopViewer.Number(r,"Scanned");c=ViewerJson.Map("target",r["Target"],"bucket",r["Bucket"],"source",r["Source"],"type",r["Type"],"more",r["More"]);if(DesktopViewer.Rows(r).Count>0||!DesktopViewer.Flag(r,"More"))break;}r["Inspected"]=scanned;return r;},delegate(object result){var r=DesktopViewer.Map(result);relationshipCursor=c;relationships.Rows.Clear();foreach(object item in DesktopViewer.Rows(r)){var e=DesktopViewer.Map(item);AddRow(relationships,e,e["sn"],e["ty"],e["tn"]);}relationshipNote.Text=DesktopViewer.Rows(r).Count+" relationships · "+DesktopViewer.Number(r,"Inspected").ToString("N0")+" rows inspected · "+(DesktopViewer.Flag(r,"More")?"More data remains.":"End of relationships.")+(dir=="out"?" Outgoing queries scan bounded disk windows.":"");});}
        private void QueryPath(string source,string target){if(source.Length==0||target.Length==0){SetStatus("Select or enter both source and target object IDs.",true);return;}int limit=int.Parse(visitLimit.Text);Run(delegate(GraphIndex db){return db.ViewerShortestPath(source,target,limit);},delegate(object result){var p=DesktopViewer.Map(result);string state=DesktopViewer.Text(p,"Status");if(state=="Found")DisplayPath(p,false);else{path=null;steps.Rows.Clear();graph.SetEdges(new List<object>());pathNote.Text=state=="VisitLimit"?"Visit budget reached; this result is incomplete. Increase the budget or choose a closer source/target.":"No path found through the supported resolved relationships. Collection gaps or other relationship types may hide routes.";SetHelp("Path search result",pathNote.Text);tabs.SelectedIndex=2;}});}
        private void Continue(){if(path==null||!DesktopViewer.Flag(path,"PathLimited"))return;string next=DesktopViewer.Text(path,"NextPathId");if(pathIsSaved)LoadRecorded(next);else QueryPath(next,DesktopViewer.Text(DesktopViewer.Map(path["Target"]),"i"));}
        private void ShowSingleEdge(Dictionary<string,object> edge){var p=ViewerJson.Map("Source",ViewerJson.Map("i",edge["s"],"n",edge["sn"]),"Target",ViewerJson.Map("i",edge["t"],"n",edge["tn"]),"Steps",new List<object>{edge},"HopCount",1,"PathLimited",false);DisplayPath(p,false);Explain(edge);}
        private void DisplayPath(Dictionary<string,object> value,bool saved){path=value;pathIsSaved=saved;steps.Rows.Clear();var edges=(List<object>)value["Steps"];foreach(object item in edges){var e=DesktopViewer.Map(item);AddRow(steps,e,e["sn"],e["ty"],e["tn"]);}graph.SetEdges(edges);tabs.SelectedIndex=2;graph.Fit();
            sourceId.Text=DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"i");targetId.Text=DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"i");
            pathNote.Text=DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"n")+" → "+DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"n")+" · "+DesktopViewer.Number(value,"HopCount")+" recorded hops · Showing "+edges.Count+" steps. "+(DesktopViewer.Flag(value,"PathLimited")?"Use Continue this path for the next segment.":"Complete chain from this source to target.");
            var b=new StringBuilder();b.AppendLine(DesktopViewer.Text(DesktopViewer.Map(value["Source"]),"n")+" → "+DesktopViewer.Text(DesktopViewer.Map(value["Target"]),"n"));
            b.AppendLine("\r\n"+(saved?"Why it is flagged\r\nThe saved chain contains a permission or access capability and reaches a recognized privileged group or domain. This is a review candidate; effective access requires validation.":"What this establishes\r\nThe index contains this chain of supported relationships. A custom target is not necessarily privileged. This does not establish effective access."));
            if(saved){b.AppendLine("\r\nTriage score: "+DesktopViewer.Text(value,"Score"));foreach(string reason in (List<string>)value["Reasons"])b.AppendLine("• "+reason);}
            b.AppendLine("\r\nSelect a graph line or path step for its explanation and recommended fix.\r\n\r\n"+DesktopViewer.ChangeProcess);SetHelp("Recorded path & recommended fixes",b.ToString());UpdateButtons();
        }
        private void ExportPlan(){if(path==null){SetStatus("Open a path before exporting its review plan.",true);return;}using(var dialog=new SaveFileDialog{Title="Export this path segment's review plan",Filter="Text review plan (*.txt)|*.txt",FileName="bhtest-review-plan.txt"}){if(dialog.ShowDialog(this)!=DialogResult.OK)return;var b=new StringBuilder("BHTest review plan — displayed path segment\r\n");b.AppendLine("Source: "+DesktopViewer.Text(DesktopViewer.Map(path["Source"]),"i"));b.AppendLine("Target: "+DesktopViewer.Text(DesktopViewer.Map(path["Target"]),"i"));b.AppendLine("Remaining chain hops: "+DesktopViewer.Text(path,"HopCount"));b.AppendLine("Segment limited: "+DesktopViewer.Flag(path,"PathLimited"));b.AppendLine("Continue from: "+DesktopViewer.Text(path,"NextPathId"));b.AppendLine("Recorded relationships require validation. Original coverage is not stored in this index.\r\n");foreach(object item in (List<object>)path["Steps"])b.AppendLine(DesktopViewer.Explain(DesktopViewer.Map(item))+"\r\n--------------------\r\n");File.WriteAllText(dialog.FileName,b.ToString(),new UTF8Encoding(true));SetStatus("Exported the displayed path segment's review plan.",false);}}
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

    public sealed class PathCanvas : Control
    {
        private List<Dictionary<string,object>> nodes=new List<Dictionary<string,object>>();
        private List<object> edges=new List<object>();private float scale=1,panX=40,panY=100;private Point down,last;private bool dragging,moved;private Dictionary<string,object> highlighted;
        public event Action<Dictionary<string,object>> NodeSelected,EdgeSelected;
        public int SelectedIndex{get;private set;}
        public Dictionary<string,object> SelectedNode{get{return nodes.Count==0?null:nodes[SelectedIndex];}}
        public PathCanvas(){DoubleBuffered=true;BackColor=Color.FromArgb(13,23,40);TabStop=true;Resize+=delegate{Invalidate();};}
        public void SetEdges(List<object> value){edges=value;nodes=new List<Dictionary<string,object>>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(object item in edges){var e=DesktopViewer.Map(item);foreach(string side in new string[]{"s","t"}){string id=DesktopViewer.Text(e,side);if(seen.Add(id))nodes.Add(ViewerJson.Map("i",id,"n",DesktopViewer.Text(e,side=="s"?"sn":"tn"),"t",DesktopViewer.Text(e,side=="s"?"st":"tt")));}}SelectedIndex=0;highlighted=null;Fit();}
        private PointF Position(int index){return new PointF(panX+index*230*scale,panY);}
        public void Fit(){scale=nodes.Count<2?1:Math.Min(1.15f,Math.Max(.025f,(ClientSize.Width-160f)/((nodes.Count-1)*230f)));panX=(ClientSize.Width-Math.Max(0,nodes.Count-1)*230*scale)/2;panY=Math.Max(65,ClientSize.Height/2f-15);Invalidate();}
        public void Zoom(float factor){ZoomAt(factor,new PointF(ClientSize.Width/2f,ClientSize.Height/2f));}
        private void ZoomAt(float factor,PointF anchor){float next=Math.Max(.025f,Math.Min(3,scale*factor)),ratio=next/scale;panX=anchor.X-(anchor.X-panX)*ratio;panY=anchor.Y-(anchor.Y-panY)*ratio;scale=next;Invalidate();}
        public void FocusNode(int index){if(nodes.Count==0)return;SelectedIndex=Math.Max(0,Math.Min(nodes.Count-1,index));scale=1;panX=ClientSize.Width/2f-SelectedIndex*230;panY=ClientSize.Height/2f-15;Invalidate();if(NodeSelected!=null)NodeSelected(SelectedNode);}
        public void Highlight(Dictionary<string,object> edge){highlighted=edge;Invalidate();}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var muted=new SolidBrush(Color.FromArgb(164,180,202)))g.DrawString(nodes.Count==0?"Open a path or relationship to explore the graph.":"Drag to pan · Mouse wheel or +/− to zoom · Read path for readable labels · "+nodes.Count+" nodes · "+Math.Round(scale*100)+"%",Font,muted,12,12);
            using(var ink=new SolidBrush(Color.FromArgb(237,243,252)))using(var normal=new Pen(Color.FromArgb(121,179,255),2))using(var selected=new Pen(Color.FromArgb(246,199,119),3))using(var arrow=new AdjustableArrowCap(4,5,true)){
                normal.CustomEndCap=arrow;selected.CustomEndCap=arrow;
                for(int i=0;i<edges.Count&&i+1<nodes.Count;i++){var a=Position(i);var b=Position(i+1);if(b.X<0||a.X>Width)continue;var edge=DesktopViewer.Map(edges[i]);var pen=ReferenceEquals(edge,highlighted)?selected:normal;if(b.X-a.X>24)g.DrawLine(pen,a.X+9,a.Y,b.X-13,b.Y);if(scale>=.5f)DrawLabel(g,DesktopViewer.Text(edge,"ty"),new PointF((a.X+b.X)/2,a.Y-27),ink,185);}
                for(int i=0;i<nodes.Count;i++){var p=Position(i);if(p.X<-160||p.X>Width+160)continue;string type=DesktopViewer.Text(nodes[i],"t");using(var brush=new SolidBrush(type=="users"?Color.FromArgb(121,179,255):type=="computers"?Color.FromArgb(119,217,187):Color.FromArgb(246,199,119)))g.FillEllipse(brush,p.X-7,p.Y-7,14,14);if(i==SelectedIndex)g.DrawEllipse(Pens.White,p.X-9,p.Y-9,18,18);if(scale>=.55f||i==SelectedIndex||i==nodes.Count-1)DrawLabel(g,DesktopViewer.Text(nodes[i],"n"),new PointF(p.X,p.Y+15),ink,190);}
            }
        }
        private void DrawLabel(Graphics g,string text,PointF center,Brush brush,int width){var rect=new RectangleF(center.X-width/2,center.Y,width,36);using(var format=new StringFormat{Alignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})g.DrawString(text,Font,brush,rect,format);}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;Focus();down=last=e.Location;dragging=true;moved=false;Capture=true;}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(!dragging)return;if(Math.Abs(e.X-down.X)+Math.Abs(e.Y-down.Y)>5)moved=true;panX+=e.X-last.X;panY+=e.Y-last.Y;last=e.Location;Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(!dragging)return;dragging=false;Capture=false;if(moved)return;for(int i=0;i<nodes.Count;i++){var p=Position(i);if(Math.Abs(e.X-p.X)<40&&Math.Abs(e.Y-p.Y)<35){SelectedIndex=i;Invalidate();if(NodeSelected!=null)NodeSelected(nodes[i]);return;}}for(int i=0;i<edges.Count&&i+1<nodes.Count;i++){var a=Position(i);var b=Position(i+1);if(e.X>a.X&&e.X<b.X&&Math.Abs(e.Y-a.Y)<35){highlighted=DesktopViewer.Map(edges[i]);Invalidate();if(EdgeSelected!=null)EdgeSelected(highlighted);return;}}}
        protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);ZoomAt(e.Delta>0?1.2f:1/1.2f,e.Location);}
        protected override bool IsInputKey(Keys keyData){return keyData==Keys.Left||keyData==Keys.Right||keyData==Keys.Up||keyData==Keys.Down||base.IsInputKey(keyData);}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Add||e.KeyCode==Keys.Oemplus)Zoom(1.3f);else if(e.KeyCode==Keys.Subtract||e.KeyCode==Keys.OemMinus)Zoom(1/1.3f);else if(e.KeyCode==Keys.D0)Fit();else if(e.KeyCode==Keys.Left)panX+=50;else if(e.KeyCode==Keys.Right)panX-=50;else if(e.KeyCode==Keys.Up)panY+=50;else if(e.KeyCode==Keys.Down)panY-=50;else return;e.Handled=true;e.SuppressKeyPress=true;Invalidate();}
    }
}
