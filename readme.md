# MerchTabPlugin

MerchTabPlugin is a helper plugin for managing Faustus merchant tabs in Path of Exile through ExileApi / ExileCore.

It is built to help with:

- scanning merchant tab names
- saving all listed items from merchant tabs
- saving item prices from listed items
- tracking when items were first seen
- tracking when an item's current price was last set
- updating sold / removed / newly added items
- checking for manual price changes
- highlighting items by age or pricing age
- automatically repricing low chaos items after a configured time

---

# What the plugin tracks

The plugin stores merchant tab data in JSON files.

Main things it remembers per item:

- tab index
- item entity path
- item UI position and size
- `FirstSeenAt`
- current price
- current price currency
- current price note
- `PricedAt`

## Meaning of the timestamps

### `FirstSeenAt`
The time when the item was first detected in the merchant tab.

This does **not** change on later rescans.

### `PricedAt`
The time when the currently stored price became active.

This is set when:

- the item is first scanned with a price
- a new item is added and its price is scanned
- a later price update detects that the price changed
- the plugin automatically reprices the item

This is the timestamp used for all price-age based highlighting and repricing logic.

---

# Files used by the plugin

## `merchant_tab_indices.json`
Stores the discovered merchant tabs and their names.

## `merchant_active_tab_items.json`
Stores the latest dump of the currently scanned active tab.

## `merchant_all_tabs_items.json`
Main storage file.

This contains all tracked merchant tabs and all tracked items across those tabs.

---

# How the plugin identifies items

Items are matched using a key built from:

- tab index
- entity path
- x
- y
- width
- height

That means the plugin can usually detect:

- removed items
- new items
- moved / replaced items

## Important limitation
If one item is sold and another item of the **same base type and same size** is placed into the **exact same slot**, the plugin may treat it as the same item.

That is a known limitation of UI-based matching.

---

# General usage flow

A common workflow looks like this:

1. Open Faustus merchant view
2. Expand the merchant tab dropdown
3. Click **Update Merchtab Names**
4. Select the tabs you want to manage
5. Click **Save all tab items with price**
6. Later, use:
   - **Update Tabs (New items added, Items removed/sold)**
   - **I repriced items**
   - highlights
   - **Reprice Items**

---

# Important note about mouse movement

Several actions move the mouse automatically.

When you click a button that starts an automated action, the plugin shows a short countdown and a warning to not move your mouse.

If you move the mouse during those actions, scanning / repricing may fail or click the wrong UI element.

---

# Buttons and what they do

## Update Merchtab Names
Scans the merchant tab dropdown and stores the discovered tab names and tab indices.

Use this when:

- you renamed tabs
- you want to refresh the list of available merchant tabs
- you want the plugin to know which tabs exist

---

## Save all tab items without price
Scans all selected merchant tabs and stores all items found there.

This saves:

- item positions
- entity paths
- `FirstSeenAt`

It does **not** scan prices.

Use this when you only want a fast inventory snapshot.

---

## Save all tab items with price
Scans all selected merchant tabs and stores all items found there, including prices.

For each item the plugin:

- hovers the item
- copies item text
- reads the note line
- extracts price and currency
- stores `PricedAt`

Use this for the first full pricing scan or whenever you want a complete refresh.

---

## I repriced items
Checks all selected tabs for manually changed prices.

This does **not** blindly overwrite all items.  
It compares the currently scanned price against the stored JSON.

If the price changed:

- the stored price is updated
- `PricedAt` is updated

If the price stayed the same:

- nothing changes

Use this after manually repricing items yourself.

---

## Update Tabs (New items added, Items removed/sold)
Checks all selected tabs for inventory changes.

This does two things:

- removes items from JSON if they are no longer present
- adds newly detected items to JSON

For newly added items, it also scans the price immediately.

This is the main sync button after your shop changed.

Use this when:

- items sold
- you added new items
- you want JSON to match the current state of your merchant tabs

---

# Per-tab buttons

Each tab row has two buttons.

## UPDATE
Updates **only that one tab** for:

- new items
- removed / sold items

Special behavior:

- if that tab is already the active tab, the plugin skips the startup countdown
- if another tab is active, the plugin uses the startup countdown and then switches to that tab

This button is useful for fast single-tab sync.

---

## PRICE UPDATE
Checks **only that one tab** for changed prices.

This always uses the startup countdown, because the plugin must move the mouse over items.

For each item in that tab:

