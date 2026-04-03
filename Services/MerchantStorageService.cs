using System;
using System.Collections.Generic;
using System.IO;
using MerchTabPlugin.Models;
using Newtonsoft.Json;

namespace MerchTabPlugin.Services;

public class MerchantStorageService
{
    private readonly string _allTabsItemsPath;

    public MerchantStorageService(string allTabsItemsPath)
    {
        _allTabsItemsPath = allTabsItemsPath;
    }

    public MerchAllTabsDump LoadAllTabsFile()
    {
        try
        {
            if (!File.Exists(_allTabsItemsPath))
                return new MerchAllTabsDump();

            string json = File.ReadAllText(_allTabsItemsPath);
            if (string.IsNullOrWhiteSpace(json))
                return new MerchAllTabsDump();

            MerchAllTabsDump loaded = JsonConvert.DeserializeObject<MerchAllTabsDump>(json);
            if (loaded == null)
                return new MerchAllTabsDump();

            if (loaded.Tabs == null)
                loaded.Tabs = new List<MerchTabDump>();
            if (loaded.KnownTabs == null)
                loaded.KnownTabs = new List<MerchKnownTab>();

            for (int i = 0; i < loaded.Tabs.Count; i++)
            {
                if (loaded.Tabs[i].Items == null)
                    loaded.Tabs[i].Items = new List<MerchStoredItem>();
            }

            return loaded;
        }
        catch
        {
            return new MerchAllTabsDump();
        }
    }

    public void SaveAllTabsFile(MerchAllTabsDump allTabs)
    {
        File.WriteAllText(_allTabsItemsPath, JsonConvert.SerializeObject(allTabs, Formatting.Indented));
    }

    public string BuildTabSummaryText(int tabIndex)
    {
        try
        {
            MerchAllTabsDump allTabs = LoadAllTabsFile();
            if (allTabs == null || allTabs.Tabs == null)
                return "(0 items)";

            MerchTabDump tab = null;
            for (int i = 0; i < allTabs.Tabs.Count; i++)
            {
                if (allTabs.Tabs[i].TabIndex == tabIndex)
                {
                    tab = allTabs.Tabs[i];
                    break;
                }
            }

            if (tab == null || tab.Items == null || tab.Items.Count == 0)
                return "(0 items)";

            int itemCount = tab.Items.Count;
            decimal divineTotal = 0m;
            decimal chaosTotal = 0m;

            for (int i = 0; i < tab.Items.Count; i++)
            {
                MerchStoredItem item = tab.Items[i];
                if (!item.PriceAmount.HasValue || string.IsNullOrWhiteSpace(item.PriceCurrency))
                    continue;

                decimal amount = item.PriceAmount.Value;
                string currency = NormalizeCurrency(item.PriceCurrency);

                if (currency == "divine")
                    divineTotal += amount;
                else if (currency == "chaos")
                    chaosTotal += amount;
            }

            string valueText = FormatValueText(divineTotal, chaosTotal);
            return "(" + itemCount + " items, " + valueText + ")";
        }
        catch
        {
            return "(?)";
        }
    }

    public string BuildSelectedTabsTotalSummaryText(Dictionary<int, bool> selectedTabs)
    {
        try
        {
            MerchAllTabsDump allTabs = LoadAllTabsFile();
            if (allTabs == null || allTabs.Tabs == null)
                return "Total value of all selected stashes: 0D, 0C";

            decimal divineTotal = 0m;
            decimal chaosTotal = 0m;

            for (int i = 0; i < allTabs.Tabs.Count; i++)
            {
                MerchTabDump tab = allTabs.Tabs[i];
                bool selected = selectedTabs.ContainsKey(tab.TabIndex) && selectedTabs[tab.TabIndex];
                if (!selected || tab.Items == null)
                    continue;

                for (int j = 0; j < tab.Items.Count; j++)
                {
                    MerchStoredItem item = tab.Items[j];
                    if (!item.PriceAmount.HasValue || string.IsNullOrWhiteSpace(item.PriceCurrency))
                        continue;

                    decimal amount = item.PriceAmount.Value;
                    string currency = NormalizeCurrency(item.PriceCurrency);

                    if (currency == "divine")
                        divineTotal += amount;
                    else if (currency == "chaos")
                        chaosTotal += amount;
                }
            }

            return "Total value of all selected stashes: " +
                   (int)Math.Floor(divineTotal) + "D, " +
                   (int)Math.Floor(chaosTotal) + "C";
        }
        catch
        {
            return "Total value of all selected stashes: ?";
        }
    }

    private string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return "";

        string c = currency.Trim().ToLowerInvariant();

        if (c.Contains("divine"))
            return "divine";

        if (c.Contains("chaos"))
            return "chaos";

        return c;
    }

    private string FormatValueText(decimal divineTotal, decimal chaosTotal)
    {
        int divineWhole = (int)Math.Floor(divineTotal);
        int chaosWhole = (int)Math.Floor(chaosTotal);

        if (divineWhole <= 0 && chaosWhole <= 0)
            return "no prices";

        if (divineWhole > 0 && chaosWhole > 0)
            return divineWhole + "D " + chaosWhole + "C";

        if (divineWhole > 0)
            return divineWhole + "D";

        return chaosWhole + "C";
    }
}