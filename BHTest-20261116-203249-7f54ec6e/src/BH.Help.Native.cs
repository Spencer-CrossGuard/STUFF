using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BH.Native
{
    public sealed class HelpEntry
    {
        public string Key,Category,Title,Summary,Body,Terms;
        public string[] Fields,Controls,Kinds;
        public HelpEntry(string key,string category,string title,string summary,string body,string terms,string[] fields,string[] controls,string[] kinds)
        {Key=key;Category=category;Title=title;Summary=summary;Body=body;Terms=terms;Fields=fields;Controls=controls;Kinds=kinds;}
        public override string ToString(){return Category+" · "+Title;}
        internal bool Matches(string query)
        {
            string haystack=Key+" "+Category+" "+Title+" "+Summary+" "+Body+" "+Terms+" "+string.Join(" ",Fields);
            foreach(string word in query.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries))if(haystack.IndexOf(word,StringComparison.OrdinalIgnoreCase)<0)return false;
            return true;
        }
    }
    public static class DesktopHelpCatalog
    {
        public static readonly Dictionary<string,HelpEntry> Topics=CreateTopics();
        private static Dictionary<string,HelpEntry> CreateTopics()
        {
            var topics=new Dictionary<string,HelpEntry>(StringComparer.OrdinalIgnoreCase);
            /*__DESKTOP_TOPICS__*/
            return topics;
        }
        public static HelpEntry ForField(string name){foreach(var item in Topics.Values)foreach(string value in item.Fields)if(string.Equals(name,value,StringComparison.OrdinalIgnoreCase))return item;return null;}
        public static HelpEntry ForKind(string name){foreach(var item in Topics.Values)foreach(string value in item.Kinds)if(string.Equals(name,value,StringComparison.OrdinalIgnoreCase))return item;return null;}
        public static HelpEntry ForControl(string name){foreach(var item in Topics.Values)foreach(string value in item.Controls)if(string.Equals(name,value,StringComparison.OrdinalIgnoreCase))return item;return null;}
        public static string Describe(HelpEntry topic)
        {return topic==null?"This property has no built-in interpretation. Read the original source and collection notes; do not infer effective access from an unfamiliar value.":topic.Summary+"\r\n\r\n"+topic.Body;}
        public static List<HelpEntry> Search(string query)
        {
            query=(query??"").Trim();var result=new List<HelpEntry>();HelpEntry exact;
            if(Topics.TryGetValue(query,out exact)){result.Add(exact);return result;}
            exact=ForField(query);if(exact!=null){result.Add(exact);return result;}
            foreach(var topic in Topics.Values)if(topic.Matches(query))result.Add(topic);
            foreach(var pair in DesktopViewer.Guide)
            {
                string body=DesktopViewer.Explain(ViewerJson.Map("ty",pair.Key,"sn","Source principal","tn","Target object"));
                var topic=new HelpEntry("relationship:"+pair.Key,"Relationships",pair.Key+" — "+pair.Value[0],pair.Value[1],
                    "General reference: this help entry is not evidence that the relationship was collected or that a path exists.\r\n\r\n"+body,"",new string[0],new string[0],new string[0]);
                if(topic.Matches(query))result.Add(topic);
            }
            result.Sort(delegate(HelpEntry a,HelpEntry b){if(a.Key=="start")return b.Key=="start"?0:-1;if(b.Key=="start")return 1;int order=string.Compare(a.Category,b.Category,StringComparison.OrdinalIgnoreCase);return order!=0?order:string.Compare(a.Title,b.Title,StringComparison.OrdinalIgnoreCase);});
            return result;
        }
    }
    public sealed class HelpLibraryWindow : Form
    {
        private readonly TextBox search;
        private readonly ListBox choices;
        private readonly RichTextBox content;
        private readonly Label count;
        public HelpLibraryWindow(string initialQuery)
        {
            Text="Tessera help & glossary | offline";Name="HelpLibraryWindow";Size=new Size(1050,720);MinimumSize=new Size(800,540);StartPosition=FormStartPosition.CenterParent;
            Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(12,18,32);ForeColor=Color.FromArgb(237,243,252);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12)};
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(layout);
            search=new TextBox{Name="HelpSearch",Dock=DockStyle.Fill,BackColor=Color.FromArgb(19,30,48),ForeColor=ForeColor};search.AccessibleName="Search help topics, fields or relationships";layout.Controls.Add(search,0,0);
            count=new Label{Name="HelpCount",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(164,180,202),TextAlign=ContentAlignment.MiddleLeft};layout.Controls.Add(count,0,1);
            var split=new SplitContainer{Dock=DockStyle.Fill,Size=new Size(1000,600),SplitterDistance=350,Panel1MinSize=230,Panel2MinSize=370,SplitterWidth=8};layout.Controls.Add(split,0,2);
            choices=new ListBox{Name="HelpTopics",Dock=DockStyle.Fill,BackColor=Color.FromArgb(19,30,48),ForeColor=ForeColor,BorderStyle=BorderStyle.None,HorizontalScrollbar=true,IntegralHeight=false};split.Panel1.Controls.Add(choices);
            content=new RichTextBox{Name="HelpTopicText",Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.FromArgb(19,30,48),ForeColor=ForeColor,BorderStyle=BorderStyle.None,DetectUrls=false};split.Panel2.Controls.Add(content);
            search.TextChanged+=delegate{UpdateTopics();};choices.SelectedIndexChanged+=delegate{var item=choices.SelectedItem as HelpEntry;content.Text=item==null?"Choose a topic to read its meaning, checks and limitations.":item.Title+"\r\n\r\n"+DesktopHelpCatalog.Describe(item);content.Select(0,0);content.ScrollToCaret();};
            KeyPreview=true;KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Escape){Close();e.SuppressKeyPress=true;}else if(e.Control&&e.KeyCode==Keys.F){search.Focus();search.SelectAll();e.SuppressKeyPress=true;}};
            search.Text=initialQuery??"";UpdateTopics();Shown+=delegate{search.Focus();search.SelectAll();};
        }
        private void UpdateTopics()
        {
            choices.BeginUpdate();try{choices.Items.Clear();foreach(var topic in DesktopHelpCatalog.Search(search.Text))choices.Items.Add(topic);}finally{choices.EndUpdate();}
            count.Text=choices.Items.Count+" matching entries · Ctrl+F to search · Escape to close";
            if(choices.Items.Count>0)choices.SelectedIndex=0;else content.Text="No matching help. Try a relationship label, property name such as SecurityEnabled, or a task such as routes or import. No behavior is inferred from an unfamiliar label.";
        }
    }
}
