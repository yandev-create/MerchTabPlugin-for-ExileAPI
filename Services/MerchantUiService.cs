using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExileCore;
using MerchTabPlugin.Models;

namespace MerchTabPlugin.Services;

public class MerchantUiService
{
    private readonly GameController _gc;
    private readonly Random _rng = new Random();

    public static readonly int[] MerchantTabDropdownPath = { 44, 2, 0, 0, 1, 1, 0, 0, 1, 4, 2 };
    public static readonly int[] MerchantTabPagesPath = { 44, 2, 0, 0, 1, 1, 0, 0, 1, 1 };

    public MerchantUiService(GameController gc)
    {
        _gc = gc;
    }

    public dynamic GetElementByPath(dynamic root, int[] path)
    {
        dynamic cur = root;
        for (int i = 0; i < path.Length; i++)
        {
            if (cur == null)
                return null;

            try { cur = cur.GetChildAtIndex(path[i]); }
            catch { return null; }
        }

        return cur;
    }

    public List<MerchantTabRow> GetMerchantTabRowsFromKnownDropdown(dynamic dropdown)
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
            catch
            {
                continue;
            }

            if (w < 60f || h < 18f)
                continue;

            string name = CleanupTabName(ExtractDeepText(row));
            if (string.IsNullOrWhiteSpace(name) || name == "-")
                name = "Tab " + i;

            rows.Add(new MerchantTabRow
            {
                Index = i,
                Name = name,
                X = x,
                Y = y,
                W = w,
                H = h
            });
        }

        rows.Sort(delegate (MerchantTabRow a, MerchantTabRow b)
        {
            return a.Y.CompareTo(b.Y);
        });

        return rows;
    }

    public int GetActivePageIndex(dynamic pages)
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

    public async Task<bool> OpenMerchantTabByIndex(int index)
    {
        try
        {
            dynamic ingameUi = _gc?.Game?.IngameState?.IngameUi;
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

            var win = _gc.Window.GetWindowRectangle();
            Input.SetCursorPos(new System.Numerics.Vector2(win.X + x, win.Y + y));
            await Task.Delay(Next(65, 135));
            Input.LeftDown();
            await Task.Delay(Next(35, 85));
            Input.LeftUp();
            await Task.Delay(Next(220, 360));

            return true;
        }
        catch
        {
            return false;
        }
    }

    public string CleanupTabName(string raw)
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

    private int Next(int minInclusive, int maxInclusive)
    {
        lock (_rng)
        {
            return _rng.Next(minInclusive, maxInclusive + 1);
        }
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
        catch
        {
        }
    }
}