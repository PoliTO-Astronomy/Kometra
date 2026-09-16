using System.Collections.Generic;

namespace Kometra.Models.Nodes;

public class GraphNodeModel : BaseNodeModel
{
    public List<string> ImagePaths { get; set; } = new();
    public string CsvPath { get; set; } = string.Empty;
}