- the plugin scans the current listed price
- compares with stored JSON
- updates the stored price if changed
- updates `PricedAt` if changed

Use this after manually repricing only one tab.

---

# Highlight features

Highlights only apply to the **currently open merchant tab**.

If you switch tabs, run the highlight button again for the new tab.

---

## Highlight Old Items
Highlights items whose `FirstSeenAt` is older than the configured number of hours.

This helps identify items that have been sitting in the shop for a long time.

---

## Highlight Old Prices
Highlights items whose `PricedAt` is older than the configured number of hours.

This helps identify items whose **current price** has been unchanged for a long time.

---

## Highlight Cheap Old Prices
Highlights items that are:

- priced in **chaos**
- priced at **less than or equal to X chaos**
- and whose `PricedAt` is older than **Y hours**

This is useful for finding cheap items that may be stale and ready for repricing or vendor cleanup.

---

## Clear buttons
Each highlight section has its own clear button.

These remove only that specific highlight overlay.

---

# Reprice feature

## Reprice chaos items
This feature automatically lowers prices of eligible chaos-priced items.

It uses two settings:

- `>= X hours ago`
- `by Y % (rounded)`

### Rules
Only items are repriced if:

- they have a chaos price
- they are **not** divine-priced
- `PricedAt` is older than or equal to the configured hours

### Rounding rule
The plugin always rounds down after the reduction, then clamps to minimum 1 chaos.

Examples:

- `15 chaos` with `10%` reduction -> `13 chaos`
- `2 chaos` with `10%` reduction -> `1 chaos`
- `1 chaos` with `10%` reduction -> stays `1 chaos`

### How repricing works
For each eligible item:

1. open the correct merchant tab
2. right click the item
3. locate the price input field
4. replace the value with the new chaos amount
5. click the list item button
6. update JSON immediately

After a successful reprice, the plugin updates:

- `PriceAmount`
- `PriceCurrency`
- `PriceRaw`
- `PriceNoteRaw`
- `PricedAt`

---

# JSON update behavior

## New items
When a new item is found:

- it is added to JSON
- `FirstSeenAt` is set
- if price is scanned, `PricedAt` is set too

## Removed items
When an item is no longer present in a tab:

- it is removed from JSON

## Changed prices
When a price changes:

- price fields are updated
- `PricedAt` is updated

---

# Delay / automation behavior

Many automated actions use a short startup delay and countdown overlay.

This exists to give you time to stop touching the mouse before the plugin starts moving it.

## Delay behavior by action

### Uses startup delay
- Save all tab items without price
- Save all tab items with price
- I repriced items
- Update Tabs (New items added, Items removed/sold)
- PRICE UPDATE
- Reprice Items

### Conditional startup delay
- UPDATE  
  If the selected tab is already the active merchant tab, the delay is skipped.  
  Otherwise, the delay is used before switching tabs.

---

# Recommended workflow examples

## First setup
1. Open Faustus merchant UI
2. Expand merchant tab dropdown
3. Click **Update Merchtab Names**
4. Check the tabs you want to include
5. Click **Save all tab items with price**

## After some items sold
1. Open merchant UI
2. Click **Update Tabs (New items added, Items removed/sold)**

## After manually repricing
1. Open merchant UI
2. Click **I repriced items**
   or use per-tab **PRICE UPDATE**

## To find stale items
1. Open a merchant tab
2. Use **Highlight Old Items**
3. Or use **Highlight Old Prices**
4. Or use **Highlight Cheap Old Prices**

## To auto-discount cheap chaos items
1. Set the repricing hours
2. Set the repricing percent
3. Click **Reprice Items**

---

# Known limitations

## UI-based matching
The plugin identifies items by entity path and UI slot-like position data.

If two items of the same base type and size occupy the same exact slot at different times, the plugin may treat them as the same item.

## Mouse dependency
Price scanning and repricing depend on the mouse being controlled by the plugin.

Do not move the mouse during automated actions.

## UI path dependency
Some functions rely on specific UI paths, especially the repricing UI:

- value input
- list item button

If the game UI changes in a future patch, those paths may need to be updated.

---

# Summary

MerchTabPlugin is a merchant management helper focused on:

- tracking what is listed
- tracking when items appeared
- tracking when prices were last changed
- detecting sold / new / changed items
- highlighting stale items
- helping with repricing

It is designed for a practical merchant workflow where you repeatedly:

- scan
- sync
- review
- highlight
- reprice

while keeping a JSON history of your shop state.