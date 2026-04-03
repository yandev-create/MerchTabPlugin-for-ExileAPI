using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
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
            try { cur = cur.GetChildAtIndex(path[i]); } catch { return null; }
        }
        return cur;
    }

    private int NextDelay(int minInclusive, int maxInclusive)
    {
        lock (_rng)
        {
            return _rng.Next(minInclusive, maxInclusive + 1);
        }
    }

    private float NextFloat(float minInclusive, float maxInclusive)
    {
        lock (_rng)
        {
            return (float)(_rng.NextDouble() * (maxInclusive - minInclusive) + minInclusive);
        }
    }

    private System.Numerics.Vector2 AddSmallRandomOffset(System.Numerics.Vector2 target, float maxOffsetPx = 4f)
    {
        return new System.Numerics.Vector2(
            target.X + NextFloat(-maxOffsetPx, maxOffsetPx),
            target.Y + NextFloat(-maxOffsetPx, maxOffsetPx));
    }

    public async System.Threading.Tasks.Task MoveMouseHumanized(System.Numerics.Vector2 targetScreenPos)
    {
        targetScreenPos = AddSmallRandomOffset(targetScreenPos, 4f);

        Point current;
        try { current = Cursor.Position; }
        catch { current = new Point((int)targetScreenPos.X, (int)targetScreenPos.Y); }

        var start = new System.Numerics.Vector2(current.X, current.Y);

        float distanceX = targetScreenPos.X - start.X;
        float distanceY = targetScreenPos.Y - start.Y;
        float distance = MathF.Sqrt(distanceX * distanceX + distanceY * distanceY);

        int steps = Math.Max(12, Math.Min(40, (int)(distance / 18f)));
        int totalMs = NextDelay(160, 360);

        float curveX = NextFloat(-18f, 18f);
        float curveY = NextFloat(-18f, 18f);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;

            float eased = t < 0.5f
                ? 2f * t * t
                : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f;

            float curveFactor = 4f * t * (1f - t);

            float x = start.X + distanceX * eased + curveX * curveFactor;
            float y = start.Y + distanceY * eased + curveY * curveFactor;

            Input.SetCursorPos(new System.Numerics.Vector2(x, y));
            await System.Threading.Tasks.Task.Delay(Math.Max(5, totalMs / steps));
        }

        Input.SetCursorPos(targetScreenPos);
        await System.Threading.Tasks.Task.Delay(NextDelay(25, 60));
    }

    public async System.Threading.Tasks.Task LeftClickHumanized()
    {
        await System.Threading.Tasks.Task.Delay(NextDelay(22, 65));
        Input.LeftDown();
        await System.Threading.Tasks.Task.Delay(NextDelay(38, 95));
        Input.LeftUp();
        await System.Threading.Tasks.Task.Delay(NextDelay(70, 180));
    }

    public async System.Threading.Tasks.Task RightClickHumanized()
    {
        await System.Threading.Tasks.Task.Delay(NextDelay(22, 65));
        Input.RightDown();
        await System.Threading.Tasks.Task.Delay(NextDelay(38, 95));
        Input.RightUp();
        await System.Threading.Tasks.Task.Delay(NextDelay(80, 190));
    }

    public async System.Threading.Tasks.Task MoveAndLeftClick(System.Numerics.Vector2 targetScreenPos)
    {
        await System.Threading.Tasks.Task.Delay(NextDelay(28, 95));
        await MoveMouseHumanized(targetScreenPos);
        await LeftClickHumanized();
    }

    public async System.Threading.Tasks.Task MoveAndRightClick(System.Numerics.Vector2 targetScreenPos)
    {
        await System.Threading.Tasks.Task.Delay(NextDelay(28, 95));
        await MoveMouseHumanized(targetScreenPos);
        await RightClickHumanized();
    }

    public async System.Threading.Tasks.Task<bool> ClickElementCenterHumanized(dynamic element)
    {
        try
        {
            dynamic rect = null;
            try { rect = element.GetClientRect(); } catch { }

            if (rect == null)
                return false;

            float x = (float)rect.X + (float)rect.Width / 2f;
            float y = (float)rect.Y + (float)rect.Height / 2f;
            var win = _gc.Window.GetWindowRectangle();

            await MoveAndLeftClick(new System.Numerics.Vector2(win.X + x, win.Y + y));
            await System.Threading.Tasks.Task.Delay(NextDelay(90, 180));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async System.Threading.Tasks.Task<bool> OpenMerchantTabByIndex(int index)
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
            await MoveAndLeftClick(new System.Numerics.Vector2(win.X + x, win.Y + y));
            await System.Threading.Tasks.Task.Delay(NextDelay(220, 420));

            return true;
        }
        catch
        {
            return false;
        }
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

    public string ExtractDeepText(dynamic el)
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
}