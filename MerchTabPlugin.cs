using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using MerchTabPlugin.Models;
using MerchTabPlugin.Services;
using Newtonsoft.Json;
using SharpDX;
using Color = SharpDX.Color;

namespace MerchTabPlugin;

public class MerchTabPlugin : BaseSettingsPlugin<MerchTabSettings>
{
    private string TabIndexPath => Path.Combine(DirectoryFullName, "merchant_tab_indices.json");
    private string ActiveTabItemsPath => Path.Combine(DirectoryFullName, "merchant_active_tab_items.json");
    private string AllTabsItemsPath => Path.Combine(DirectoryFullName, "merchant_all_tabs_items.json");

    private static readonly int[] RepriceValueInputPath = { 178, 2, 0, 0 };
    private static readonly int[] RepriceListItemPath = { 178, 2, 0, 2, 0 };

    private MerchantUiService _ui;
    private MerchantScanService _scan;
    private MerchantStorageService _storage;

    private bool _isRunningGetItems = false;
    private bool _isRunningTabUpdate = false;
    private bool _isRunningRepriceCheck = false;
    private bool _isRunningApplyReprice = false;
    private string _lastStatus = "";

    private bool _highlightEnabled = false;
    private int _highlightHours = 12;
    private int _highlightTabIndex = -1;
    private readonly List<HighlightBox> _highlightBoxes = new List<HighlightBox>();

    private bool _highlightRepriceEnabled = false;
    private int _highlightRepriceHours = 10;
    private int _highlightRepriceTabIndex = -1;
    private readonly List<HighlightBox> _highlightRepriceBoxes = new List<HighlightBox>();

    private bool _highlightCheapOldPriceEnabled = false;
    private int _highlightCheapOldPriceHours = 5;
    private int _highlightCheapOldPriceMaxChaos = 3;
    private int _highlightCheapOldPriceTabIndex = -1;
    private readonly List<HighlightBox> _highlightCheapOldPriceBoxes = new List<HighlightBox>();

    private bool _showStartCountdown = false;
    private DateTime _countdownUntil = DateTime.MinValue;
    private string _countdownMessage = "";

    private readonly Random _rng = new Random();

    public override bool Initialise()
    {
        Name = "MerchTabPlugin";
        EnsureServices();
        return true;
    }

    private void EnsureServices()
    {
        if (_ui == null)
            _ui = new MerchantUiService(GameController);

        if (_scan == null)
            _scan = new MerchantScanService(GameController, Log);

        if (_storage == null)
            _storage = new MerchantStorageService(AllTabsItemsPath);
    }

    private async Task RunWithStartCountdown(Func<Task> action, string message = "Don't move your mouse")
    {
        _showStartCountdown = true;
        _countdownUntil = DateTime.Now.AddSeconds(3);
        _countdownMessage = message;

        while (DateTime.Now < _countdownUntil)
            await Task.Delay(50);

        _showStartCountdown = false;
        _countdownMessage = "";

        await action();
    }

    private int GetCountdownSecondsRemaining()
    {
        if (!_showStartCountdown)
            return 0;

        double remaining = (_countdownUntil - DateTime.Now).TotalSeconds;
        if (remaining <= 0)
            return 0;

        return (int)Math.Ceiling(remaining);
    }

    private int GetCurrentActiveMerchantTabIndex()
    {
        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
                return -1;

            dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);
            if (pages == null)
                return -1;

