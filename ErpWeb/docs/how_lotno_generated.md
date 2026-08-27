# How lot number is generated on miscellaneous receipt

This note explains how lot number works on the miscellaneous receipt entry screen (`IvMiscReceipt`), in plain language.

`InventoryLotEntryState` is a small helper that remembers **which item is currently open in the add/edit popup**, then decides what to do with the **lot number**.

Think of it as: **lot number belongs to the item, not to the popup.** If the user picks a different item, the old lot should not stay.

---

## Why it exists

On a miscellaneous receipt, some items need a lot number (and expiry date), and some do not.

The popup is reused for every line. Without this helper, a common mistake happens:

1. User adds Item A (lot-controlled) → lot `260827001` is filled.
2. User changes the item to Item B.
3. The old lot from Item A is still sitting there.

That would be wrong. The helper stops that.

It only remembers one thing: **the last item code in this popup**.

---

## When it is used on this screen

There are three moments:

**1. User clicks Add line**

The helper is cleared. The popup starts with no item and no lot.

**2. User clicks Edit on an existing line**

The helper is told: “this popup is already for item X.”
So if the user opens the line and does not change the item, the existing lot stays.

**3. User picks an item in the popup**

This is the main decision. The helper looks at the new item and returns one of three answers:

| Answer | Meaning | What the screen does |
|---|---|---|
| **Keep** | Same item (or nothing useful to change) | Leave lot and expiry as they are |
| **Clear** | This item does **not** use lot control | Empty lot number and expiry |
| **New lot** | Different item, and it **does** use lot control | Fill a new lot number, clear expiry |

---

## How it decides

When the user selects an item:

1. **Item has no lot control**
   Lot number is not needed. Clear it.

2. **Same item as before**
   Example: user is editing a line for `ITEM-A`, and picks `ITEM-A` again.
   Do not generate a new lot. Keep what is already there.

3. **Different item, and it uses lot control**
   Example: first pick, or user changed from `ITEM-A` to `ITEM-B`.
   Generate a new lot number.

That last case is why “Add line” and “change item” get a fresh lot, but “edit the same line and keep the same item” does not overwrite the lot.

---

## Where the lot number is generated

The lot number is generated **on this receipt screen**, not in the database and not when you save.

Two places work together.

**1. The helper decides “we need a new lot”**

In `InventoryLotEntryState.SelectItem` (`ErpWeb.UI/Inventory/InventoryLotEntryState.cs`):

- if the item has **no** lot control → return **Clear**
- if it is the **same item** already in the popup → return **Keep**
- if it is a **different** lot-controlled item → call `nextLotNo()` and return **New lot**

**2. This receipt page actually builds the number**

That `nextLotNo` is `NextLotNo()` in `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.cs`.

The number is made in the UI, from today’s date plus a running count of lots already on **this receipt**.

A new lot looks like:

**date + running number**

Example for 27 Aug 2026:

- first lot-controlled line: `260827001`
- next one: `260827002`
- next: `260827003`

It counts how many lines on this receipt already have a lot that starts with today’s date. The line being edited is ignored, so editing one line does not jump the number.

The user can still type a different lot number after it is filled. The helper only **suggests** the next number.

---

## When it is generated

Only when the user **picks an item** in the add/edit popup.

That happens in `OnItemSelectedAsync`. A new lot is generated only if the helper returns **NewLot**. That is when:

- the chosen item **uses lot control**, and
- it is **not the same item** already in this popup

| User action | New lot generated? |
|---|---|
| Click **Add line** | No. Popup is empty. Lot is generated later when they pick an item. |
| Pick a **lot-controlled** item on a new line | **Yes** |
| Change to a **different** lot-controlled item | **Yes** |
| Pick an item with **no** lot control | No. Lot is cleared. |
| Edit a line and keep the **same** item | No. Existing lot is kept. |
| Click **Add item / Update item** (save the line) | No. It just copies the lot already in the popup. |
| Click **Save** on the whole receipt | No. It only stores what is already on the lines. |

---

## Simple walkthrough

**Add a lot-controlled item**

- Popup is empty.
- User picks `MILK`.
- Helper says “new lot”.
- Screen fills `260827001`.
- User must still enter expiry date.

**Add a normal item (no lot)**

- User picks `PAPER`.
- Helper says “clear”.
- Lot and expiry stay empty. That is correct.

**Edit an existing line, keep the same item**

- Line already has lot `ABC-99`.
- User opens it, maybe changes quantity, saves.
- Helper says “keep”.
- Lot stays `ABC-99`.

**Edit a line, then change to another lot-controlled item**

- Old item’s lot is still in the box.
- User picks a different item.
- Helper says “new lot”.
- Old lot is replaced, expiry is cleared, user enters a new expiry.

---

## After the user clicks Add / Update

When the line is saved onto the grid:

- If the item **uses lot control** → keep the lot number (and expiry).
- If it **does not** → lot number and expiry are stored blank, even if something was typed.

And before that, if the item uses lot control, the screen checks:

- lot number is filled
- expiry date is filled
- expiry is not before today

---

## Short version

`_lotEntry` remembers the current item in the popup.

- Same item → keep the lot.
- No lot control → clear it.
- New or different lot-controlled item → suggest the next lot number like `260827001`.

The lot number is generated **when the user selects a lot-controlled item** in the popup.
`SelectItem` decides it is needed, then `NextLotNo()` creates `yyMMdd` + `001`, `002`, and so on.
That way lot numbers do not accidentally copy from one item to another.
