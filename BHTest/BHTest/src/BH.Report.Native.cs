// Graph query result types and streaming CSV export.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BH.Native
{
    public struct GraphEdge
    {
        public string Source;
        public string Target;
        public string EdgeType;
    }

    public sealed class EgoGraphResult
    {
        public List<string> NodeIds = new List<string>();
        public List<GraphEdge> Edges = new List<GraphEdge>();
        public bool Truncated;
    }

    public static class CsvExporter
    {
        public static void Export(string[] ndjsonFiles, string[] fields, string outputPath)
        {
            using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(string.Join(",", fields));
                var sb = new StringBuilder(256);
                foreach (var file in ndjsonFiles)
                {
                    foreach (string line in NdjsonReader.Lines(file))
                    {

                            if (line.Length == 0) continue;
                            sb.Clear();
                            for (int i = 0; i < fields.Length; i++)
                            {
                                if (i > 0) sb.Append(',');
                                string v = JsonRecordReader.ExtractField(line, fields[i]);
                                if (v != null)
                                {
                                    sb.Append('"');
                                    if (v.Length > 0 && "=+-@\t\r\n".IndexOf(v[0]) >= 0) sb.Append('\'');
                                    sb.Append(v.Replace("\"", "\"\""));
                                    sb.Append('"');
                                }
                            }
                            writer.WriteLine(sb.ToString());
                    }
                }
            }
        }
    }
}
