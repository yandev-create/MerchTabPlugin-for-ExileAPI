using System;
using System.Collections.Generic;
using System.Globalization;

namespace MerchTabPlugin.Models;

public class MerchantTabRow
{
    public int Index { get; set; }
    public string Name { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}

public class MerchantItemInfo
{
    public string UiPath { get; set; }
    public string EntityPath { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }

    public string PriceRaw { get; set; }
    public int? PriceAmount { get; set; }
    public string PriceCurrency { get; set; }
    public string PriceNoteRaw { get; set; }

    public DateTime PricedAt { get; set; } = DateTime.MinValue;
}

public class MerchAllTabsDump
{
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
    public List<MerchKnownTab> KnownTabs { get; set; } = new List<MerchKnownTab>();
    public List<MerchTabDump> Tabs { get; set; } = new List<MerchTabDump>();
}

public class MerchKnownTab
{
    public int Index { get; set; }
    public string Name { get; set; }
    public bool Selected { get; set; }
}

public class MerchTabDump
{
    public int TabIndex { get; set; }
    public string TabName { get; set; }
    public DateTime LastScanned { get; set; } = DateTime.MinValue;
    public int ItemCount { get; set; }
    public List<MerchStoredItem> Items { get; set; } = new List<MerchStoredItem>();
}

public class MerchStoredItem
{
    public string UiPath { get; set; }
    public string EntityPath { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.MinValue;

    public string PriceRaw { get; set; }
    public int? PriceAmount { get; set; }
    public string PriceCurrency { get; set; }
    public string PriceNoteRaw { get; set; }
    public DateTime PricedAt { get; set; } = DateTime.MinValue;
}

public class HighlightBox
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}

public static class MerchantItemKey
{
    public static string Build(int tabIndex, string entityPath, float x, float y, float w, float h)
    {
        return tabIndex + "|" + (entityPath ?? "-") + "|" +
               Round2(x) + "|" + Round2(y) + "|" + Round2(w) + "|" + Round2(h);
    }

    public static string Round2(float value)
    {
        return Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
    }
}