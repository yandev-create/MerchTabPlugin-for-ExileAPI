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

        var win = _gc.Window.GetWindowRectangle();

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                MerchantItemInfo item = items[i];

                float cx = item.X + item.W / 2f;
                float cy = item.Y + item.H / 2f;

                Input.SetCursorPos(new System.Numerics.Vector2(win.X + cx, win.Y + cy));
                await Task.Delay(Next(170, 300));

                string before = GetClipboardTextSafe();
                SetClipboardTextSafe(string.Empty);
                await Task.Delay(Next(30, 70));

                SendCtrlAltC();
                await Task.Delay(Next(230, 360));

                string copied = GetClipboardTextSafe();

                if (string.IsNullOrWhiteSpace(copied) || copied == before)
                {
                    await Task.Delay(Next(140, 260));
                    copied = GetClipboardTextSafe();
                }

                if (!string.IsNullOrWhiteSpace(copied) &&
                    copied.IndexOf("Item Class:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.PricedAt = DateTime.Now;
                    ParsePriceFromCopiedText(item, copied);
                }

                await Task.Delay(Next(40, 95));
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
            SleepRandom(15, 35);
            Input.KeyDown(Keys.Menu);
            SleepRandom(15, 35);
            Input.KeyDown(Keys.C);
            SleepRandom(20, 45);
            Input.KeyUp(Keys.C);
            SleepRandom(15, 35);
            Input.KeyUp(Keys.Menu);
            SleepRandom(15, 35);
            Input.KeyUp(Keys.ControlKey);
        }
        catch
        {
        }
    }

    private int Next(int minInclusive, int maxInclusive)
    {
        lock (_rng)
        {
            return _rng.Next(minInclusive, maxInclusive + 1);
        }
    }

    private void SleepRandom(int minInclusive, int maxInclusive)
    {
        Thread.Sleep(Next(minInclusive, maxInclusive));
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