            return _ui.GetActivePageIndex(pages);
        }
        catch
        {
            return -1;
        }
    }

    private async Task RunUpdateWithOptionalCountdown(int targetTabIndex, Func<Task> action)
    {
        int activeTabIndex = GetCurrentActiveMerchantTabIndex();

        if (activeTabIndex == targetTabIndex)
            await action();
        else
            await RunWithStartCountdown(action);
    }

    private int NextDelay(int minInclusive, int maxInclusive)
    {
        lock (_rng)
        {
            return _rng.Next(minInclusive, maxInclusive + 1);
        }
    }

    private async Task RandomDelay(int minInclusive, int maxInclusive)
    {
        await Task.Delay(NextDelay(minInclusive, maxInclusive));
    }

    private void SleepRandom(int minInclusive, int maxInclusive)
    {
        Thread.Sleep(NextDelay(minInclusive, maxInclusive));
    }

    private void RebuildKnownTabs(MerchAllTabsDump allTabs)
    {
        allTabs.KnownTabs.Clear();

        for (int i = 0; i < Settings.ScannedTabIndices.Count; i++)
        {
            int idx;
            if (!int.TryParse(Settings.ScannedTabIndices[i], out idx))
                continue;

            string name = "(unknown)";
            if (Settings.TabNames.ContainsKey(idx))
                name = _ui.CleanupTabName(Settings.TabNames[idx]);

            bool selected = Settings.SelectedTabs.ContainsKey(idx) && Settings.SelectedTabs[idx];

            allTabs.KnownTabs.Add(new MerchKnownTab
            {
                Index = idx,
                Name = name,
                Selected = selected
            });
        }
    }

    private void ScanMerchantTabs()
    {
        EnsureServices();

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("SCAN: IngameUi is null");
                return;
            }

            dynamic dropdown = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabDropdownPath);
            if (dropdown == null)
            {
                Log("SCAN: Merchant tab dropdown not found at known path.");
                Log("SCAN: Open Faustus -> Merchant and expand the right-side dropdown first.");
                return;
            }

            List<MerchantTabRow> rows = _ui.GetMerchantTabRowsFromKnownDropdown(dropdown);
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
                    Name = _ui.CleanupTabName(row.Name),
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
        EnsureServices();

        if (_isRunningGetItems)
            return;

        _isRunningGetItems = true;
        _lastStatus = withPrices ? "Saving all tab items with price..." : "Saving all tab items without price...";
        Log(withPrices ? "GET ITEMS+PRICES: started" : "GET ITEMS: started");

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("GET ITEMS: IngameUi is null");
                return;
            }

            dynamic dropdown = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabDropdownPath);
            dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);

            if (dropdown == null || pages == null)
            {
                Log("GET ITEMS: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = _ui.GetMerchantTabRowsFromKnownDropdown(dropdown);
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

                _lastStatus = "Opening tab [" + i + "] " + _ui.CleanupTabName(tabRows[i].Name);

                bool opened = await _ui.OpenMerchantTabByIndex(i);
                if (!opened)
                {
                    Log("GET ITEMS: failed to open tab [" + i + "]");
                    continue;
                }

                await RandomDelay(320, 560);
                await DumpCurrentlyActiveMerchantTabItemsInternal(withPrices);
                await RandomDelay(180, 320);
            }

            _lastStatus = withPrices ? "Save all tab items with price complete" : "Save all tab items without price complete";
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

    private async Task UpdateSelectedTabsForNewAndRemovedItems()
    {
        EnsureServices();

        if (_isRunningTabUpdate)
            return;

        _isRunningTabUpdate = true;
        _lastStatus = "Updating selected tabs for new/removed items...";
        Log("TAB UPDATE: started");

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("TAB UPDATE: IngameUi is null");
                return;
            }

            dynamic dropdown = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabDropdownPath);
            if (dropdown == null)
            {
                Log("TAB UPDATE: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = _ui.GetMerchantTabRowsFromKnownDropdown(dropdown);
            if (tabRows.Count == 0)
            {
                Log("TAB UPDATE: No tab rows found.");
                return;
            }

            int totalNew = 0;
            int totalRemoved = 0;
            int checkedTabs = 0;

            for (int i = 0; i < tabRows.Count; i++)
            {
                bool selected = Settings.SelectedTabs.ContainsKey(i) && Settings.SelectedTabs[i];
                if (!selected)
                    continue;

                checkedTabs++;
                TabUpdateResult result = await UpdateSingleTabInternal(i, false);
                totalNew += result.NewItems;
                totalRemoved += result.RemovedItems;
            }

            _lastStatus = "Tab update complete: checked " + checkedTabs + " tabs, added " + totalNew + ", removed " + totalRemoved;
            Log("TAB UPDATE: complete, checked " + checkedTabs + " tabs, added " + totalNew + ", removed " + totalRemoved);
        }
        catch (Exception ex)
        {
            Log("TAB UPDATE ERROR: " + ex.Message);
            _lastStatus = "Tab update error: " + ex.Message;
        }
        finally
        {
            _isRunningTabUpdate = false;
        }
    }

    private async Task UpdateSingleTabOnly(int tabIndex)
    {
        EnsureServices();

        if (_isRunningTabUpdate)
            return;

        _isRunningTabUpdate = true;
        _lastStatus = "Updating tab [" + tabIndex + "]...";
        Log("TAB UPDATE SINGLE: started for tab " + tabIndex);

        try
        {
            TabUpdateResult result = await UpdateSingleTabInternal(tabIndex, false);
            _lastStatus = "Tab [" + tabIndex + "] updated: added " + result.NewItems + ", removed " + result.RemovedItems;
            Log("TAB UPDATE SINGLE: tab " + tabIndex + " added " + result.NewItems + ", removed " + result.RemovedItems);
        }
        catch (Exception ex)
        {
            Log("TAB UPDATE SINGLE ERROR: " + ex.Message);
            _lastStatus = "Single tab update error: " + ex.Message;
        }
        finally
        {
            _isRunningTabUpdate = false;
        }
    }

    private async Task PriceUpdateSingleTabOnly(int tabIndex)
    {
        EnsureServices();

        if (_isRunningRepriceCheck)
            return;

        _isRunningRepriceCheck = true;
        _lastStatus = "Checking prices in tab [" + tabIndex + "]...";
        Log("PRICE UPDATE SINGLE: started for tab " + tabIndex);

        try
        {
            TabPriceUpdateResult result = await CheckSingleTabPricesInternal(tabIndex);
            _lastStatus = "Tab [" + tabIndex + "] price update: changed " + result.RepricedItems + " items";
            Log("PRICE UPDATE SINGLE: tab " + tabIndex + " changed " + result.RepricedItems + " items");
        }
        catch (Exception ex)
        {
            Log("PRICE UPDATE SINGLE ERROR: " + ex.Message);
            _lastStatus = "Single tab price update error: " + ex.Message;
        }
        finally
        {
            _isRunningRepriceCheck = false;
        }
    }

    private async Task RepriceSelectedTabs()
    {
        EnsureServices();

        if (_isRunningApplyReprice)
            return;

        _isRunningApplyReprice = true;
        _lastStatus = "Repricing selected tabs...";
        Log("REPRICE APPLY: started");

        try
        {
            MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
            if (allTabs == null || allTabs.Tabs == null || allTabs.Tabs.Count == 0)
            {
                _lastStatus = "No stored tab data available";
                return;
            }

            int hours = Settings.RepriceOlderThanHours;
            int percent = Settings.RepricePercent;

            if (hours < 1)
                hours = 1;

            if (percent < 1)
                percent = 1;

            if (percent > 99)
                percent = 99;

            DateTime cutoff = DateTime.Now.AddHours(-hours);

            int totalChanged = 0;
            int checkedTabs = 0;

            for (int i = 0; i < allTabs.Tabs.Count; i++)
            {
                MerchTabDump tab = allTabs.Tabs[i];
                bool selected = Settings.SelectedTabs.ContainsKey(tab.TabIndex) && Settings.SelectedTabs[tab.TabIndex];
                if (!selected)
                    continue;

                checkedTabs++;
                int changedInTab = await RepriceSingleTabInternal(tab.TabIndex, cutoff, percent);
                totalChanged += changedInTab;
            }

            _lastStatus = "Repriced " + totalChanged + " items across " + checkedTabs + " selected tabs";
            Log("REPRICE APPLY: repriced " + totalChanged + " items across " + checkedTabs + " tabs");
        }
        catch (Exception ex)
        {
            Log("REPRICE APPLY ERROR: " + ex.Message);
            _lastStatus = "Reprice error: " + ex.Message;
        }
        finally
        {
            _isRunningApplyReprice = false;
        }
    }

    private async Task<int> RepriceSingleTabInternal(int requestedTabIndex, DateTime cutoff, int percent)
    {
        EnsureServices();

        int changedCount = 0;

        MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
        if (allTabs == null || allTabs.Tabs == null)
            return 0;

        MerchTabDump targetTab = null;
        for (int i = 0; i < allTabs.Tabs.Count; i++)
        {
            if (allTabs.Tabs[i].TabIndex == requestedTabIndex)
            {
                targetTab = allTabs.Tabs[i];
                break;
            }
        }

        if (targetTab == null || targetTab.Items == null || targetTab.Items.Count == 0)
            return 0;

        _lastStatus = "Opening tab [" + requestedTabIndex + "] for repricing";

        bool opened = await _ui.OpenMerchantTabByIndex(requestedTabIndex);
        if (!opened)
        {
            Log("REPRICE APPLY: failed to open tab [" + requestedTabIndex + "]");
            return 0;
        }

        await RandomDelay(380, 620);

        dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
        if (ingameUi == null)
            return 0;

        dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);
        if (pages == null)
            return 0;

        int activePageIndex = _ui.GetActivePageIndex(pages);
        if (activePageIndex < 0)
            return 0;

        if (activePageIndex != requestedTabIndex)
        {
            Log("REPRICE APPLY: active tab mismatch, expected " + requestedTabIndex + " got " + activePageIndex);
            return 0;
        }

        for (int i = 0; i < targetTab.Items.Count; i++)
        {
            MerchStoredItem item = targetTab.Items[i];
            if (!ShouldRepriceChaosItem(item, cutoff))
                continue;

            int oldAmount = item.PriceAmount ?? 0;
            int newAmount = CalculateRepricedChaosAmount(oldAmount, percent);
            if (newAmount == oldAmount)
                continue;

            _lastStatus = "Repricing tab [" + requestedTabIndex + "] item " + (i + 1) + "/" + targetTab.Items.Count;

            bool success = await ApplyRepriceToItem(item, newAmount);
            if (!success)
                continue;

            item.PriceAmount = newAmount;
            item.PriceCurrency = "chaos";
            item.PriceRaw = "Note: ~b/o " + newAmount + " chaos";
            item.PriceNoteRaw = "Note: ~b/o " + newAmount + " chaos";
            item.PricedAt = DateTime.Now;

            changedCount++;

            _storage.SaveAllTabsFile(allTabs);
            await RandomDelay(220, 420);
        }

        targetTab.LastScanned = DateTime.Now;
        _storage.SaveAllTabsFile(allTabs);

        return changedCount;
    }

    private bool ShouldRepriceChaosItem(MerchStoredItem item, DateTime cutoff)
    {
        if (item == null)
            return false;

        if (!item.PriceAmount.HasValue)
            return false;

        if (item.PriceAmount.Value < 1)
            return false;

        if (string.IsNullOrWhiteSpace(item.PriceCurrency))
            return false;

        string currency = item.PriceCurrency.Trim().ToLowerInvariant();
        if (currency.Contains("div"))
            return false;

        if (!currency.Contains("chaos"))
            return false;

        if (item.PricedAt == DateTime.MinValue)
            return false;

        return item.PricedAt <= cutoff;
    }

    private int CalculateRepricedChaosAmount(int oldAmount, int percent)
    {
        if (oldAmount <= 0)
            return 0;

        double reduced = oldAmount * (100.0 - percent) / 100.0;
        int newAmount = (int)Math.Floor(reduced);

        if (newAmount < 1)
            newAmount = 1;

        return newAmount;
    }

    private async Task<bool> ApplyRepriceToItem(MerchStoredItem item, int newAmount)
    {
        try
        {
            var win = GameController.Window.GetWindowRectangle();
            float cx = item.X + item.W / 2f;
            float cy = item.Y + item.H / 2f;

            await _ui.MoveAndRightClick(new System.Numerics.Vector2(win.X + cx, win.Y + cy));
            await RandomDelay(240, 430);

            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
                return false;

            dynamic valueInput = _ui.GetElementByPath(ingameUi, RepriceValueInputPath);
            dynamic listItem = _ui.GetElementByPath(ingameUi, RepriceListItemPath);

            if (valueInput == null || listItem == null)
            {
                Log("REPRICE APPLY: repricing UI nodes not found");
                return false;
            }

            if (!await _ui.ClickElementCenterHumanized(valueInput))
                return false;

            await RandomDelay(110, 220);
            ReplaceFocusedText(newAmount.ToString());
            await RandomDelay(140, 260);

            if (!await _ui.ClickElementCenterHumanized(listItem))
                return false;

            await RandomDelay(260, 460);
            return true;
        }
        catch (Exception ex)
        {
            Log("REPRICE APPLY ITEM ERROR: " + ex.Message);
            return false;
        }
    }

    private void ReplaceFocusedText(string text)
    {
        SetClipboardTextSafe(text);
        SleepRandom(35, 70);
        SendCtrlA();
        SleepRandom(35, 70);
        SendCtrlV();
        SleepRandom(45, 90);
    }

    private void SendCtrlA()
    {
        try
        {
            Input.KeyDown(Keys.ControlKey);
            SleepRandom(18, 34);
            Input.KeyDown(Keys.A);
            SleepRandom(24, 48);
            Input.KeyUp(Keys.A);
            SleepRandom(18, 34);
            Input.KeyUp(Keys.ControlKey);
        }
        catch
        {
        }
    }

    private void SendCtrlV()
    {
        try
        {
            Input.KeyDown(Keys.ControlKey);
            SleepRandom(18, 34);
            Input.KeyDown(Keys.V);
            SleepRandom(24, 48);
            Input.KeyUp(Keys.V);
            SleepRandom(18, 34);
            Input.KeyUp(Keys.ControlKey);
        }
        catch
        {
        }
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

    private async Task<TabUpdateResult> UpdateSingleTabInternal(int requestedTabIndex, bool withFullPriceCheck)
    {
        EnsureServices();

        var result = new TabUpdateResult();

        dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
        if (ingameUi == null)
            return result;

        dynamic dropdown = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabDropdownPath);
        dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);

        if (dropdown == null || pages == null)
            return result;

        List<MerchantTabRow> tabRows = _ui.GetMerchantTabRowsFromKnownDropdown(dropdown);
        if (requestedTabIndex < 0 || requestedTabIndex >= tabRows.Count)
            return result;

        _lastStatus = "Opening tab [" + requestedTabIndex + "] " + _ui.CleanupTabName(tabRows[requestedTabIndex].Name);

        bool opened = await _ui.OpenMerchantTabByIndex(requestedTabIndex);
        if (!opened)
            return result;

        await RandomDelay(360, 620);

        dynamic refreshedUi = GameController?.Game?.IngameState?.IngameUi;
        if (refreshedUi == null)
            return result;

        dynamic refreshedPages = _ui.GetElementByPath(refreshedUi, MerchantUiService.MerchantTabPagesPath);
        if (refreshedPages == null)
            return result;

        int activePageIndex = _ui.GetActivePageIndex(refreshedPages);
        if (activePageIndex < 0)
            return result;

        dynamic activePage = null;
        try { activePage = refreshedPages.GetChildAtIndex(activePageIndex); } catch { }

        if (activePage == null)
            return result;

        var currentItems = new List<MerchantItemInfo>();
        _scan.CollectInventoryItems(activePage, "activePage/" + activePageIndex, currentItems);

        string activeTabName = requestedTabIndex < tabRows.Count
            ? _ui.CleanupTabName(tabRows[requestedTabIndex].Name)
            : "Unknown";

        if (!string.IsNullOrWhiteSpace(activeTabName) && activeTabName != "-" && activeTabName != "Unknown")
        {
            if (!Settings.TabNames.ContainsKey(activePageIndex) || Settings.TabNames[activePageIndex] != activeTabName)
                Settings.TabNames[activePageIndex] = activeTabName;
        }

        MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
        allTabs.LastUpdated = DateTime.Now;

        if (allTabs.KnownTabs == null)
            allTabs.KnownTabs = new List<MerchKnownTab>();

        RebuildKnownTabs(allTabs);

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

        var oldItemsByKey = new Dictionary<string, MerchStoredItem>();
        for (int i = 0; i < targetTab.Items.Count; i++)
        {
            MerchStoredItem oldItem = targetTab.Items[i];
            string key = MerchantItemKey.Build(activePageIndex, oldItem.EntityPath, oldItem.X, oldItem.Y, oldItem.W, oldItem.H);
            if (!oldItemsByKey.ContainsKey(key))
                oldItemsByKey.Add(key, oldItem);
        }

        var currentKeys = new HashSet<string>();
        var newItems = new List<MerchantItemInfo>();

        for (int i = 0; i < currentItems.Count; i++)
        {
            MerchantItemInfo item = currentItems[i];
            string key = MerchantItemKey.Build(activePageIndex, item.EntityPath, item.X, item.Y, item.W, item.H);
            currentKeys.Add(key);

            if (!oldItemsByKey.ContainsKey(key))
                newItems.Add(item);
        }

        int removedCount = 0;
        foreach (KeyValuePair<string, MerchStoredItem> kv in oldItemsByKey)
        {
            if (!currentKeys.Contains(kv.Key))
                removedCount++;
        }

        result.NewItems = newItems.Count;
        result.RemovedItems = removedCount;

        if (withFullPriceCheck)
        {
            await _scan.PopulatePricesForCurrentTabItems(currentItems);
        }
        else if (newItems.Count > 0)
        {
            await _scan.PopulatePricesForCurrentTabItems(newItems);
        }

        var mergedItems = new List<MerchStoredItem>();

        for (int i = 0; i < currentItems.Count; i++)
        {
            MerchantItemInfo src = currentItems[i];
            string key = MerchantItemKey.Build(activePageIndex, src.EntityPath, src.X, src.Y, src.W, src.H);

            MerchStoredItem old = null;
            if (oldItemsByKey.ContainsKey(key))
                old = oldItemsByKey[key];

            if (old == null)
            {
                mergedItems.Add(new MerchStoredItem
                {
                    UiPath = src.UiPath,
                    EntityPath = src.EntityPath,
                    X = src.X,
                    Y = src.Y,
                    W = src.W,
                    H = src.H,
                    FirstSeenAt = DateTime.Now,
                    PriceRaw = src.PriceRaw,
                    PriceAmount = src.PriceAmount,
                    PriceCurrency = src.PriceCurrency,
                    PriceNoteRaw = src.PriceNoteRaw,
                    PricedAt = src.PricedAt
                });
            }
            else
            {
                string newPriceRaw = old.PriceRaw;
                int? newPriceAmount = old.PriceAmount;
                string newPriceCurrency = old.PriceCurrency;
                string newPriceNoteRaw = old.PriceNoteRaw;
                DateTime newPricedAt = old.PricedAt;

                if (withFullPriceCheck)
                {
                    bool priceChanged =
                        !StringEquals(old.PriceRaw, src.PriceRaw) ||
                        old.PriceAmount != src.PriceAmount ||
                        !StringEquals(old.PriceCurrency, src.PriceCurrency) ||
                        !StringEquals(old.PriceNoteRaw, src.PriceNoteRaw);

                    if (priceChanged)
                    {
                        newPriceRaw = src.PriceRaw;
                        newPriceAmount = src.PriceAmount;
                        newPriceCurrency = src.PriceCurrency;
                        newPriceNoteRaw = src.PriceNoteRaw;
                        newPricedAt = src.PricedAt != DateTime.MinValue ? src.PricedAt : DateTime.Now;
                    }
                }

                mergedItems.Add(new MerchStoredItem
                {
                    UiPath = src.UiPath,
                    EntityPath = src.EntityPath,
                    X = src.X,
                    Y = src.Y,
                    W = src.W,
                    H = src.H,
                    FirstSeenAt = old.FirstSeenAt != DateTime.MinValue ? old.FirstSeenAt : DateTime.Now,
                    PriceRaw = newPriceRaw,
                    PriceAmount = newPriceAmount,
                    PriceCurrency = newPriceCurrency,
                    PriceNoteRaw = newPriceNoteRaw,
                    PricedAt = newPricedAt
                });
            }
        }

        targetTab.TabName = activeTabName;
        targetTab.LastScanned = DateTime.Now;
        targetTab.ItemCount = currentItems.Count;
        targetTab.Items = mergedItems;

        allTabs.Tabs.Sort(delegate (MerchTabDump a, MerchTabDump b)
        {
            return a.TabIndex.CompareTo(b.TabIndex);
        });

        allTabs.KnownTabs.Sort(delegate (MerchKnownTab a, MerchKnownTab b)
        {
            return a.Index.CompareTo(b.Index);
        });

        _storage.SaveAllTabsFile(allTabs);

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
                item.PriceNoteRaw,
                item.PricedAt
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

        return result;
    }

    private async Task<TabPriceUpdateResult> CheckSingleTabPricesInternal(int requestedTabIndex)
    {
        EnsureServices();

        var result = new TabPriceUpdateResult();

        dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
        if (ingameUi == null)
            return result;

        dynamic dropdown = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabDropdownPath);
        dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);

        if (dropdown == null || pages == null)
            return result;

        List<MerchantTabRow> tabRows = _ui.GetMerchantTabRowsFromKnownDropdown(dropdown);
        if (requestedTabIndex < 0 || requestedTabIndex >= tabRows.Count)
            return result;

        _lastStatus = "Opening tab [" + requestedTabIndex + "] " + _ui.CleanupTabName(tabRows[requestedTabIndex].Name);

        bool opened = await _ui.OpenMerchantTabByIndex(requestedTabIndex);
        if (!opened)
            return result;

        await RandomDelay(360, 620);

        dynamic refreshedUi = GameController?.Game?.IngameState?.IngameUi;
        if (refreshedUi == null)
            return result;

        dynamic refreshedPages = _ui.GetElementByPath(refreshedUi, MerchantUiService.MerchantTabPagesPath);
        if (refreshedPages == null)
            return result;

        int activePageIndex = _ui.GetActivePageIndex(refreshedPages);
        if (activePageIndex < 0)
            return result;

        dynamic activePage = null;
        try { activePage = refreshedPages.GetChildAtIndex(activePageIndex); } catch { }

        if (activePage == null)
            return result;

        var currentItems = new List<MerchantItemInfo>();
        _scan.CollectInventoryItems(activePage, "activePage/" + activePageIndex, currentItems);
        await _scan.PopulatePricesForCurrentTabItems(currentItems);

        MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
        allTabs.LastUpdated = DateTime.Now;

        MerchTabDump targetTab = null;
        for (int i = 0; i < allTabs.Tabs.Count; i++)
        {
            if (allTabs.Tabs[i].TabIndex == activePageIndex)
            {
                targetTab = allTabs.Tabs[i];
                break;
            }
        }

        if (targetTab == null || targetTab.Items == null)
            return result;

        var storedByKey = new Dictionary<string, MerchStoredItem>();
        for (int i = 0; i < targetTab.Items.Count; i++)
        {
            MerchStoredItem stored = targetTab.Items[i];
            string key = MerchantItemKey.Build(activePageIndex, stored.EntityPath, stored.X, stored.Y, stored.W, stored.H);
            if (!storedByKey.ContainsKey(key))
                storedByKey.Add(key, stored);
        }

        for (int i = 0; i < currentItems.Count; i++)
        {
            MerchantItemInfo scanned = currentItems[i];
            string key = MerchantItemKey.Build(activePageIndex, scanned.EntityPath, scanned.X, scanned.Y, scanned.W, scanned.H);

            if (!storedByKey.ContainsKey(key))
                continue;

            MerchStoredItem stored = storedByKey[key];

            bool changed =
                !StringEquals(stored.PriceRaw, scanned.PriceRaw) ||
                stored.PriceAmount != scanned.PriceAmount ||
                !StringEquals(stored.PriceCurrency, scanned.PriceCurrency) ||
                !StringEquals(stored.PriceNoteRaw, scanned.PriceNoteRaw);

            if (!changed)
                continue;

            stored.PriceRaw = scanned.PriceRaw;
            stored.PriceAmount = scanned.PriceAmount;
            stored.PriceCurrency = scanned.PriceCurrency;
            stored.PriceNoteRaw = scanned.PriceNoteRaw;
            stored.PricedAt = scanned.PricedAt != DateTime.MinValue ? scanned.PricedAt : DateTime.Now;

            result.RepricedItems++;
        }

        targetTab.LastScanned = DateTime.Now;
        targetTab.ItemCount = currentItems.Count;

        allTabs.Tabs.Sort(delegate (MerchTabDump a, MerchTabDump b)
        {
            return a.TabIndex.CompareTo(b.TabIndex);
        });

        allTabs.KnownTabs.Sort(delegate (MerchKnownTab a, MerchKnownTab b)
        {
            return a.Index.CompareTo(b.Index);
        });

        _storage.SaveAllTabsFile(allTabs);

        return result;
    }

    private async Task CheckRepricedItemsFromSelectedTabs()
    {
        EnsureServices();

        if (_isRunningRepriceCheck)
            return;

        _isRunningRepriceCheck = true;
        _lastStatus = "Checking repriced items in selected tabs...";
        Log("REPRICE CHECK: started");

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("REPRICE CHECK: IngameUi is null");
                return;
            }

            dynamic dropdown = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabDropdownPath);
            if (dropdown == null)
            {
                Log("REPRICE CHECK: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = _ui.GetMerchantTabRowsFromKnownDropdown(dropdown);
            if (tabRows.Count == 0)
            {
                Log("REPRICE CHECK: No tab rows found.");
                return;
            }

            int checkedTabs = 0;
            int repricedCount = 0;

            for (int i = 0; i < tabRows.Count; i++)
            {
                bool selected = Settings.SelectedTabs.ContainsKey(i) && Settings.SelectedTabs[i];
                if (!selected)
                    continue;

                checkedTabs++;
                TabPriceUpdateResult result = await CheckSingleTabPricesInternal(i);
                repricedCount += result.RepricedItems;
            }

            _lastStatus = "Reprice check complete: checked " + checkedTabs + " tabs, updated " + repricedCount + " items";
            Log("REPRICE CHECK: complete, checked " + checkedTabs + " tabs, updated " + repricedCount + " items");
        }
        catch (Exception ex)
        {
            Log("REPRICE CHECK ERROR: " + ex.Message);
            _lastStatus = "Reprice check error: " + ex.Message;
        }
        finally
        {
            _isRunningRepriceCheck = false;
        }
    }

    private async Task DumpCurrentlyActiveMerchantTabItemsInternal(bool withPrices)
    {
        EnsureServices();

        try
        {
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                Log("ITEM DUMP: IngameUi is null");
                return;
            }

            dynamic dropdown = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabDropdownPath);
            dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);

            if (dropdown == null || pages == null)
            {
                Log("ITEM DUMP: Merchant UI not found.");
                return;
            }

            List<MerchantTabRow> tabRows = _ui.GetMerchantTabRowsFromKnownDropdown(dropdown);
            int activePageIndex = _ui.GetActivePageIndex(pages);

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
            _scan.CollectInventoryItems(activePage, "activePage/" + activePageIndex, items);

            string activeTabName = "Unknown";
            if (activePageIndex >= 0 && activePageIndex < tabRows.Count)
                activeTabName = _ui.CleanupTabName(tabRows[activePageIndex].Name);

            if (!string.IsNullOrWhiteSpace(activeTabName) && activeTabName != "-" && activeTabName != "Unknown")
            {
                if (!Settings.TabNames.ContainsKey(activePageIndex) || Settings.TabNames[activePageIndex] != activeTabName)
                    Settings.TabNames[activePageIndex] = activeTabName;
            }

            if (withPrices)
                await _scan.PopulatePricesForCurrentTabItems(items);

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
                    item.PriceNoteRaw,
                    item.PricedAt
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

    private void UpdateAllTabsFile(int activeTabIndex, string activeTabName, List<MerchantItemInfo> items)
    {
        EnsureServices();

        try
        {
            MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
            allTabs.LastUpdated = DateTime.Now;

            if (allTabs.KnownTabs == null)
                allTabs.KnownTabs = new List<MerchKnownTab>();

            RebuildKnownTabs(allTabs);

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
                string oldKey = MerchantItemKey.Build(activeTabIndex, oldItem.EntityPath, oldItem.X, oldItem.Y, oldItem.W, oldItem.H);
                if (!oldItemsByKey.ContainsKey(oldKey))
                    oldItemsByKey.Add(oldKey, oldItem);
            }

            var newStoredItems = new List<MerchStoredItem>();

            for (int i = 0; i < items.Count; i++)
            {
                MerchantItemInfo src = items[i];
                string newKey = MerchantItemKey.Build(activeTabIndex, src.EntityPath, src.X, src.Y, src.W, src.H);

                DateTime firstSeenAt = DateTime.Now;
                string oldPriceRaw = null;
                int? oldPriceAmount = null;
                string oldPriceCurrency = null;
                string oldPriceNoteRaw = null;
                DateTime oldPricedAt = DateTime.MinValue;

                if (oldItemsByKey.ContainsKey(newKey))
                {
                    MerchStoredItem old = oldItemsByKey[newKey];
                    if (old.FirstSeenAt != DateTime.MinValue)
                        firstSeenAt = old.FirstSeenAt;

                    oldPriceRaw = old.PriceRaw;
                    oldPriceAmount = old.PriceAmount;
                    oldPriceCurrency = old.PriceCurrency;
                    oldPriceNoteRaw = old.PriceNoteRaw;
                    oldPricedAt = old.PricedAt;
                }

                DateTime pricedAt = src.PricedAt != DateTime.MinValue ? src.PricedAt : oldPricedAt;

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
                    PriceNoteRaw = !string.IsNullOrWhiteSpace(src.PriceNoteRaw) ? src.PriceNoteRaw : oldPriceNoteRaw,
                    PricedAt = pricedAt
                });
            }

            targetTab.Items = newStoredItems;

            allTabs.Tabs.Sort(delegate (MerchTabDump a, MerchTabDump b) { return a.TabIndex.CompareTo(b.TabIndex); });
            allTabs.KnownTabs.Sort(delegate (MerchKnownTab a, MerchKnownTab b) { return a.Index.CompareTo(b.Index); });

            _storage.SaveAllTabsFile(allTabs);
        }
        catch (Exception ex)
        {
            Log("ALL TABS UPDATE ERROR: " + ex.Message);
        }
    }

    private void HighlightOldItemsInCurrentTab()
    {
        EnsureServices();

        try
        {
            _highlightBoxes.Clear();
            _highlightEnabled = false;
            _highlightTabIndex = -1;

            MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                _lastStatus = "Highlight failed: IngameUi null";
                return;
            }

            dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);
            if (pages == null)
            {
                _lastStatus = "Highlight failed: merchant UI not found";
                return;
            }

            int activePageIndex = _ui.GetActivePageIndex(pages);
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

    private void HighlightLastRepricedOldItemsInCurrentTab()
    {
        EnsureServices();

        try
        {
            _highlightRepriceBoxes.Clear();
            _highlightRepriceEnabled = false;
            _highlightRepriceTabIndex = -1;

            MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                _lastStatus = "Reprice highlight failed: IngameUi null";
                return;
            }

            dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);
            if (pages == null)
            {
                _lastStatus = "Reprice highlight failed: merchant UI not found";
                return;
            }

            int activePageIndex = _ui.GetActivePageIndex(pages);
            if (activePageIndex < 0)
            {
                _lastStatus = "Reprice highlight failed: no active merchant tab";
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

            DateTime cutoff = DateTime.Now.AddHours(-_highlightRepriceHours);

            for (int i = 0; i < tabDump.Items.Count; i++)
            {
                MerchStoredItem item = tabDump.Items[i];
                if (item.PricedAt != DateTime.MinValue && item.PricedAt <= cutoff)
                {
                    _highlightRepriceBoxes.Add(new HighlightBox
                    {
                        X = item.X,
                        Y = item.Y,
                        W = item.W,
                        H = item.H
                    });
                }
            }

            _highlightRepriceEnabled = true;
            _highlightRepriceTabIndex = activePageIndex;
            _lastStatus = "Highlighted " + _highlightRepriceBoxes.Count + " items priced older than " + _highlightRepriceHours + "h";
        }
        catch (Exception ex)
        {
            Log("REPRICE HIGHLIGHT ERROR: " + ex.Message);
            _lastStatus = "Reprice highlight error: " + ex.Message;
        }
    }

    private void HighlightCheapItemsWithOldPricesInCurrentTab()
    {
        EnsureServices();

        try
        {
            _highlightCheapOldPriceBoxes.Clear();
            _highlightCheapOldPriceEnabled = false;
            _highlightCheapOldPriceTabIndex = -1;

            MerchAllTabsDump allTabs = _storage.LoadAllTabsFile();
            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
            {
                _lastStatus = "Cheap/old-price highlight failed: IngameUi null";
                return;
            }

            dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);
            if (pages == null)
            {
                _lastStatus = "Cheap/old-price highlight failed: merchant UI not found";
                return;
            }

            int activePageIndex = _ui.GetActivePageIndex(pages);
            if (activePageIndex < 0)
            {
                _lastStatus = "Cheap/old-price highlight failed: no active merchant tab";
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

            DateTime cutoff = DateTime.Now.AddHours(-_highlightCheapOldPriceHours);

            for (int i = 0; i < tabDump.Items.Count; i++)
            {
                MerchStoredItem item = tabDump.Items[i];
                if (item == null)
                    continue;

                if (!item.PriceAmount.HasValue)
                    continue;

                if (item.PriceAmount.Value > _highlightCheapOldPriceMaxChaos)
                    continue;

                if (string.IsNullOrWhiteSpace(item.PriceCurrency))
                    continue;

                string currency = item.PriceCurrency.Trim().ToLowerInvariant();
                if (!currency.Contains("chaos"))
                    continue;

                if (currency.Contains("div"))
                    continue;

                if (item.PricedAt == DateTime.MinValue)
                    continue;

                if (item.PricedAt > cutoff)
                    continue;

                _highlightCheapOldPriceBoxes.Add(new HighlightBox
                {
                    X = item.X,
                    Y = item.Y,
                    W = item.W,
                    H = item.H
                });
            }

            _highlightCheapOldPriceEnabled = true;
            _highlightCheapOldPriceTabIndex = activePageIndex;
            _lastStatus = "Highlighted " + _highlightCheapOldPriceBoxes.Count +
                          " items <= " + _highlightCheapOldPriceMaxChaos +
                          " chaos with price older than " + _highlightCheapOldPriceHours + "h";
        }
        catch (Exception ex)
        {
            Log("CHEAP/OLD PRICE HIGHLIGHT ERROR: " + ex.Message);
            _lastStatus = "Cheap/old-price highlight error: " + ex.Message;
        }
    }

    private bool StringEquals(string a, string b)
    {
        return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    }

    private void Log(string msg)
    {
        DebugWindow.LogMsg("[MerchTab] " + msg);
    }

    public override void DrawSettings()
    {
        EnsureServices();

        ImGuiNET.ImGui.TextDisabled("Open Faustus -> Merchant and expand the right-side tab dropdown");
        ImGuiNET.ImGui.Spacing();

        bool buttonDisabled = _isRunningGetItems || _isRunningTabUpdate || _isRunningRepriceCheck || _isRunningApplyReprice || _showStartCountdown;
        if (buttonDisabled)
            ImGuiNET.ImGui.BeginDisabled();

        if (ImGuiNET.ImGui.Button("Update Merchtab Names"))
            _ = RunWithStartCountdown(() =>
            {
                ScanMerchantTabs();
                return Task.CompletedTask;
            });

        if (ImGuiNET.ImGui.Button("Save all tab items without price"))
            _ = RunWithStartCountdown(() => GetItemsFromSelectedTabs(false));

        if (ImGuiNET.ImGui.Button("Save all tab items with price"))
            _ = RunWithStartCountdown(() => GetItemsFromSelectedTabs(true));

        if (ImGuiNET.ImGui.Button("I repriced items"))
            _ = RunWithStartCountdown(() => CheckRepricedItemsFromSelectedTabs());

        if (ImGuiNET.ImGui.Button("Update Tabs (New items added, Items removed/sold)"))
            _ = RunWithStartCountdown(() => UpdateSelectedTabsForNewAndRemovedItems());

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

                string name = Settings.TabNames.ContainsKey(idx) ? _ui.CleanupTabName(Settings.TabNames[idx]) : "(unknown)";
                bool selected = Settings.SelectedTabs.ContainsKey(idx) ? Settings.SelectedTabs[idx] : true;
                if (!Settings.SelectedTabs.ContainsKey(idx))
                    Settings.SelectedTabs[idx] = true;

                if (ImGuiNET.ImGui.Checkbox("##tab_" + idx, ref selected))
                    Settings.SelectedTabs[idx] = selected;

                ImGuiNET.ImGui.SameLine();
                string summary = _storage.BuildTabSummaryText(idx);
                ImGuiNET.ImGui.Text("[" + idx + "] " + name + " " + summary);

                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.Button("UPDATE##tabupdate_" + idx))
                {
                    int capturedIdx = idx;
                    _ = RunUpdateWithOptionalCountdown(capturedIdx, () => UpdateSingleTabOnly(capturedIdx));
                }

                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.Button("PRICE UPDATE##tabpriceupdate_" + idx))
                {
                    int capturedIdx = idx;
                    _ = RunWithStartCountdown(() => PriceUpdateSingleTabOnly(capturedIdx));
                }
            }

            ImGuiNET.ImGui.Spacing();
            ImGuiNET.ImGui.TextDisabled(_storage.BuildSelectedTabsTotalSummaryText(Settings.SelectedTabs));
        }

        ImGuiNET.ImGui.Spacing();
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.Spacing();

        int oldItemHours = Settings.HighlightOlderThanHours;
        ImGuiNET.ImGui.Text("Highlight items older than (hours):");
        ImGuiNET.ImGui.SetNextItemWidth(120f);
        if (ImGuiNET.ImGui.InputInt("##olderhours", ref oldItemHours))
        {
            if (oldItemHours < 1) oldItemHours = 1;
            if (oldItemHours > 9999) oldItemHours = 9999;
            Settings.HighlightOlderThanHours = oldItemHours;
        }

        ImGuiNET.ImGui.Spacing();

        if (ImGuiNET.ImGui.Button("Highlight Old Items"))
        {
            _highlightHours = Settings.HighlightOlderThanHours;
            HighlightOldItemsInCurrentTab();
        }

        ImGuiNET.ImGui.SameLine();

        if (ImGuiNET.ImGui.Button("Clear Old Item Highlights"))
        {
            _highlightEnabled = false;
            _highlightBoxes.Clear();
            _highlightTabIndex = -1;
            _lastStatus = "Old item highlights cleared";
        }

        ImGuiNET.ImGui.Spacing();
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.Spacing();

        int priceAgeHours = Settings.HighlightLastRepriceOlderThanHours;
        ImGuiNET.ImGui.Text("Highlight items priced older than (hours):");
        ImGuiNET.ImGui.SetNextItemWidth(120f);
        if (ImGuiNET.ImGui.InputInt("##repriceolderhours", ref priceAgeHours))
        {
            if (priceAgeHours < 1) priceAgeHours = 1;
            if (priceAgeHours > 9999) priceAgeHours = 9999;
            Settings.HighlightLastRepriceOlderThanHours = priceAgeHours;
        }

        ImGuiNET.ImGui.Spacing();

        if (ImGuiNET.ImGui.Button("Highlight Old Prices"))
        {
            _highlightRepriceHours = Settings.HighlightLastRepriceOlderThanHours;
            HighlightLastRepricedOldItemsInCurrentTab();
        }

        ImGuiNET.ImGui.SameLine();

        if (ImGuiNET.ImGui.Button("Clear Price Highlights"))
        {
            _highlightRepriceEnabled = false;
            _highlightRepriceBoxes.Clear();
            _highlightRepriceTabIndex = -1;
            _lastStatus = "Price highlights cleared";
        }

        ImGuiNET.ImGui.Spacing();
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.Spacing();

        ImGuiNET.ImGui.Text("Highlight items <= x chaos with last price change older than y hours:");

        int cheapChaos = _highlightCheapOldPriceMaxChaos;
        ImGuiNET.ImGui.SetNextItemWidth(90f);
        if (ImGuiNET.ImGui.InputInt("##cheapoldpricechaos", ref cheapChaos))
        {
            if (cheapChaos < 1) cheapChaos = 1;
            if (cheapChaos > 9999) cheapChaos = 9999;
            _highlightCheapOldPriceMaxChaos = cheapChaos;
        }

        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.Text("chaos");

        int cheapHours = _highlightCheapOldPriceHours;
        ImGuiNET.ImGui.SetNextItemWidth(90f);
        if (ImGuiNET.ImGui.InputInt("##cheapoldpricehours", ref cheapHours))
        {
            if (cheapHours < 1) cheapHours = 1;
            if (cheapHours > 9999) cheapHours = 9999;
            _highlightCheapOldPriceHours = cheapHours;
        }

        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.Text("hours");

        ImGuiNET.ImGui.Spacing();

        if (ImGuiNET.ImGui.Button("Highlight Cheap Old Prices"))
            HighlightCheapItemsWithOldPricesInCurrentTab();

        ImGuiNET.ImGui.SameLine();

        if (ImGuiNET.ImGui.Button("Clear Cheap Old Price Highlights"))
        {
            _highlightCheapOldPriceEnabled = false;
            _highlightCheapOldPriceBoxes.Clear();
            _highlightCheapOldPriceTabIndex = -1;
            _lastStatus = "Cheap old price highlights cleared";
        }

        ImGuiNET.ImGui.Spacing();
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.Spacing();

        ImGuiNET.ImGui.Text("Reprice chaos items");

        int repriceHours = Settings.RepriceOlderThanHours;
        ImGuiNET.ImGui.Text(">= ");
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(90f);
        if (ImGuiNET.ImGui.InputInt("##repriceapplyhours", ref repriceHours))
        {
            if (repriceHours < 1) repriceHours = 1;
            if (repriceHours > 9999) repriceHours = 9999;
            Settings.RepriceOlderThanHours = repriceHours;
        }
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.Text("hours ago");

        int repricePercent = Settings.RepricePercent;
        ImGuiNET.ImGui.Text("by ");
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(90f);
        if (ImGuiNET.ImGui.InputInt("##repriceapplypercent", ref repricePercent))
        {
            if (repricePercent < 1) repricePercent = 1;
            if (repricePercent > 99) repricePercent = 99;
            Settings.RepricePercent = repricePercent;
        }
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.Text("% (rounded)");

        ImGuiNET.ImGui.Spacing();

        if (ImGuiNET.ImGui.Button("Reprice Items"))
            _ = RunWithStartCountdown(() => RepriceSelectedTabs());
    }

    public override void Render()
    {
        EnsureServices();

        try
        {
            if (_showStartCountdown)
            {
                int seconds = GetCountdownSecondsRemaining();
                var rect = GameController.Window.GetWindowRectangle();
                float centerX = rect.X + rect.Width / 2f;
                float centerY = rect.Y + rect.Height / 2f;

                Graphics.DrawText(_countdownMessage, new Vector2(centerX - 140, centerY - 30), Color.Red);

                string countdownText = seconds > 0 ? seconds.ToString() + "..." : "";
                if (!string.IsNullOrEmpty(countdownText))
                    Graphics.DrawText(countdownText, new Vector2(centerX - 20, centerY + 5), Color.Yellow);
            }

            dynamic ingameUi = GameController?.Game?.IngameState?.IngameUi;
            if (ingameUi == null)
                return;

            dynamic pages = _ui.GetElementByPath(ingameUi, MerchantUiService.MerchantTabPagesPath);
            if (pages == null)
                return;

            int activePageIndex = _ui.GetActivePageIndex(pages);

            if (_highlightEnabled && _highlightBoxes.Count > 0 && activePageIndex == _highlightTabIndex)
            {
                for (int i = 0; i < _highlightBoxes.Count; i++)
                {
                    HighlightBox b = _highlightBoxes[i];
                    Graphics.DrawFrame(new RectangleF(b.X, b.Y, b.W, b.H), Color.Red, 2);
                }
            }

            if (_highlightRepriceEnabled && _highlightRepriceBoxes.Count > 0 && activePageIndex == _highlightRepriceTabIndex)
            {
                for (int i = 0; i < _highlightRepriceBoxes.Count; i++)
                {
                    HighlightBox b = _highlightRepriceBoxes[i];
                    Graphics.DrawFrame(new RectangleF(b.X, b.Y, b.W, b.H), Color.Yellow, 2);
                }
            }

            if (_highlightCheapOldPriceEnabled && _highlightCheapOldPriceBoxes.Count > 0 && activePageIndex == _highlightCheapOldPriceTabIndex)
            {
                for (int i = 0; i < _highlightCheapOldPriceBoxes.Count; i++)
                {
                    HighlightBox b = _highlightCheapOldPriceBoxes[i];
                    Graphics.DrawFrame(new RectangleF(b.X, b.Y, b.W, b.H), Color.Orange, 2);
                }
            }
        }
        catch
        {
        }
    }

    private class TabUpdateResult
    {
        public int NewItems { get; set; }
        public int RemovedItems { get; set; }
    }

    private class TabPriceUpdateResult
    {
        public int RepricedItems { get; set; }
    }
}