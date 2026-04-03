using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using MerchTabPlugin.Models;
using System.Windows.Forms;

namespace MerchTabPlugin.Services;

public class MerchantScanService
{
    private readonly GameController _gc;
    private readonly Action<string> _log;
    private readonly Random _rng = new Random();

    public MerchantScanService(GameController gc, Action<string> log)
    {
        _gc = gc;
        _log = log;
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

    public void CollectInventoryItems(dynamic el, string path, List<MerchantItemInfo> items)
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
                    catch
                    {
                    }
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
        catch
        {
        }
    }

    public async Task PopulatePricesForCurrentTabItems(List<MerchantItemInfo> items)
    {
        if (items == null || items.Count == 0)
            return;

        var ui = new MerchantUiService(_gc);
        var win = _gc.Window.GetWindowRectangle();

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                MerchantItemInfo item = items[i];

                float cx = item.X + item.W / 2f;
                float cy = item.Y + item.H / 2f;

                await ui.MoveMouseHumanized(new System.Numerics.Vector2(win.X + cx, win.Y + cy));
                await RandomDelay(220, 480);

                string before = GetClipboardTextSafe();
                SetClipboardTextSafe(string.Empty);
                await RandomDelay(40, 90);

                SendCtrlAltC();
                await RandomDelay(260, 460);

                string copied = GetClipboardTextSafe();

                if (string.IsNullOrWhiteSpace(copied) || copied == before)
                {
                    await RandomDelay(180, 340);
                    copied = GetClipboardTextSafe();
                }

                if (!string.IsNullOrWhiteSpace(copied) &&
                    copied.IndexOf("Item Class:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.PricedAt = DateTime.Now;
                    ParsePriceFromCopiedText(item, copied);
                }

                await RandomDelay(70, 160);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("PRICE SCAN ERROR: " + ex.Message);
        }
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

    private void SendCtrlAltC()
    {
        try
        {
            Input.KeyDown(Keys.ControlKey);
            SleepRandom(20, 40);
            Input.KeyDown(Keys.Menu);
            SleepRandom(20, 40);
            Input.KeyDown(Keys.C);
            SleepRandom(24, 52);
            Input.KeyUp(Keys.C);
            SleepRandom(18, 36);
            Input.KeyUp(Keys.Menu);
            SleepRandom(18, 36);
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

    private string Safe(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "-";

        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}