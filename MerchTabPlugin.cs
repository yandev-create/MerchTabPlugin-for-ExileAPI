using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using Newtonsoft.Json;
using SharpDX;
using System.Windows.Forms;

namespace MerchTabPlugin;

public class MerchTabPlugin : BaseSettingsPlugin<MerchTabSettings>
{
    private string TabIndexPath => Path.Combine(DirectoryFullName, "merchant_tab_indices.json");
    private string ActiveTabItemsPath => Path.Combine(DirectoryFullName, "merchant_active_tab_items.json");
    private string AllTabsItemsPath => Path.Combine(DirectoryFullName, "merchant_all_tabs_items.json");

    private static readonly int[] MerchantTabDropdownPath = { 44, 2, 0, 0, 1, 1, 0, 0, 1, 4, 2 };
    private static readonly int[] MerchantTabPagesPath = { 44, 2, 0, 0, 1, 1, 0, 0, 1, 1 };

    private bool _isRunningGetItems = false;
    private bool _isRunningAddNewItems = false;
    private bool _isRunningUpdateItems = false;
    private string _lastStatus = "";

    private bool _highlightEnabled = false;
    private int _highlightHours = 12;
    private int _highlightTabIndex = -1;
    private readonly List<HighlightBox> _highlightBoxes = new List<HighlightBox>();

    public override bool Initialise()
    {
        Name = "MerchTabPlugin";
        return true;
    }

    private void ScanMerchantTabs()
    {
        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("SCAN: IngameUi is null");
                return;
            }

            dynamic dropdown = GetElementByPath(ingameUi, MerchantTabDropdownPath);
            if (dropdown == null)
            {
                Log("SCAN: Merchant tab dropdown not found at known path.");
                Log("SCAN: Open Faustus -> Merchant and expand the right-side dropdown first.");
                return;
            }

            List<MerchantTabRow> rows = GetMerchantTabRowsFromKnownDropdown(dropdown);
            if (rows.Count == 0)
            {
                Log("SCAN: No merchant tab rows found.");
                return;
            }

            Settings.ScannedTabIndices.Clear();

            var exportRows = new List<object>();
            for (int i = 0; i < rows.Count; i++)
            {
                MerchantTabRow row = rows[i];

                Settings.ScannedTabIndices.Add(i.ToString());

                if (!string.IsNullOrWhiteSpace(row.Name) && row.Name != "-")
                {
                    if (!Settings.TabNames.ContainsKey(i) || Settings.TabNames[i] != row.Name)
                        Settings.TabNames[i] = row.Name;
                }

                if (!Settings.SelectedTabs.ContainsKey(i))
                    Settings.SelectedTabs[i] = true;

                exportRows.Add(new
                {
                    Index = i,
                    Name = CleanupTabName(row.Name),
                    X = row.X,
                    Y = row.Y,
                    Width = row.W,
                    Height = row.H,
                    Selected = Settings.SelectedTabs[i]
                });
            }

            File.WriteAllText(TabIndexPath, JsonConvert.SerializeObject(new
            {
                ScannedAt = DateTime.Now,
                Count = rows.Count,
                Tabs = exportRows
            }, Formatting.Indented));

