# Adds "focus on one route" to the viewer graph: click a node and one route through it is highlighted and numbered,
# the rest fades, and the table lists that route. Prev route / Next route cycle alternatives. Esc or All relationships clears.
# Requires Patch-ViewerLayout.ps1 to have been applied first. Run from the folder that contains BHTest.Viewer.ps1.
$p = (Resolve-Path '.\BHTest.Viewer.ps1').Path
$text = [IO.File]::ReadAllText($p)
if ($text.Contains('RouteTracer.Trace(')) { 'Already patched. Nothing to do.'; return }
if (-not $text.Contains('GraphLayout.Layers(')) { throw 'Run Patch-ViewerLayout.ps1 first, then run this one. Nothing was changed.' }
$nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
function Count-Of($haystack, $needle) { ($haystack.Split([string[]]@($needle), [StringSplitOptions]::None).Count) - 1 }
$edits = @()
$edits += ,@(@'
private Point down,last;private bool dragging,moved,network,spread,dense;
'@, @'
private Point down,last;private bool dragging,moved,network,spread,dense;private readonly List<int> depthOf=new List<int>();private HashSet<int> routeSet,routeNodes;private Dictionary<int,string> routeLabels;private int routeNode,routeVariant;public event Action<List<object>> RouteSelected;
'@)
$edits += ,@(@'
public void SetEdges(List<object> value){spread=false;dense=false;network=false;
'@, @'
public void SetEdges(List<object> value){spread=false;dense=false;network=false;depthOf.Clear();ClearRoute();
'@)
$edits += ,@(@'
spread=false;network=true;edges=value;nodes=new List<Dictionary<string,object>>();nodeIndex.Clear();layout.Clear();
'@, @'
spread=false;network=true;edges=value;nodes=new List<Dictionary<string,object>>();nodeIndex.Clear();layout.Clear();depthOf.Clear();ClearRoute();
'@)
$edits += ,@(@'
nodeIndex.Add(DesktopViewer.Text(node,"i"),nodes.Count);nodes.Add(node);layout.Add(new PointF(0,0));
'@, @'
nodeIndex.Add(DesktopViewer.Text(node,"i"),nodes.Count);nodes.Add(node);depthOf.Add(layer.Key);layout.Add(new PointF(0,0));
'@)
$edits += ,@(@'
public void Highlight(Dictionary<string,object> edge){highlighted=edge;Invalidate();}
'@, @'
public void Highlight(Dictionary<string,object> edge){highlighted=edge;if(edge==null)ClearRoute();Invalidate();} public bool RouteActive{get{return routeSet!=null;}} public void TraceRoute(int node,int variant){if(!network||node<0||node>=nodes.Count||depthOf.Count!=nodes.Count)return;var source=new int[connections.Count];var target=new int[connections.Count];for(int i=0;i<connections.Count;i++){source[i]=connections[i].Source;target[i]=connections[i].Target;}int[] chain=RouteTracer.Trace(node,variant,depthOf.ToArray(),source,target);if(chain.Length==0){ClearRoute();return;}routeNode=node;routeVariant=Math.Max(0,variant);routeSet=new HashSet<int>(chain);routeNodes=new HashSet<int>();routeNodes.Add(node);routeLabels=new Dictionary<int,string>();var rows=new List<object>();foreach(int c in chain){routeNodes.Add(connections[c].Source);routeNodes.Add(connections[c].Target);routeLabels[c]=(rows.Count+1)+(connections[c].Edges.Count>1?"+":"");foreach(object edge in connections[c].Edges)rows.Add(edge);}Invalidate();if(RouteSelected!=null)RouteSelected(rows);} public void NextRoute(int delta){if(routeSet==null)return;TraceRoute(routeNode,routeVariant+delta);} public void ClearRoute(){routeSet=null;routeNodes=null;routeLabels=null;Invalidate();}
'@)
$edits += ,@(@'
SelectedIndex=i;highlighted=null;Invalidate();if(NodeSelected!=null)NodeSelected(nodes[i]);return;
'@, @'
SelectedIndex=i;highlighted=null;Invalidate();if(NodeSelected!=null)NodeSelected(nodes[i]);if(network)TraceRoute(i,0);return;
'@)
$edits += ,@(@'
else if(e.KeyCode==Keys.Down)panY-=50;else return;
'@, @'
else if(e.KeyCode==Keys.Down)panY-=50;else if(e.KeyCode==Keys.Escape&&routeSet!=null)ClearRoute();else return;
'@)
$edits += ,@(@'
bool active=IsHighlighted(c);if(active!=(pass==1))continue;bool expanded=active&&network&&c.Edges.Count>1;
'@, @'
bool onRoute=routeSet!=null&&routeSet.Contains(c.Index);bool picked=IsHighlighted(c);bool active=picked||onRoute;if(active!=(pass==1))continue;bool expanded=picked&&network&&c.Edges.Count>1;
'@)
$edits += ,@(@'
Color color=active?(exact?Color.FromArgb(255,207,123):Color.FromArgb(161,203,249)):highlighted!=null?Color.FromArgb(39,58,81):
'@, @'
Color color=onRoute&&!picked?Color.FromArgb(255,207,123):active?(exact?Color.FromArgb(255,207,123):Color.FromArgb(161,203,249)):routeSet!=null?Color.FromArgb(28,89,121,160):highlighted!=null?Color.FromArgb(39,58,81):
'@)
$edits += ,@(@'
new Pen(color,exact?3:active?2:dense?.9f:1.15f)
'@, @'
new Pen(color,exact?3:onRoute?2.6f:active?2:dense?.9f:1.15f)
'@)
$edits += ,@(@'
if(network){if(active){label=(lane+1).ToString();
'@, @'
if(network){if(onRoute&&!picked){label=routeLabels[c.Index];point=midpoint;}else if(active){label=(lane+1).ToString();
'@)
$edits += ,@(@'
using(var brush=new SolidBrush(type=="users"?Color.FromArgb(121,179,255):type=="computers"?Color.FromArgb(119,217,187):Color.FromArgb(246,199,119)))
'@, @'
Color fill=type=="users"?Color.FromArgb(121,179,255):type=="computers"?Color.FromArgb(119,217,187):Color.FromArgb(246,199,119);if(routeNodes!=null&&!routeNodes.Contains(i))fill=Color.FromArgb(70,fill);using(var brush=new SolidBrush(fill))
'@)
$edits += ,@(@'
if(scale>=.4f||nodes.Count<=18||i==SelectedIndex||endpoint){
'@, @'
if(routeNodes!=null?routeNodes.Contains(i):(scale>=.4f||nodes.Count<=18||i==SelectedIndex||endpoint)){
'@)
$edits += ,@(@'
graph.ConnectionSelected+=ShowConnection;
'@, @'
graph.ConnectionSelected+=ShowConnection;graph.RouteSelected+=ShowRoute;
'@)
$edits += ,@(@'
private void ShowTargetRows(List<object> rows){steps.Rows.Clear();
'@, @'
private void ShowRoute(List<object> rows){if(targetGraphEdges==null||rows.Count==0)return;ShowTargetRows(rows);SetConnectionLegend(new List<object>());var first=DesktopViewer.Map(rows[0]);var last=DesktopViewer.Map(rows[rows.Count-1]);connectionTitle.Text="Route: "+DesktopViewer.Text(first,"sn")+" \u2192 "+DesktopViewer.Text(last,"tn")+" \u00B7 "+rows.Count+" relationships. Prev route and Next route show other routes; All relationships clears.";}private void ShowTargetRows(List<object> rows){steps.Rows.Clear();
'@)
$edits += ,@(@'
Existing administrator relationships are included as context, not automatically new escalation findings.");}}));
'@, @'
Existing administrator relationships are included as context, not automatically new escalation findings.");}}));tools.Controls.Add(Button("PreviousRoute","Prev route",delegate{graph.NextRoute(-1);}));tools.Controls.Add(Button("NextRoute","Next route",delegate{graph.NextRoute(1);}));
'@)
$classBlock = @'
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
'@
$anchor = @'
    public sealed class PathCanvas : Control
'@
foreach ($e in $edits) {
    $n = Count-Of $text $e[0]
    if ($n -ne 1) { throw "Cannot patch: expected exactly 1 match but found $n for: $($e[0].Substring(0, [Math]::Min(70, $e[0].Length)))... Your BHTest.Viewer.ps1 is not the version this patch was written for. Nothing was changed." }
}
if ((Count-Of $text $anchor) -ne 1) { throw 'Cannot patch: the PathCanvas class was not found exactly once. Nothing was changed.' }
Copy-Item $p "$p.routes.bak" -Force
foreach ($e in $edits) { $text = $text.Replace($e[0], $e[1]) }
$block = $classBlock.Replace("`n", $nl)
$text = $text.Replace($anchor, $block + $nl + $nl + $anchor)
[IO.File]::WriteAllText($p, $text, (New-Object System.Text.UTF8Encoding($true)))
'Patched. Backup: ' + "$p.routes.bak"
'Open the viewer in a NEW PowerShell window.'
