using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace MerchTabPlugin;

public class MerchTabSettings : ISettings
{
    [JsonIgnore]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    public List<string> ScannedTabIndices { get; set; } = new List<string>();
    public Dictionary<int, string> TabNames { get; set; } = new Dictionary<int, string>();
    public Dictionary<int, bool> SelectedTabs { get; set; } = new Dictionary<int, bool>();

    public int HighlightOlderThanHours { get; set; } = 12;
}