            Log("SCAN: Found " + rows.Count + " merchant tabs");
            _lastStatus = "Found " + rows.Count + " merchant tabs";
        }
        catch (Exception ex)
        {
            Log("SCAN ERROR: " + ex.Message);
            _lastStatus = "Scan error: " + ex.Message;
        }
    }

    private async Task GetItemsFromSelectedTabs(bool withPrices)
    {
        if (_isRunningGetItems)
            return;

        _isRunningGetItems = true;
        _lastStatus = withPrices ? "Saving items with price..." : "Saving items without price...";
        Log(withPrices ? "GET ITEMS+PRICES: started" : "GET ITEMS: started");

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("GET ITEMS: IngameUi is null");
                return;
            }

            dynamic dropdown = GetElementByPath(ingameUi, MerchantTabDropdownPath);
            dynamic pages = GetElementByPath(ingameUi, MerchantTabPagesPath);

            if (dropdown == null || pages == null)
            {
                Log("GET ITEMS: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = GetMerchantTabRowsFromKnownDropdown(dropdown);
            if (tabRows.Count == 0)
            {
                Log("GET ITEMS: No tab rows found.");
                return;
            }

            for (int i = 0; i < tabRows.Count; i++)
            {
                bool selected = Settings.SelectedTabs.ContainsKey(i) && Settings.SelectedTabs[i];
                if (!selected)
                    continue;

                _lastStatus = "Opening tab [" + i + "] " + CleanupTabName(tabRows[i].Name);

                bool opened = await OpenMerchantTabByIndex(i);
                if (!opened)
                {
                    Log("GET ITEMS: failed to open tab [" + i + "]");
                    continue;
                }

                await Task.Delay(350);
                await DumpCurrentlyActiveMerchantTabItemsInternal(withPrices);
                await Task.Delay(150);
            }

            _lastStatus = withPrices ? "Save items with price complete" : "Save items without price complete";
        }
        catch (Exception ex)
        {
            Log("GET ITEMS ERROR: " + ex.Message);
            _lastStatus = "Error: " + ex.Message;
        }
        finally
        {
            _isRunningGetItems = false;
        }
    }

    private async Task AddNewItemsToCurrentActiveTab()
    {
        if (_isRunningAddNewItems)
            return;

        _isRunningAddNewItems = true;
        _lastStatus = "Checking current tab for new items...";
        Log("ADD NEW ITEMS: started");

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("ADD NEW ITEMS: IngameUi is null");
                return;
            }

            dynamic dropdown = GetElementByPath(ingameUi, MerchantTabDropdownPath);
            dynamic pages = GetElementByPath(ingameUi, MerchantTabPagesPath);

            if (dropdown == null || pages == null)
            {
                Log("ADD NEW ITEMS: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = GetMerchantTabRowsFromKnownDropdown(dropdown);
            int activePageIndex = GetActivePageIndex(pages);

            if (activePageIndex < 0)
            {
                Log("ADD NEW ITEMS: Could not find active merchant tab page.");
                return;
            }

            dynamic activePage = null;
            try { activePage = pages.GetChildAtIndex(activePageIndex); } catch { }

            if (activePage == null)
            {
                Log("ADD NEW ITEMS: Active page element is null.");
                return;
            }

            var currentItems = new List<MerchantItemInfo>();
            CollectInventoryItems(activePage, "activePage/" + activePageIndex, currentItems);

            string activeTabName = "Unknown";
            if (activePageIndex >= 0 && activePageIndex < tabRows.Count)
                activeTabName = CleanupTabName(tabRows[activePageIndex].Name);

            if (!string.IsNullOrWhiteSpace(activeTabName) && activeTabName != "-" && activeTabName != "Unknown")
            {
                if (!Settings.TabNames.ContainsKey(activePageIndex) || Settings.TabNames[activePageIndex] != activeTabName)
                    Settings.TabNames[activePageIndex] = activeTabName;
            }

            MerchAllTabsDump allTabs = LoadAllTabsFile();
            allTabs.LastUpdated = DateTime.Now;

            if (allTabs.KnownTabs == null)
                allTabs.KnownTabs = new List<MerchKnownTab>();

            allTabs.KnownTabs.Clear();
            for (int i = 0; i < Settings.ScannedTabIndices.Count; i++)
            {
                int idx;
                if (!int.TryParse(Settings.ScannedTabIndices[i], out idx))
                    continue;

                string name = "(unknown)";
                if (Settings.TabNames.ContainsKey(idx))
                    name = CleanupTabName(Settings.TabNames[idx]);

                bool selected = Settings.SelectedTabs.ContainsKey(idx) && Settings.SelectedTabs[idx];

                allTabs.KnownTabs.Add(new MerchKnownTab
                {
                    Index = idx,
                    Name = name,
                    Selected = selected
                });
            }

            MerchTabDump targetTab = null;
            for (int i = 0; i < allTabs.Tabs.Count; i++)
            {
                if (allTabs.Tabs[i].TabIndex == activePageIndex)
                {
                    targetTab = allTabs.Tabs[i];
                    break;
                }
            }

            if (targetTab == null)
            {
                targetTab = new MerchTabDump
                {
                    TabIndex = activePageIndex,
                    TabName = activeTabName
                };
                allTabs.Tabs.Add(targetTab);
            }

            if (targetTab.Items == null)
                targetTab.Items = new List<MerchStoredItem>();

            var existingKeys = new HashSet<string>();
            for (int i = 0; i < targetTab.Items.Count; i++)
            {
                var oldItem = targetTab.Items[i];
                string key = BuildItemKey(activePageIndex, oldItem.EntityPath, oldItem.X, oldItem.Y, oldItem.W, oldItem.H);
                existingKeys.Add(key);
            }

            var newItems = new List<MerchantItemInfo>();
            for (int i = 0; i < currentItems.Count; i++)
            {
                var item = currentItems[i];
                string key = BuildItemKey(activePageIndex, item.EntityPath, item.X, item.Y, item.W, item.H);

                if (!existingKeys.Contains(key))
                    newItems.Add(item);
            }

            if (newItems.Count == 0)
            {
                _lastStatus = "No new items found in current tab";
                Log("ADD NEW ITEMS: no new items found");
                return;
            }

            _lastStatus = "Found " + newItems.Count + " new items, scanning prices...";
            Log("ADD NEW ITEMS: found " + newItems.Count + " new items");

            await PopulatePricesForCurrentTabItems(newItems);

            DateTime now = DateTime.Now;

            for (int i = 0; i < newItems.Count; i++)
            {
                MerchantItemInfo src = newItems[i];

                targetTab.Items.Add(new MerchStoredItem
                {
                    UiPath = src.UiPath,
                    EntityPath = src.EntityPath,
                    X = src.X,
                    Y = src.Y,
                    W = src.W,
                    H = src.H,
                    FirstSeenAt = now,
                    PriceRaw = src.PriceRaw,
                    PriceAmount = src.PriceAmount,
                    PriceCurrency = src.PriceCurrency,
                    PriceScannedAt = src.PriceScannedAt,
                    PriceNoteRaw = src.PriceNoteRaw
                });
            }

            targetTab.TabName = activeTabName;
            targetTab.LastScanned = now;
            targetTab.ItemCount = currentItems.Count;

            allTabs.Tabs.Sort(delegate (MerchTabDump a, MerchTabDump b)
            {
                return a.TabIndex.CompareTo(b.TabIndex);
            });

            allTabs.KnownTabs.Sort(delegate (MerchKnownTab a, MerchKnownTab b)
            {
                return a.Index.CompareTo(b.Index);
            });

            File.WriteAllText(AllTabsItemsPath, JsonConvert.SerializeObject(allTabs, Formatting.Indented));

            var exportItems = new List<object>();
            for (int i = 0; i < currentItems.Count; i++)
            {
                MerchantItemInfo item = currentItems[i];
                exportItems.Add(new
                {
                    item.UiPath,
                    item.EntityPath,
                    item.X,
                    item.Y,
                    item.W,
                    item.H,
                    item.PriceRaw,
                    item.PriceAmount,
                    item.PriceCurrency,
                    item.PriceScannedAt,
                    item.PriceNoteRaw
                });
            }

            File.WriteAllText(ActiveTabItemsPath, JsonConvert.SerializeObject(new
            {
                DumpedAt = DateTime.Now,
                ActiveTabIndex = activePageIndex,
                ActiveTabName = activeTabName,
                ItemCount = currentItems.Count,
                Items = exportItems
            }, Formatting.Indented));

            _lastStatus = "Added " + newItems.Count + " new items to [" + activePageIndex + "] " + activeTabName;
            Log("ADD NEW ITEMS: added " + newItems.Count + " new items");
        }
        catch (Exception ex)
        {
            Log("ADD NEW ITEMS ERROR: " + ex.Message);
            _lastStatus = "Add new items error: " + ex.Message;
        }
        finally
        {
            _isRunningAddNewItems = false;
        }
    }

    private async Task UpdateItemsFromSelectedTabs()
    {
        if (_isRunningUpdateItems)
            return;

        _isRunningUpdateItems = true;
        _lastStatus = "Updating items in selected tabs...";
        Log("UPDATE ITEMS: started");

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("UPDATE ITEMS: IngameUi is null");
                return;
            }

            dynamic dropdown = GetElementByPath(ingameUi, MerchantTabDropdownPath);
            dynamic pages = GetElementByPath(ingameUi, MerchantTabPagesPath);

            if (dropdown == null || pages == null)
            {
                Log("UPDATE ITEMS: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = GetMerchantTabRowsFromKnownDropdown(dropdown);
            if (tabRows.Count == 0)
            {
                Log("UPDATE ITEMS: No tab rows found.");
                return;
            }

            MerchAllTabsDump allTabs = LoadAllTabsFile();
            allTabs.LastUpdated = DateTime.Now;

            int totalRemoved = 0;
            int totalCheckedTabs = 0;

            for (int i = 0; i < tabRows.Count; i++)
            {
                bool selected = Settings.SelectedTabs.ContainsKey(i) && Settings.SelectedTabs[i];
                if (!selected)
                    continue;

                totalCheckedTabs++;
                _lastStatus = "Updating tab [" + i + "] " + CleanupTabName(tabRows[i].Name);

                bool opened = await OpenMerchantTabByIndex(i);
                if (!opened)
                {
                    Log("UPDATE ITEMS: failed to open tab [" + i + "]");
                    continue;
                }

                await Task.Delay(350);

                dynamic refreshedUi = GameController?.Game?.IngameState?.IngameUi;
                if (refreshedUi == null)
                    continue;

                dynamic refreshedPages = GetElementByPath(refreshedUi, MerchantTabPagesPath);
                if (refreshedPages == null)
                    continue;

                int activePageIndex = GetActivePageIndex(refreshedPages);
                if (activePageIndex < 0)
                    continue;

                dynamic activePage = null;
                try { activePage = refreshedPages.GetChildAtIndex(activePageIndex); } catch { }

                if (activePage == null)
                    continue;

                var currentItems = new List<MerchantItemInfo>();
                CollectInventoryItems(activePage, "activePage/" + activePageIndex, currentItems);

                var currentKeys = new HashSet<string>();
                for (int j = 0; j < currentItems.Count; j++)
                {
                    MerchantItemInfo item = currentItems[j];
                    string key = BuildItemKey(activePageIndex, item.EntityPath, item.X, item.Y, item.W, item.H);
                    currentKeys.Add(key);
                }

                MerchTabDump targetTab = null;
                for (int j = 0; j < allTabs.Tabs.Count; j++)
                {
                    if (allTabs.Tabs[j].TabIndex == activePageIndex)
                    {
                        targetTab = allTabs.Tabs[j];
                        break;
                    }
                }

                if (targetTab == null)
                    continue;

                int beforeCount = targetTab.Items.Count;

                var keptItems = new List<MerchStoredItem>();
                for (int j = 0; j < targetTab.Items.Count; j++)
                {
                    MerchStoredItem stored = targetTab.Items[j];
                    string storedKey = BuildItemKey(activePageIndex, stored.EntityPath, stored.X, stored.Y, stored.W, stored.H);

                    if (currentKeys.Contains(storedKey))
                        keptItems.Add(stored);
                }

                int removed = beforeCount - keptItems.Count;
                totalRemoved += removed;

                targetTab.Items = keptItems;
                targetTab.ItemCount = currentItems.Count;
                targetTab.LastScanned = DateTime.Now;

                Log("UPDATE ITEMS: tab [" + activePageIndex + "] removed " + removed + " sold/missing items");
                await Task.Delay(120);
            }

            allTabs.Tabs.Sort(delegate (MerchTabDump a, MerchTabDump b)
            {
                return a.TabIndex.CompareTo(b.TabIndex);
            });

            allTabs.KnownTabs.Sort(delegate (MerchKnownTab a, MerchKnownTab b)
            {
                return a.Index.CompareTo(b.Index);
            });

            File.WriteAllText(AllTabsItemsPath, JsonConvert.SerializeObject(allTabs, Formatting.Indented));

            _lastStatus = "Update complete: checked " + totalCheckedTabs + " tabs, removed " + totalRemoved + " items";
            Log("UPDATE ITEMS: complete, checked " + totalCheckedTabs + " tabs, removed " + totalRemoved + " items");
        }
        catch (Exception ex)
        {
            Log("UPDATE ITEMS ERROR: " + ex.Message);
            _lastStatus = "Update items error: " + ex.Message;
        }
        finally
        {
            _isRunningUpdateItems = false;
        }
    }

    private async Task<bool> OpenMerchantTabByIndex(int index)
    {
        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
                return false;

            dynamic dropdown = GetElementByPath(ingameUi, MerchantTabDropdownPath);
            if (dropdown == null)
                return false;

            dynamic row = null;
            try { row = dropdown.GetChildAtIndex(index); } catch { }

            if (row == null)
                return false;

            dynamic rect = null;
            try { rect = row.GetClientRect(); } catch { }
            if (rect == null)
                return false;

            float x = (float)rect.X + (float)rect.Width / 2f;
            float y = (float)rect.Y + (float)rect.Height / 2f;

            var win = GameController.Window.GetWindowRectangle();
            Input.SetCursorPos(new System.Numerics.Vector2(win.X + x, win.Y + y));
            await Task.Delay(80);
            Input.LeftDown();
            await Task.Delay(50);
            Input.LeftUp();
            await Task.Delay(250);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task DumpCurrentlyActiveMerchantTabItemsInternal(bool withPrices)
    {
        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("ITEM DUMP: IngameUi is null");
                return;
            }

            dynamic dropdown = GetElementByPath(ingameUi, MerchantTabDropdownPath);
            dynamic pages = GetElementByPath(ingameUi, MerchantTabPagesPath);

            if (dropdown == null || pages == null)
            {
                Log("ITEM DUMP: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = GetMerchantTabRowsFromKnownDropdown(dropdown);
            int activePageIndex = GetActivePageIndex(pages);

            if (activePageIndex < 0)
            {
                Log("ITEM DUMP: Could not find active merchant tab page.");
                return;
            }

            dynamic activePage = null;
            try { activePage = pages.GetChildAtIndex(activePageIndex); } catch { }

            if (activePage == null)
            {
                Log("ITEM DUMP: Active page element is null.");
                return;
            }

            var items = new List<MerchantItemInfo>();
            CollectInventoryItems(activePage, "activePage/" + activePageIndex, items);

            string activeTabName = "Unknown";
            if (activePageIndex >= 0 && activePageIndex < tabRows.Count)
                activeTabName = CleanupTabName(tabRows[activePageIndex].Name);

            if (!string.IsNullOrWhiteSpace(activeTabName) && activeTabName != "-" && activeTabName != "Unknown")
            {
                if (!Settings.TabNames.ContainsKey(activePageIndex) || Settings.TabNames[activePageIndex] != activeTabName)
                    Settings.TabNames[activePageIndex] = activeTabName;
            }

            if (withPrices)
                await PopulatePricesForCurrentTabItems(items);

            var exportItems = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                MerchantItemInfo item = items[i];
                exportItems.Add(new
                {
                    item.UiPath,
                    item.EntityPath,
                    item.X,
                    item.Y,
                    item.W,
                    item.H,
                    item.PriceRaw,
                    item.PriceAmount,
                    item.PriceCurrency,
                    item.PriceScannedAt,
                    item.PriceNoteRaw
                });
            }

            File.WriteAllText(ActiveTabItemsPath, JsonConvert.SerializeObject(new
            {
                DumpedAt = DateTime.Now,
                ActiveTabIndex = activePageIndex,
                ActiveTabName = activeTabName,
                ItemCount = items.Count,
                Items = exportItems
            }, Formatting.Indented));

            UpdateAllTabsFile(activePageIndex, activeTabName, items);
        }
        catch (Exception ex)
        {
            Log("ITEM DUMP ERROR: " + ex.Message);
        }
    }

    private async Task PopulatePricesForCurrentTabItems(List<MerchantItemInfo> items)
    {
        if (items == null || items.Count == 0)
            return;

        var win = GameController.Window.GetWindowRectangle();

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                MerchantItemInfo item = items[i];

                float cx = item.X + item.W / 2f;
                float cy = item.Y + item.H / 2f;

                Input.SetCursorPos(new System.Numerics.Vector2(win.X + cx, win.Y + cy));
                await Task.Delay(220);

                string before = GetClipboardTextSafe();
                SetClipboardTextSafe(string.Empty);
                await Task.Delay(40);

                SendCtrlAltC();
                await Task.Delay(260);

                string copied = GetClipboardTextSafe();

                if (string.IsNullOrWhiteSpace(copied) || copied == before)
                {
                    await Task.Delay(180);
                    copied = GetClipboardTextSafe();
                }

                if (!string.IsNullOrWhiteSpace(copied) &&
                    copied.IndexOf("Item Class:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.PriceScannedAt = DateTime.Now;
                    ParsePriceFromCopiedText(item, copied);
                }

                await Task.Delay(60);
            }
        }
        catch (Exception ex)
        {
            Log("PRICE SCAN ERROR: " + ex.Message);
        }
    }

    private void SendCtrlAltC()
    {
        try
        {
            Input.KeyDown(Keys.ControlKey);
            Thread.Sleep(20);
            Input.KeyDown(Keys.Menu);
            Thread.Sleep(20);
            Input.KeyDown(Keys.C);
            Thread.Sleep(30);
            Input.KeyUp(Keys.C);
            Thread.Sleep(20);
            Input.KeyUp(Keys.Menu);
            Thread.Sleep(20);
            Input.KeyUp(Keys.ControlKey);
        }
        catch
        {
        }
    }

    private string GetClipboardTextSafe()
    {
        string result = "";
        Thread sta = new Thread(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                    result = Clipboard.GetText();
            }
            catch
            {
                result = "";
            }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        return result ?? "";
    }

    private void SetClipboardTextSafe(string text)
    {
        Thread sta = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text ?? "");
            }
            catch
            {
            }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
    }

    private void ParsePriceFromCopiedText(MerchantItemInfo item, string copied)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(copied))
                return;

            string[] lines = copied.Replace("\r", "").Split('\n');
            string noteLine = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i]?.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("Note:", StringComparison.OrdinalIgnoreCase))
                {
                    noteLine = line;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(noteLine))
                return;

            item.PriceNoteRaw = noteLine;
            item.PriceRaw = noteLine;

            Match m = Regex.Match(noteLine, @"Note:\s*~b/o\s+([0-9]+(?:\.[0-9]+)?)\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(noteLine, @"Note:\s*~price\s+([0-9]+(?:\.[0-9]+)?)\s+(.+)$", RegexOptions.IgnoreCase);

            if (!m.Success)
                return;

            double amountDouble;
            if (double.TryParse(
                m.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out amountDouble))
            {
                if (Math.Abs(amountDouble - Math.Round(amountDouble)) < 0.0001)
                    item.PriceAmount = (int)Math.Round(amountDouble);
            }

            item.PriceCurrency = m.Groups[2].Value.Trim();
        }
        catch
        {
        }
    }

    private void UpdateAllTabsFile(int activeTabIndex, string activeTabName, List<MerchantItemInfo> items)
    {
        try
        {
            MerchAllTabsDump allTabs = LoadAllTabsFile();
            allTabs.LastUpdated = DateTime.Now;
            allTabs.KnownTabs.Clear();

            for (int i = 0; i < Settings.ScannedTabIndices.Count; i++)
            {
                int idx;
                if (!int.TryParse(Settings.ScannedTabIndices[i], out idx))
                    continue;

                string name = "(unknown)";
                if (Settings.TabNames.ContainsKey(idx))
                    name = CleanupTabName(Settings.TabNames[idx]);

                bool selected = Settings.SelectedTabs.ContainsKey(idx) && Settings.SelectedTabs[idx];

                allTabs.KnownTabs.Add(new MerchKnownTab
                {
                    Index = idx,
                    Name = name,
                    Selected = selected
                });
            }

            MerchTabDump targetTab = null;
            for (int i = 0; i < allTabs.Tabs.Count; i++)
            {
                if (allTabs.Tabs[i].TabIndex == activeTabIndex)
                {
                    targetTab = allTabs.Tabs[i];
                    break;
                }
            }

            if (targetTab == null)
            {
                targetTab = new MerchTabDump();
                targetTab.TabIndex = activeTabIndex;
                allTabs.Tabs.Add(targetTab);
            }

            targetTab.TabName = activeTabName;
            targetTab.LastScanned = DateTime.Now;
            targetTab.ItemCount = items.Count;

            var oldItemsByKey = new Dictionary<string, MerchStoredItem>();
            for (int i = 0; i < targetTab.Items.Count; i++)
            {
                MerchStoredItem oldItem = targetTab.Items[i];
                string oldKey = BuildItemKey(activeTabIndex, oldItem.EntityPath, oldItem.X, oldItem.Y, oldItem.W, oldItem.H);
                if (!oldItemsByKey.ContainsKey(oldKey))
                    oldItemsByKey.Add(oldKey, oldItem);
            }

            var newStoredItems = new List<MerchStoredItem>();

            for (int i = 0; i < items.Count; i++)
            {
                MerchantItemInfo src = items[i];
                string newKey = BuildItemKey(activeTabIndex, src.EntityPath, src.X, src.Y, src.W, src.H);

                DateTime firstSeenAt = DateTime.Now;
                string oldPriceRaw = null;
                int? oldPriceAmount = null;
                string oldPriceCurrency = null;
                DateTime oldPriceScannedAt = DateTime.MinValue;
                string oldPriceNoteRaw = null;

                if (oldItemsByKey.ContainsKey(newKey))
                {
                    MerchStoredItem old = oldItemsByKey[newKey];
                    if (old.FirstSeenAt != DateTime.MinValue)
                        firstSeenAt = old.FirstSeenAt;

                    oldPriceRaw = old.PriceRaw;
                    oldPriceAmount = old.PriceAmount;
                    oldPriceCurrency = old.PriceCurrency;
                    oldPriceScannedAt = old.PriceScannedAt;
                    oldPriceNoteRaw = old.PriceNoteRaw;
                }

                newStoredItems.Add(new MerchStoredItem
                {
                    UiPath = src.UiPath,
                    EntityPath = src.EntityPath,
                    X = src.X,
                    Y = src.Y,
                    W = src.W,
                    H = src.H,
                    FirstSeenAt = firstSeenAt,
                    PriceRaw = !string.IsNullOrWhiteSpace(src.PriceRaw) ? src.PriceRaw : oldPriceRaw,
                    PriceAmount = src.PriceAmount ?? oldPriceAmount,
                    PriceCurrency = !string.IsNullOrWhiteSpace(src.PriceCurrency) ? src.PriceCurrency : oldPriceCurrency,
                    PriceScannedAt = src.PriceScannedAt != DateTime.MinValue ? src.PriceScannedAt : oldPriceScannedAt,
                    PriceNoteRaw = !string.IsNullOrWhiteSpace(src.PriceNoteRaw) ? src.PriceNoteRaw : oldPriceNoteRaw
                });
            }

            targetTab.Items = newStoredItems;

            allTabs.Tabs.Sort(delegate (MerchTabDump a, MerchTabDump b) { return a.TabIndex.CompareTo(b.TabIndex); });
            allTabs.KnownTabs.Sort(delegate (MerchKnownTab a, MerchKnownTab b) { return a.Index.CompareTo(b.Index); });

            File.WriteAllText(AllTabsItemsPath, JsonConvert.SerializeObject(allTabs, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Log("ALL TABS UPDATE ERROR: " + ex.Message);
        }
    }

    private void HighlightOldItemsInCurrentTab()
    {
        try
        {
            _highlightBoxes.Clear();
            _highlightEnabled = false;
            _highlightTabIndex = -1;

            MerchAllTabsDump allTabs = LoadAllTabsFile();
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                _lastStatus = "Highlight failed: IngameUi null";
                return;
            }

            dynamic pages = GetElementByPath(ingameUi, MerchantTabPagesPath);
            if (pages == null)
            {
                _lastStatus = "Highlight failed: merchant UI not found";
                return;
            }

            int activePageIndex = GetActivePageIndex(pages);
            if (activePageIndex < 0)
            {
                _lastStatus = "Highlight failed: no active merchant tab";
                return;
            }

            MerchTabDump tabDump = null;
            for (int i = 0; i < allTabs.Tabs.Count; i++)
            {
                if (allTabs.Tabs[i].TabIndex == activePageIndex)
                {
                    tabDump = allTabs.Tabs[i];
                    break;
                }
            }

            if (tabDump == null)
            {
                _lastStatus = "No stored data for current tab";
                return;
            }

            DateTime cutoff = DateTime.Now.AddHours(-_highlightHours);

            for (int i = 0; i < tabDump.Items.Count; i++)
            {
                MerchStoredItem item = tabDump.Items[i];
                if (item.FirstSeenAt != DateTime.MinValue && item.FirstSeenAt <= cutoff)
                {
                    _highlightBoxes.Add(new HighlightBox
                    {
                        X = item.X,
                        Y = item.Y,
                        W = item.W,
                        H = item.H
                    });
                }
            }

            _highlightEnabled = true;
            _highlightTabIndex = activePageIndex;
            _lastStatus = "Highlighted " + _highlightBoxes.Count + " items older than " + _highlightHours + "h";
        }
        catch (Exception ex)
        {
            Log("HIGHLIGHT ERROR: " + ex.Message);
            _lastStatus = "Highlight error: " + ex.Message;
        }
    }

    private string BuildTabSummaryText(int tabIndex)
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

    private string BuildSelectedTabsTotalSummaryText()
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
                bool selected = Settings.SelectedTabs.ContainsKey(tab.TabIndex) && Settings.SelectedTabs[tab.TabIndex];
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

    private string BuildItemKey(int tabIndex, string entityPath, float x, float y, float w, float h)
    {
        return tabIndex + "|" + (entityPath ?? "-") + "|" + Round2(x) + "|" + Round2(y) + "|" + Round2(w) + "|" + Round2(h);
    }

    private string Round2(float value)
    {
        return Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private MerchAllTabsDump LoadAllTabsFile()
    {
        try
        {
            if (!File.Exists(AllTabsItemsPath))
                return new MerchAllTabsDump();

            string json = File.ReadAllText(AllTabsItemsPath);
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

    private dynamic GetElementByPath(dynamic root, int[] path)
    {
        dynamic cur = root;
        for (int i = 0; i < path.Length; i++)
        {
            if (cur == null)
                return null;
            try { cur = cur.GetChildAtIndex(path[i]); } catch { return null; }
        }
        return cur;
    }

    private List<MerchantTabRow> GetMerchantTabRowsFromKnownDropdown(dynamic dropdown)
    {
        var rows = new List<MerchantTabRow>();
        if (dropdown == null)
            return rows;

        int childCount = 0;
        try { childCount = (int)dropdown.ChildCount; } catch { }

        for (int i = 0; i < childCount; i++)
        {
            dynamic row = null;
            try { row = dropdown.GetChildAtIndex(i); } catch { }
            if (row == null)
                continue;

            bool visible = false;
            dynamic rect = null;
            try { visible = row.IsVisible; } catch { }
            try { rect = row.GetClientRect(); } catch { }

            if (!visible || rect == null)
                continue;

            float x, y, w, h;
            try
            {
                x = (float)rect.X;
                y = (float)rect.Y;
                w = (float)rect.Width;
                h = (float)rect.Height;
            }
            catch { continue; }

            if (w < 60f || h < 18f)
                continue;

            string name = CleanupTabName(ExtractDeepText(row));
            if (string.IsNullOrWhiteSpace(name) || name == "-")
                name = "Tab " + i;

            rows.Add(new MerchantTabRow { Index = i, Name = name, X = x, Y = y, W = w, H = h });
        }

        rows.Sort(delegate (MerchantTabRow a, MerchantTabRow b) { return a.Y.CompareTo(b.Y); });
        return rows;
    }

    private int GetActivePageIndex(dynamic pages)
    {
        if (pages == null)
            return -1;

        int childCount = 0;
        try { childCount = (int)pages.ChildCount; } catch { }

        for (int i = 0; i < childCount; i++)
        {
            dynamic page = null;
            try { page = pages.GetChildAtIndex(i); } catch { }
            if (page == null)
                continue;

            bool visible = false;
            try { visible = page.IsVisible; } catch { }
            if (visible)
                return i;
        }

        return -1;
    }

    private void CollectInventoryItems(dynamic el, string path, List<MerchantItemInfo> items)
    {
        if (el == null)
            return;

        try
        {
            string type = "-";
            try { type = el.Type != null ? el.Type.ToString() : "-"; } catch { }

            if (string.Equals(type, "InventoryItem", StringComparison.OrdinalIgnoreCase))
            {
                dynamic rect = null;
                try { rect = el.GetClientRect(); } catch { }

                float x = 0f, y = 0f, w = 0f, h = 0f;
                if (rect != null)
                {
                    try
                    {
                        x = (float)rect.X;
                        y = (float)rect.Y;
                        w = (float)rect.Width;
                        h = (float)rect.Height;
                    }
                    catch { }
                }

                string entityPath = "-";
                try { entityPath = Safe((string)el.Entity?.Path); } catch { }

                items.Add(new MerchantItemInfo
                {
                    UiPath = path,
                    EntityPath = entityPath,
                    X = x,
                    Y = y,
                    W = w,
                    H = h
                });
            }

            int childCount = 0;
            try { childCount = (int)el.ChildCount; } catch { }

            for (int i = 0; i < childCount; i++)
            {
                dynamic child = null;
                try { child = el.GetChildAtIndex(i); } catch { }
                if (child != null)
                    CollectInventoryItems(child, path + "/" + i, items);
            }
        }
        catch { }
    }

    private string ExtractDeepText(dynamic el)
    {
        var parts = new List<string>();
        CollectTextRecursive(el, parts, 0);

        var cleaned = new List<string>();
        for (int i = 0; i < parts.Count; i++)
        {
            string p = parts[i];
            if (!string.IsNullOrWhiteSpace(p) && p != "-")
                cleaned.Add(p);
        }

        string joined = string.Join(" ", cleaned).Trim();
        return string.IsNullOrWhiteSpace(joined) ? "-" : joined;
    }

    private void CollectTextRecursive(dynamic el, List<string> parts, int depth)
    {
        if (el == null || depth > 6)
            return;

        try
        {
            string text = null;
            try { text = (string)el.Text; } catch { }

            if (!string.IsNullOrWhiteSpace(text) && text != "-")
                parts.Add(text.Trim());

            int childCount = 0;
            try { childCount = (int)el.ChildCount; } catch { }

            for (int i = 0; i < childCount; i++)
            {
                dynamic child = null;
                try { child = el.GetChildAtIndex(i); } catch { }
                if (child != null)
                    CollectTextRecursive(child, parts, depth + 1);
            }
        }
        catch { }
    }

    private string CleanupTabName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "-";

        string s = raw.Trim();
        string[] keep =
        {
            "Sub 10C", "11-20C", "21-30C", "31-50C",
            "50C+", "100+C", "170+C", "1-4 DIV", "5+ DIV"
        };

        for (int i = 0; i < keep.Length; i++)
        {
            if (s.IndexOf(keep[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return keep[i];
        }

        return s;
    }

    private string Safe(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "-";
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private void Log(string msg)
    {
        DebugWindow.LogMsg("[MerchTab] " + msg);
    }

    public override void DrawSettings()
    {
        ImGuiNET.ImGui.TextDisabled("Open Faustus -> Merchant and expand the right-side tab dropdown");
        ImGuiNET.ImGui.Spacing();

        bool buttonDisabled = _isRunningGetItems || _isRunningAddNewItems || _isRunningUpdateItems;
        if (buttonDisabled)
            ImGuiNET.ImGui.BeginDisabled();

        if (ImGuiNET.ImGui.Button("Update Merchtab Names"))
            ScanMerchantTabs();

        if (ImGuiNET.ImGui.Button("Save Items without price"))
            _ = GetItemsFromSelectedTabs(false);

        if (ImGuiNET.ImGui.Button("Save items with price"))
            _ = GetItemsFromSelectedTabs(true);

        if (ImGuiNET.ImGui.Button("New Items Added"))
            _ = AddNewItemsToCurrentActiveTab();

        if (ImGuiNET.ImGui.Button("Remove sold/removed items"))
            _ = UpdateItemsFromSelectedTabs();

        if (buttonDisabled)
            ImGuiNET.ImGui.EndDisabled();

        ImGuiNET.ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(_lastStatus))
            ImGuiNET.ImGui.TextDisabled(_lastStatus);

        ImGuiNET.ImGui.Spacing();

        if (Settings.ScannedTabIndices == null || Settings.ScannedTabIndices.Count == 0)
        {
            ImGuiNET.ImGui.TextDisabled("No merchant tabs scanned yet.");
        }
        else
        {
            ImGuiNET.ImGui.Text("Found " + Settings.ScannedTabIndices.Count + " merchant tabs:");

            for (int i = 0; i < Settings.ScannedTabIndices.Count; i++)
            {
                int idx;
                if (!int.TryParse(Settings.ScannedTabIndices[i], out idx))
                    continue;

                string name = Settings.TabNames.ContainsKey(idx) ? CleanupTabName(Settings.TabNames[idx]) : "(unknown)";
                bool selected = Settings.SelectedTabs.ContainsKey(idx) ? Settings.SelectedTabs[idx] : true;
                if (!Settings.SelectedTabs.ContainsKey(idx))
                    Settings.SelectedTabs[idx] = true;

                if (ImGuiNET.ImGui.Checkbox("##tab_" + idx, ref selected))
                    Settings.SelectedTabs[idx] = selected;

                ImGuiNET.ImGui.SameLine();
                string summary = BuildTabSummaryText(idx);
                ImGuiNET.ImGui.Text("[" + idx + "] " + name + " " + summary);
            }

            ImGuiNET.ImGui.Spacing();
            ImGuiNET.ImGui.TextDisabled(BuildSelectedTabsTotalSummaryText());
        }

        ImGuiNET.ImGui.Spacing();
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.Spacing();

        int hours = Settings.HighlightOlderThanHours;
        ImGuiNET.ImGui.Text("Highlight items older than (hours):");
        ImGuiNET.ImGui.SetNextItemWidth(120f);
        if (ImGuiNET.ImGui.InputInt("##olderhours", ref hours))
        {
            if (hours < 1) hours = 1;
            if (hours > 9999) hours = 9999;
            Settings.HighlightOlderThanHours = hours;
        }

        ImGuiNET.ImGui.Spacing();

        if (ImGuiNET.ImGui.Button("Highlight Old Items"))
        {
            _highlightHours = Settings.HighlightOlderThanHours;
            HighlightOldItemsInCurrentTab();
        }

        ImGuiNET.ImGui.SameLine();

        if (ImGuiNET.ImGui.Button("Clear Highlights"))
        {
            _highlightEnabled = false;
            _highlightBoxes.Clear();
            _highlightTabIndex = -1;
            _lastStatus = "Highlights cleared";
        }
    }

    public override void Render()
    {
        if (!_highlightEnabled || _highlightBoxes == null || _highlightBoxes.Count == 0)
            return;

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
                return;

            dynamic pages = GetElementByPath(ingameUi, MerchantTabPagesPath);
            if (pages == null)
                return;

            int activePageIndex = GetActivePageIndex(pages);
            if (activePageIndex != _highlightTabIndex)
                return;

            for (int i = 0; i < _highlightBoxes.Count; i++)
            {
                HighlightBox b = _highlightBoxes[i];
                Graphics.DrawFrame(new RectangleF(b.X, b.Y, b.W, b.H), Color.Red, 2);
            }
        }
        catch { }
    }

    private class MerchantTabRow
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float W { get; set; }
        public float H { get; set; }
    }

    private class MerchantItemInfo
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
        public DateTime PriceScannedAt { get; set; } = DateTime.MinValue;
        public string PriceNoteRaw { get; set; }
    }

    private class MerchAllTabsDump
    {
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public List<MerchKnownTab> KnownTabs { get; set; } = new List<MerchKnownTab>();
        public List<MerchTabDump> Tabs { get; set; } = new List<MerchTabDump>();
    }

    private class MerchKnownTab
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public bool Selected { get; set; }
    }

    private class MerchTabDump
    {
        public int TabIndex { get; set; }
        public string TabName { get; set; }
        public DateTime LastScanned { get; set; } = DateTime.MinValue;
        public int ItemCount { get; set; }
        public List<MerchStoredItem> Items { get; set; } = new List<MerchStoredItem>();
    }

    private class MerchStoredItem
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
        public DateTime PriceScannedAt { get; set; } = DateTime.MinValue;
        public string PriceNoteRaw { get; set; }
    }

    private class HighlightBox
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float W { get; set; }
        public float H { get; set; }
    }
}