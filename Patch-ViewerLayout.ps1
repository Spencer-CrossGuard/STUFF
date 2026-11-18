# Improves the viewer graph layout: crowded layers wrap into columns so the graph uses the width of the window.
# Run from the folder that contains BHTest.Viewer.ps1. A backup is written first. Safe to run once.
$p = (Resolve-Path '.\BHTest.Viewer.ps1').Path
$text = [IO.File]::ReadAllText($p)
if ($text.Contains('GraphLayout.Layers(')) { 'Already patched. Nothing to do.'; return }
$nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
function Count-Of($haystack, $needle) { ($haystack.Split([string[]]@($needle), [StringSplitOptions]::None).Count) - 1 }
$edits = @()
$edits += ,@(@'
private Point down,last;private bool dragging,moved,network,spread;
'@.Replace("`r`n","`n"), @'
private Point down,last;private bool dragging,moved,network,spread,dense;
'@.Replace("`r`n","`n"))
$edits += ,@(@'
public void SetEdges(List<object> value){spread=false;network=false;
'@.Replace("`r`n","`n"), @'
public void SetEdges(List<object> value){spread=false;dense=false;network=false;
'@.Replace("`r`n","`n"))
$edits += ,@(@'
public void SetNetwork(List<object> value,List<object> objects,bool spreadOut){spread=spreadOut;network=true;edges=value;nodes=new List<Dictionary<string,object>>();nodeIndex.Clear();layout.Clear();var layers=new SortedDictionary<int,List<Dictionary<string,object>>>();int maxDepth=0;foreach(object item in objects){var node=DesktopViewer.Map(item);int depth=(int)DesktopViewer.Number(node,"Depth");maxDepth=Math.Max(maxDepth,depth);if(!layers.ContainsKey(depth))layers.Add(depth,new List<Dictionary<string,object>>());layers[depth].Add(node);}foreach(var layer in layers){layer.Value.Sort(delegate(Dictionary<string,object> a,Dictionary<string,object> b){return StringComparer.OrdinalIgnoreCase.Compare(DesktopViewer.Text(a,"n")+DesktopViewer.Text(a,"i"),DesktopViewer.Text(b,"n")+DesktopViewer.Text(b,"i"));});for(int row=0;row<layer.Value.Count;row++){var node=layer.Value[row];nodeIndex.Add(DesktopViewer.Text(node,"i"),nodes.Count);nodes.Add(node);layout.Add(new PointF((maxDepth-layer.Key)*340,(row-(layer.Value.Count-1)/2f)*140));}}if(spread&&nodes.Count>1){float radius=Math.Max(260,nodes.Count*32);for(int i=0;i<nodes.Count;i++){double angle=2*Math.PI*i/nodes.Count;layout[i]=new PointF((float)Math.Cos(angle)*radius*Math.Max(1.25f,(ClientSize.Width-430f)/Math.Max(80,ClientSize.Height-110f)),(float)Math.Sin(angle)*radius);}}BuildConnections();}
'@.Replace("`r`n","`n"), @'
public void SetNetwork(List<object> value,List<object> objects,bool spreadOut){spread=false;network=true;edges=value;nodes=new List<Dictionary<string,object>>();nodeIndex.Clear();layout.Clear();var layers=new SortedDictionary<int,List<Dictionary<string,object>>>();foreach(object item in objects){var node=DesktopViewer.Map(item);int depth=(int)DesktopViewer.Number(node,"Depth");if(!layers.ContainsKey(depth))layers.Add(depth,new List<Dictionary<string,object>>());layers[depth].Add(node);}var sizes=new List<int>();foreach(var layer in layers){layer.Value.Sort(delegate(Dictionary<string,object> a,Dictionary<string,object> b){return StringComparer.OrdinalIgnoreCase.Compare(DesktopViewer.Text(a,"n")+DesktopViewer.Text(a,"i"),DesktopViewer.Text(b,"n")+DesktopViewer.Text(b,"i"));});sizes.Add(layer.Value.Count);for(int row=0;row<layer.Value.Count;row++){var node=layer.Value[row];nodeIndex.Add(DesktopViewer.Text(node,"i"),nodes.Count);nodes.Add(node);layout.Add(new PointF(0,0));}}float[] xs,ys;GraphLayout.Layers(sizes.ToArray(),ClientSize.Width,ClientSize.Height,out xs,out ys);for(int i=0;i<layout.Count;i++)layout[i]=new PointF(xs[i],ys[i]);BuildConnections();dense=connections.Count>90;Invalidate();}
'@.Replace("`r`n","`n"))
$edits += ,@(@'
Color.FromArgb(39,58,81):Color.FromArgb(89,121,160);
'@.Replace("`r`n","`n"), @'
Color.FromArgb(39,58,81):(dense?Color.FromArgb(90,89,121,160):Color.FromArgb(89,121,160));
'@.Replace("`r`n","`n"))
$edits += ,@(@'
new Pen(color,exact?3:active?2:1.15f)
'@.Replace("`r`n","`n"), @'
new Pen(color,exact?3:active?2:dense?.9f:1.15f)
'@.Replace("`r`n","`n"))
$layoutBlock = @'
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
'@
$anchor = @'
    public sealed class PathCanvas : Control
'@
foreach ($e in $edits) {
    $n = Count-Of $text $e[0]
    if ($n -ne 1) { throw "Cannot patch: expected exactly 1 match but found $n for: $($e[0].Substring(0, [Math]::Min(70, $e[0].Length)))... Your BHTest.Viewer.ps1 is not the version this patch was written for. Nothing was changed." }
}
if ((Count-Of $text $anchor) -ne 1) { throw 'Cannot patch: the PathCanvas class was not found exactly once. Nothing was changed.' }
Copy-Item $p "$p.layout.bak" -Force
foreach ($e in $edits) { $text = $text.Replace($e[0], $e[1]) }
$block = $layoutBlock.Replace("`n", $nl)
$text = $text.Replace($anchor, $block + $nl + $nl + $anchor)
[IO.File]::WriteAllText($p, $text, (New-Object System.Text.UTF8Encoding($true)))
'Patched. Backup: ' + "$p.layout.bak"
'Open the viewer in a NEW PowerShell window.'
